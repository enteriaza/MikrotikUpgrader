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
/// A single consumer serializes all device-file writes.
/// </summary>
internal sealed class DeviceLogWriter : IHostedService, IDisposable
{
    private readonly SerilogOptions _options;
    private readonly Channel<DeviceLogEntry> _channel;
    private readonly object _writeGate = new();
    private readonly MessageTemplateTextFormatter _formatter = new(
        SerilogConfigurator.FileOutputTemplate,
        formatProvider: null);
    private Task? _consumer;
    private int _stopped;
    private long _writeFailureCount;

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
        _channel = Channel.CreateBounded<DeviceLogEntry>(channelOptions);
    }

    public long WriteFailureCount => Interlocked.Read(ref _writeFailureCount);

    public string DeviceLogDirectory =>
        Path.IsPathRooted(_options.DeviceLogDirectory)
            ? Path.GetFullPath(_options.DeviceLogDirectory)
            : Path.GetFullPath(Path.Combine(_options.GetAbsoluteLogDirectory(), _options.DeviceLogDirectory));

    public void Emit(string deviceLogKey, LogEvent logEvent)
    {
        if (!_options.DeviceLogEnabled || string.IsNullOrWhiteSpace(deviceLogKey) || Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        using StringWriter writer = new();
        _formatter.Format(logEvent, writer);
        DeviceLogEntry entry = new(deviceLogKey, writer.ToString());

        if (_channel.Writer.TryWrite(entry))
        {
            return;
        }

        if (!_options.DeviceLogAsyncBlockWhenFull)
        {
            // Configured to avoid blocking the logging caller: fall back to synchronous append
            // so the event is not silently dropped.
            try
            {
                WriteEntry(entry);
            }
            catch (Exception ex)
            {
                RecordWriteFailure(entry.DeviceLogKey, ex);
            }

            return;
        }

        try
        {
            // Backpressure: wait for capacity, then write (Serilog sinks are synchronous).
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

                if (_channel.Writer.TryWrite(entry))
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                WriteEntry(entry);
            }
            catch (Exception writeEx)
            {
                RecordWriteFailure(entry.DeviceLogKey, writeEx);
                Log.Warning(ex, "Device log enqueue failed for {DeviceLogKey}", entry.DeviceLogKey);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DeviceLogDirectory);
        // Consumer drains until the channel is completed; do not cancel mid-drain on stop.
        _consumer = Task.Run(ConsumeAsync);
        return Task.CompletedTask;
    }

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
                catch
                {
                }
            }
        }
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (DeviceLogEntry entry in _channel.Reader.ReadAllAsync(CancellationToken.None)
                               .ConfigureAwait(false))
            {
                try
                {
                    WriteEntry(entry);
                }
                catch (Exception ex)
                {
                    RecordWriteFailure(entry.DeviceLogKey, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Device log consumer terminated unexpectedly");
        }
    }

    private void WriteEntry(DeviceLogEntry entry)
    {
        // Serialize consumer and sync-fallback writers so BlockWhenFull=false cannot race the same file.
        lock (_writeGate)
        {
            string safeName = entry.DeviceLogKey;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(c, '_');
            }

            // Also replace characters invalid on Linux when running on Windows helpers, and vice versa.
            safeName = safeName.Replace(':', '_').Replace('*', '_').Replace('?', '_').Replace('"', '_');

            string path = Path.Combine(DeviceLogDirectory, Path.GetFileName(safeName));
            Directory.CreateDirectory(DeviceLogDirectory);
            using FileStream stream = new(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using StreamWriter writer = new(stream, Encoding.UTF8);
            writer.Write(entry.Line);
            writer.Flush();
        }
    }

    private void RecordWriteFailure(string deviceLogKey, Exception ex)
    {
        Interlocked.Increment(ref _writeFailureCount);
        // Main application log only (no DeviceLogKey) to avoid recursive device-sink emission.
        Log.Warning(ex, "Failed to append device log for {DeviceLogKey}", deviceLogKey);
    }

    private readonly record struct DeviceLogEntry(string DeviceLogKey, string Line);
}
