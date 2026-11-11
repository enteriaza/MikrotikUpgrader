using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Immutable RouterOS 7 target snapshot captured for a single upgrade job.
/// </summary>
public sealed class RouterOsV7TargetSnapshot
{
    public required RouterOsVersion Version { get; init; }

    public required string VersionText { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

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
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(50);
    private const int ReadRetryAttempts = 8;

    private readonly IOptions<FirmwareFilesOptions> _firmwareFiles;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RouterOsV7TargetProvider> _logger;
    private readonly object _gate = new();
    private readonly Channel<string> _reloadSignals = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private RouterOsV7TargetSnapshot? _current;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _consumer;
    private int _disposed;

    public RouterOsV7TargetProvider(
        IOptions<FirmwareFilesOptions> firmwareFiles,
        TimeProvider timeProvider,
        ILogger<RouterOsV7TargetProvider> logger)
    {
        _firmwareFiles = firmwareFiles;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string VersionFilePath =>
        Path.Combine(_firmwareFiles.Value.GetAbsolutePath(), "mikrotik", "version");

    public RouterOsV7TargetSnapshot? TryGetCurrent()
    {
        lock (_gate)
        {
            return _current;
        }
    }

    public RouterOsV7TargetSnapshot? CaptureSnapshot() => TryGetCurrent();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VersionFilePath)!);
        TryLoad(reason: "startup", requireSuccessLog: true);
        StartWatcher();
        _cts = new CancellationTokenSource();
        _consumer = Task.Run(() => ConsumeReloadSignalsAsync(_cts.Token));
        return Task.CompletedTask;
    }

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

    private void OnWatcherEvent(object sender, FileSystemEventArgs e) =>
        _reloadSignals.Writer.TryWrite(e.ChangeType.ToString());

    private void OnWatcherRenamed(object sender, RenamedEventArgs e) =>
        _reloadSignals.Writer.TryWrite("Renamed");

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
