// -----------------------------------------------------------------------------
// File: DeviceLogWriter.cs
//
// Description:
// HostedService bounded-channel writer for per-device log appends and pending→identity
// promotions with a redirect map so late writes cannot orphan lines.
//
// -----------------------------------------------------------------------------

using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace MikrotikWorker.Logging;

/// <summary>
/// Bounded async writer that appends device-scoped log lines without retaining a handle per device.
/// A single consumer serializes all device-file writes. Pending→identity promotion is queued on the
/// same channel and records a redirect so late writes to the pending key cannot orphan events.
/// </summary>
internal sealed class DeviceLogWriter : IHostedService, IDisposable
{
    /// <summary>
    /// Device-log settings, including enablement, directory, and queue behaviour.
    /// </summary>
    private readonly SerilogOptions _options;
    /// <summary>
    /// Bounded queue of append and promote work consumed by a single reader.
    /// </summary>
    private readonly Channel<DeviceLogWorkItem> _channel;
    /// <summary>
    /// Lock serializing file writes and access to the redirect map.
    /// </summary>
    private readonly object _writeGate = new();
    /// <summary>
    /// Maps promoted pending keys to their identity keys so late writes reach the identity file. Guarded by <c>_writeGate</c>.
    /// </summary>
    private readonly Dictionary<string, string> _keyRedirects = new(StringComparer.Ordinal);
    /// <summary>
    /// Formats log events using the device file output template.
    /// </summary>
    private readonly MessageTemplateTextFormatter _formatter = new(
        SerilogConfigurator.DeviceFileOutputTemplate,
        formatProvider: null);
    /// <summary>
    /// Background task that drains the channel; null until started.
    /// </summary>
    private Task? _consumer;
    /// <summary>
    /// Set to 1 once stopping begins; read with volatile semantics.
    /// </summary>
    private int _stopped;
    /// <summary>
    /// Number of failed appends or promotions, exposed by <see cref="WriteFailureCount"/>.
    /// </summary>
    private long _writeFailureCount;

