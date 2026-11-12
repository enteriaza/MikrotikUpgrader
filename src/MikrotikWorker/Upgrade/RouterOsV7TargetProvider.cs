// -----------------------------------------------------------------------------
// File: RouterOsV7TargetProvider.cs
//
// Description:
// Loads and watches {FirmwareFiles}/mikrotik/version for the RouterOS 7
// target. Invalid updates never replace the last known good value; deletion
// keeps the last good value. Path segment casing matches the on-disk firmware
// catalogue folder (mikrotik).
//
// -----------------------------------------------------------------------------

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Immutable RouterOS 7 target snapshot captured for a single upgrade job.
/// </summary>
public sealed class RouterOsV7TargetSnapshot
{
    /// <summary>Parsed RouterOS 7 target version used for package resolution and comparisons.</summary>
    public required RouterOsVersion Version { get; init; }

    /// <summary>Original trimmed text from the version file.</summary>
    public required string VersionText { get; init; }

    /// <summary>UTC time when this snapshot was captured for a job or reload.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Absolute path of the version file that produced this snapshot.</summary>
    public required string SourcePath { get; init; }
}

/// <summary>
/// Thread-safe provider for the validated RouterOS 7 target from
/// <c>FirmwareFiles/mikrotik/version</c>.
/// </summary>
public interface IRouterOsV7TargetProvider
{
    /// <summary>
    /// Absolute path of the version file.
    /// </summary>
    string VersionFilePath { get; }

    /// <summary>
    /// Returns the current validated target, or <c>null</c> when none has been loaded successfully.
    /// </summary>
    RouterOsV7TargetSnapshot? TryGetCurrent();

    /// <summary>
    /// Captures an immutable snapshot for a job. Returns <c>null</c> when no valid target is available.
    /// </summary>
    RouterOsV7TargetSnapshot? CaptureSnapshot();
}

/// <summary>
/// Loads and watches <c>{FirmwareFiles}/mikrotik/version</c> for the RouterOS 7 target.
/// Invalid updates never replace the last known good value; deletion keeps the last good value.
/// Path segment casing matches the on-disk firmware catalogue folder (<c>mikrotik</c>).
/// </summary>
internal sealed class RouterOsV7TargetProvider : IRouterOsV7TargetProvider, IHostedService, IDisposable
{
    /// <summary>Quiet period after a file-system event before reloading, so bursts of change notifications (and partially written files) collapse into one reload.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    /// <summary>Delay between attempts to read the version file when it is locked or being written.</summary>
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(50);
    /// <summary>Maximum number of attempts to read the version file before reporting it unreadable.</summary>
    private const int ReadRetryAttempts = 8;

    /// <summary>Firmware catalogue options; supply the root under which <c>mikrotik/version</c> lives.</summary>
    private readonly IOptions<FirmwareFilesOptions> _firmwareFiles;
    /// <summary>Clock used for snapshot timestamps and the debounce delay.</summary>
    private readonly TimeProvider _timeProvider;
    /// <summary>Logger for load, rejection and watcher events.</summary>
    private readonly ILogger<RouterOsV7TargetProvider> _logger;
    /// <summary>Guards <c>_current</c> so snapshots are replaced and read atomically.</summary>
    private readonly object _gate = new();
    /// <summary>Unbounded channel of reload reasons written by watcher events; a single consumer debounces and reloads.</summary>
    private readonly Channel<string> _reloadSignals = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>Last known good snapshot; null until a valid version file has been loaded. Guarded by <c>_gate</c>.</summary>
    private RouterOsV7TargetSnapshot? _current;
    /// <summary>Watcher on the version file's folder; null when stopped or disposed.</summary>
    private FileSystemWatcher? _watcher;
    /// <summary>Cancels the reload consumer; null before start or after disposal.</summary>
    private CancellationTokenSource? _cts;
    /// <summary>Background task that debounces watcher signals and reloads the version.</summary>
    private Task? _consumer;
    /// <summary>Set to 1 once disposed so disposal runs only once (accessed via <see cref="Interlocked"/>).</summary>
    private int _disposed;

    /// <summary>
    /// Creates the provider; no file is read until <see cref="StartAsync"/> runs.
    /// </summary>
    /// <param name="firmwareFiles">Firmware catalogue options supplying the version file location.</param>
    /// <param name="timeProvider">Clock for snapshot timestamps and debounce delays.</param>
    /// <param name="logger">Logger for load and watcher events.</param>
    public RouterOsV7TargetProvider(
        IOptions<FirmwareFilesOptions> firmwareFiles,
        TimeProvider timeProvider,
        ILogger<RouterOsV7TargetProvider> logger)
    {
        _firmwareFiles = firmwareFiles;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Absolute path of <c>mikrotik/version</c> beneath the configured firmware files folder (recomputed on each access).</summary>
    public string VersionFilePath =>
        Path.Combine(_firmwareFiles.Value.GetAbsolutePath(), "mikrotik", "version");

    /// <summary>Returns the last known good snapshot under the lock, or null when no valid version has been loaded.</summary>
    public RouterOsV7TargetSnapshot? TryGetCurrent()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    /// <summary>Returns the current snapshot; the snapshot is immutable so callers can use it for the whole job without further synchronisation.</summary>
    public RouterOsV7TargetSnapshot? CaptureSnapshot() => TryGetCurrent();

    /// <summary>Ensures the version folder exists, performs the initial load, starts the file watcher and starts the background reload consumer. A missing or invalid file is logged but does not prevent startup.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VersionFilePath)!);
        TryLoad(reason: "startup", requireSuccessLog: true);
        StartWatcher();
        _cts = new CancellationTokenSource();
        _consumer = Task.Run(() => ConsumeReloadSignalsAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>Cancels the reload consumer, stops the watcher, completes the reload channel and waits for the consumer to finish; cancellation and channel-closed exceptions are swallowed.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts = _cts;
        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        StopWatcher();
        _reloadSignals.Writer.TryComplete();
        if (_consumer is not null)
        {
            try
            {
                await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    /// <summary>Stops the watcher, cancels and disposes the consumer token source and completes the reload channel. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopWatcher();
        try
        {
            CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
            if (cts is not null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                cts.Dispose();
            }
        }
        catch
        {
        }

        _reloadSignals.Writer.TryComplete();
    }

    /// <summary>
    /// Test seam: force a synchronous reload without waiting for the watcher consumer.
    /// </summary>
    internal bool TryReloadForTests(string reason = "test") => TryLoad(reason, requireSuccessLog: false);

    /// <summary>Reads reload reasons from the channel, waits out the debounce period, discards signals that arrived meanwhile and reloads. Ends quietly on cancellation.</summary>
    private async Task ConsumeReloadSignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (string reason in _reloadSignals.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await Task.Delay(Debounce, _timeProvider, cancellationToken).ConfigureAwait(false);
                // Drain coalesced signals.
                while (_reloadSignals.Reader.TryRead(out _))
                {
                }

                TryLoad(reason, requireSuccessLog: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Loads and validates the version file and, when valid and different, replaces the current snapshot.
    /// </summary>
    /// <remarks>
    /// A missing file, or an invalid file, never replaces the last known good snapshot; the cause is logged at the level that fits whether a good target already exists.
    /// </remarks>
    /// <param name="reason">Short label recorded in log messages (for example <c>startup</c> or the watcher change type).</param>
    /// <param name="requireSuccessLog">When true a missing file is always logged as an error, even if a previous good target exists.</param>
    /// <returns>True when the file holds a valid target (whether or not it changed the snapshot); false when the file was missing or rejected.</returns>
    private bool TryLoad(string reason, bool requireSuccessLog)
    {
        string path = VersionFilePath;
        if (!File.Exists(path))
        {
            if (requireSuccessLog || TryGetCurrent() is null)
            {
                _logger.LogError(
                    "RouterOS 7 target file is missing ({Path}). RouterOS 7 upgrades cannot start until a valid version is supplied. Reason={Reason}",
                    path,
                    reason);
            }
            else
            {
                _logger.LogWarning(
                    "RouterOS 7 target file was deleted ({Path}); keeping last known good target {Target}. Reason={Reason}",
                    path,
                    TryGetCurrent()!.VersionText,
                    reason);
            }

            return false;
        }

        if (!TryReadValidatedVersion(path, out RouterOsVersion version, out string versionText, out string? error))
        {
            if (TryGetCurrent() is RouterOsV7TargetSnapshot existing)
            {
                _logger.LogWarning(
                    "Rejected RouterOS 7 target file update ({Path}): {Error}. Keeping last known good target {Target}. Reason={Reason}",
                    path,
                    error,
                    existing.VersionText,
                    reason);
            }
            else
            {
                _logger.LogError(
                    "RouterOS 7 target file is invalid ({Path}): {Error}. RouterOS 7 upgrades cannot start until a valid version is supplied. Reason={Reason}",
                    path,
                    error,
                    reason);
            }

            return false;
        }

        RouterOsV7TargetSnapshot snapshot = new()
        {
            Version = version,
            VersionText = versionText,
            CapturedAtUtc = _timeProvider.GetUtcNow(),
            SourcePath = path,
        };

        lock (_gate)
        {
            RouterOsV7TargetSnapshot? previous = _current;
            if (previous is not null
                && previous.Version == snapshot.Version
                && string.Equals(previous.VersionText, snapshot.VersionText, StringComparison.Ordinal))
            {
                return true;
            }

            _current = snapshot;
        }

        _logger.LogInformation(
            "RouterOS 7 target is {Target} (source {Path}, reason {Reason})",
            versionText,
            path,
            reason);
        return true;
    }

    /// <summary>
    /// Reads the version file, tolerating transient locks, and validates its content.
    /// </summary>
    /// <remarks>
    /// The file must hold exactly one whitespace-free line that parses as a RouterOS v7 or later version.
    /// </remarks>
    /// <param name="path">Version file path.</param>
    /// <param name="version">Receives the parsed version when valid.</param>
    /// <param name="versionText">Receives the canonical version text (without channel suffix) when valid.</param>
    /// <param name="error">Receives the rejection reason when invalid; null on success.</param>
    /// <returns>True when a valid RouterOS v7+ version was read.</returns>
    private static bool TryReadValidatedVersion(
        string path,
        out RouterOsVersion version,
        out string versionText,
        out string? error)
    {
        version = default;
        versionText = string.Empty;
        error = null;

        string? raw = null;
        for (int attempt = 1; attempt <= ReadRetryAttempts; attempt++)
        {
            try
            {
                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream);
                raw = reader.ReadToEnd();
                break;
            }
            catch (IOException) when (attempt < ReadRetryAttempts)
            {
                Thread.Sleep(ReadRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < ReadRetryAttempts)
            {
                Thread.Sleep(ReadRetryDelay);
            }
        }

        if (raw is null)
        {
            error = "file could not be read (possibly locked or partially written)";
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            error = "file is empty";
            return false;
        }

        // Single-line version only: reject additional non-whitespace content beyond one token/line.
        string[] lines = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
        {
            error = "file must contain exactly one RouterOS version line";
            return false;
        }

        string candidate = lines[0];
        if (candidate.Contains(' ', StringComparison.Ordinal) || candidate.Contains('\t', StringComparison.Ordinal))
        {
            error = "version line contains unexpected whitespace";
            return false;
        }

        if (!RouterOsVersion.TryParse(candidate, out version) || !version.IsV7OrLater)
        {
            error = "contents are not a valid RouterOS v7+ version";
            return false;
        }

        // Canonical text without channel suffixes for package resolution.
        versionText = version.ToString();
        return true;
    }

    /// <summary>Creates the version folder if needed and starts a watcher for changes, creation, rename and deletion of the <c>version</c> file.</summary>
    private void StartWatcher()
    {
        string directory = Path.GetDirectoryName(VersionFilePath)!;
        Directory.CreateDirectory(directory);
        FileSystemWatcher watcher = new(directory, "version")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };
        watcher.Changed += OnWatcherEvent;
        watcher.Created += OnWatcherEvent;
        watcher.Renamed += OnWatcherRenamed;
        watcher.Deleted += OnWatcherEvent;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
        _logger.LogInformation("Watching RouterOS 7 target file at {Path}", VersionFilePath);
    }

    /// <summary>Detaches handlers and disposes the watcher; safe to call repeatedly or when no watcher exists.</summary>
    private void StopWatcher()
    {
        FileSystemWatcher? watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnWatcherEvent;
            watcher.Created -= OnWatcherEvent;
            watcher.Renamed -= OnWatcherRenamed;
            watcher.Deleted -= OnWatcherEvent;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>Queues a reload request labelled with the file-system change type.</summary>
    private void OnWatcherEvent(object sender, FileSystemEventArgs e) =>
        _reloadSignals.Writer.TryWrite(e.ChangeType.ToString());

    /// <summary>Queues a reload request labelled <c>Renamed</c>.</summary>
    private void OnWatcherRenamed(object sender, RenamedEventArgs e) =>
        _reloadSignals.Writer.TryWrite("Renamed");

    /// <summary>Logs the watcher failure, recreates the watcher and queues a reload so changes missed during the fault are picked up.</summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "RouterOS 7 target FileSystemWatcher error; recreating watcher");
        try
        {
            StopWatcher();
            StartWatcher();
            _reloadSignals.Writer.TryWrite("WatcherRecreated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recreate RouterOS 7 target FileSystemWatcher");
        }
    }
}