    /// <summary>
    /// Creates the writer using <c>Logging:Serilog</c> device-log options.
    /// </summary>
    public DeviceLogWriter(IOptions<SerilogOptions> options)
    {
        _options = options.Value;
        BoundedChannelOptions channelOptions = new(Math.Max(64, _options.DeviceLogAsyncCapacity))
        {
            // Wait applies to WriteAsync / WaitToWriteAsync — not TryWrite.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        };
        _channel = Channel.CreateBounded<DeviceLogWorkItem>(channelOptions);
    }

    /// <summary>Count of append/promote failures observed by the consumer.</summary>
    public long WriteFailureCount => Interlocked.Read(ref _writeFailureCount);

    /// <summary>Absolute directory for per-device log files.</summary>
    public string DeviceLogDirectory =>
        Path.IsPathRooted(_options.DeviceLogDirectory)
            ? Path.GetFullPath(_options.DeviceLogDirectory)
            : Path.GetFullPath(Path.Combine(_options.GetAbsoluteLogDirectory(), _options.DeviceLogDirectory));

    /// <summary>Formats and enqueues a structured log event for <paramref name="deviceLogKey"/>.</summary>
    public void Emit(string deviceLogKey, LogEvent logEvent)
    {
        if (!_options.DeviceLogEnabled || string.IsNullOrWhiteSpace(deviceLogKey) || Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        using StringWriter writer = new();
        _formatter.Format(logEvent, writer);
        EnqueueOrWrite(new DeviceLogWorkItem.Append(deviceLogKey, writer.ToString()));
    }

    /// <summary>Enqueues raw text (e.g. session header) for <paramref name="deviceLogKey"/>.</summary>
    public void WriteRaw(string deviceLogKey, string text)
    {
        if (!_options.DeviceLogEnabled || string.IsNullOrWhiteSpace(deviceLogKey) || Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        EnqueueOrWrite(new DeviceLogWorkItem.Append(deviceLogKey, text));
    }

    /// <summary>
    /// Moves any pending pre-identity log content into the identity-based file, then deletes the pending file.
    /// Queued on the same consumer as appends so ordering is preserved; a redirect map catches late writes.
    /// </summary>
    public void PromoteKey(string fromKey, string toKey)
    {
        if (!_options.DeviceLogEnabled
            || string.IsNullOrWhiteSpace(fromKey)
            || string.IsNullOrWhiteSpace(toKey)
            || string.Equals(fromKey, toKey, StringComparison.Ordinal))
        {
            return;
        }

        if (Volatile.Read(ref _stopped) != 0)
        {
            // Channel may already be completing; report so operators know pending content may remain.
            Log.Warning(
                "Device log promotion skipped because the writer is stopping. From={FromKey}, To={ToKey}",
                fromKey,
                toKey);
            return;
        }

        EnqueueOrWrite(new DeviceLogWorkItem.Promote(fromKey, toKey));
    }

    /// <summary>
    /// Test seam: resolves the active file key after any promotions.
    /// </summary>
    internal string ResolveRedirectedKey(string deviceLogKey)
    {
        lock (_writeGate)
        {
            return ResolveActiveKey_NoLock(deviceLogKey);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DeviceLogDirectory);
        // Consumer drains until the channel is completed; do not cancel mid-drain on stop.
        _consumer = Task.Run(ConsumeAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Completes the channel and drains the consumer; briefly waits if host cancellation interrupts.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        if (_consumer is not null)
        {
            try
            {
                await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Host stop cancelled while draining; best-effort continue wait briefly.
                try
                {
                    await _consumer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Device log consumer did not finish draining before shutdown wait expired");
                }
            }
        }
    }

    /// <summary>Synchronously stops the writer by draining with a non-cancellable token.</summary>
    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Tries a non-blocking enqueue; when the channel is full, either writes synchronously
    /// (if not blocking) or waits for capacity.
    /// </summary>
    private void EnqueueOrWrite(DeviceLogWorkItem item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            return;
        }

        if (!_options.DeviceLogAsyncBlockWhenFull)
        {
            try
            {
                ProcessItem(item);
            }
            catch (Exception ex)
            {
                RecordWriteFailure(item.PrimaryKey, ex);
            }

            return;
        }

        try
        {
            while (Volatile.Read(ref _stopped) == 0)
            {
                ValueTask<bool> wait = _channel.Writer.WaitToWriteAsync();
                if (!wait.IsCompletedSuccessfully)
                {
                    wait.AsTask().GetAwaiter().GetResult();
                }
                else if (!wait.Result)
                {
                    return;
                }

                if (_channel.Writer.TryWrite(item))
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                ProcessItem(item);
            }
            catch (Exception writeEx)
            {
                RecordWriteFailure(item.PrimaryKey, writeEx);
                Log.Warning(ex, "Device log enqueue failed for {DeviceLogKey}", item.PrimaryKey);
            }
        }
    }

    /// <summary>Single consumer that serializes all append and promote work items.</summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (DeviceLogWorkItem item in _channel.Reader.ReadAllAsync(CancellationToken.None)
                               .ConfigureAwait(false))
            {
                try
                {
                    ProcessItem(item);
                }
                catch (Exception ex)
                {
                    RecordWriteFailure(item.PrimaryKey, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Device log consumer terminated unexpectedly");
        }
    }

    /// <summary>Dispatches an append or promote work item under the write gate as needed.</summary>
    private void ProcessItem(DeviceLogWorkItem item)
    {
        switch (item)
        {
            case DeviceLogWorkItem.Append append:
                WriteLine(append.DeviceLogKey, append.Line);
                break;
            case DeviceLogWorkItem.Promote promote:
                PromoteFiles(promote.FromKey, promote.ToKey);
                break;
        }
    }

    /// <summary>Appends <paramref name="line"/> to the redirected active file for <paramref name="deviceLogKey"/>.</summary>
    private void WriteLine(string deviceLogKey, string line)
    {
        lock (_writeGate)
        {
            string activeKey = ResolveActiveKey_NoLock(deviceLogKey);
            string path = ResolvePath(activeKey);
            Directory.CreateDirectory(DeviceLogDirectory);
            using FileStream stream = new(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using StreamWriter writer = new(stream, Encoding.UTF8);
            writer.Write(line);
            writer.Flush();
        }
    }

    /// <summary>
    /// Registers a redirect, appends pending content into the identity file, then deletes the pending file.
    /// </summary>
    private void PromoteFiles(string fromKey, string toKey)
    {
        lock (_writeGate)
        {
            // Register redirect first so concurrent/sync-fallback writes to fromKey land on toKey.
            _keyRedirects[fromKey] = toKey;

            string fromPath = ResolvePath(fromKey);
            string toPath = ResolvePath(toKey);
            if (!File.Exists(fromPath))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(DeviceLogDirectory);
                string pendingContent = File.ReadAllText(fromPath, Encoding.UTF8);
                if (!string.IsNullOrEmpty(pendingContent))
                {
                    // Append only — never truncate an existing identity log.
                    File.AppendAllText(toPath, pendingContent, Encoding.UTF8);
                }

                File.Delete(fromPath);
            }
            catch (Exception ex)
            {
                RecordWriteFailure(fromKey, ex);
                Log.Warning(
                    ex,
                    "Failed to promote device log from {FromKey} to {ToKey}",
                    fromKey,
                    toKey);
            }
        }
    }

    /// <summary>Follows the redirect map (bounded) to the current file key. Caller must hold the write gate.</summary>
    private string ResolveActiveKey_NoLock(string deviceLogKey)
    {
        string current = deviceLogKey;
        // Guard against accidental cycles.
        for (int i = 0; i < 8; i++)
        {
            if (!_keyRedirects.TryGetValue(current, out string? next)
                || string.Equals(current, next, StringComparison.Ordinal))
            {
                break;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Maps a device log key to a safe path under <see cref="DeviceLogDirectory"/>.</summary>
    private string ResolvePath(string deviceLogKey)
    {
        string safeName = deviceLogKey;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(c, '_');
        }

        safeName = safeName.Replace(':', '_').Replace('*', '_').Replace('?', '_').Replace('"', '_');
        return Path.Combine(DeviceLogDirectory, Path.GetFileName(safeName));
    }

    /// <summary>Increments <see cref="WriteFailureCount"/> and warns on the service logger (no DeviceLogKey).</summary>
    private void RecordWriteFailure(string deviceLogKey, Exception ex)
    {
        Interlocked.Increment(ref _writeFailureCount);
        // Main application log only (no DeviceLogKey) to avoid recursive device-sink emission.
        Log.Warning(ex, "Failed to append device log for {DeviceLogKey}", deviceLogKey);
    }

    /// <summary>Channel payload for serialized device-log I/O.</summary>
    private abstract record DeviceLogWorkItem(string PrimaryKey)
    {
        /// <summary>Append formatted or raw text to a device log key.</summary>
        public sealed record Append(string DeviceLogKey, string Line) : DeviceLogWorkItem(DeviceLogKey);

        /// <summary>Promote pending content from <paramref name="FromKey"/> into <paramref name="ToKey"/>.</summary>
        public sealed record Promote(string FromKey, string ToKey) : DeviceLogWorkItem(FromKey);
    }
}
