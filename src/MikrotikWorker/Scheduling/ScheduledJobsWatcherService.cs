// -----------------------------------------------------------------------------
// File: ScheduledJobsWatcherService.cs
//
// Description:
// Hosted watcher for ScheduledJobs Incoming CSVs: FileSystemWatcher events plus
// periodic reconciliation feed a bounded discovery channel drained by a fixed
// pool of ingestion workers. Coalesces duplicate paths, recovers from watcher
// errors, and warns about leftover Processing files at startup.
//
// -----------------------------------------------------------------------------

using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Watches ScheduledJobs for CSV files, reconciles missed events, and bounds concurrent ingestion.
/// </summary>
internal sealed class ScheduledJobsWatcherService : BackgroundService
{
    /// <summary>
    /// Resolved ScheduledJobs folder layout.
    /// </summary>
    private readonly ScheduledJobsPaths _paths;
    /// <summary>
    /// Ingests each discovered CSV.
    /// </summary>
    private readonly ScheduledJobFileProcessor _processor;
    /// <summary>
    /// Scan interval, worker count, and queue capacity settings.
    /// </summary>
    private readonly IOptions<ScheduledJobsOptions> _options;
    /// <summary>
    /// Logger for watcher and ingestion diagnostics.
    /// </summary>
    private readonly ILogger<ScheduledJobsWatcherService> _logger;
    /// <summary>
    /// Bounded channel of discovered CSV paths drained by the ingestion workers.
    /// </summary>
    private readonly Channel<string> _discovery;
    /// <summary>
    /// Normalised paths already queued or being ingested, used to coalesce duplicates. Guarded by <c>_pendingGate</c>.
    /// </summary>
    private readonly HashSet<string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Lock guarding <c>_pendingKeys</c>.
    /// </summary>
    private readonly object _pendingGate = new();
    /// <summary>
    /// Lock guarding creation and disposal of <c>_watcher</c>.
    /// </summary>
    private readonly object _watcherGate = new();
    /// <summary>
    /// Current file system watcher, or null when stopped. Guarded by <c>_watcherGate</c>.
    /// </summary>
    private FileSystemWatcher? _watcher;
    /// <summary>
    /// Host stopping token captured in <see cref="ExecuteAsync"/>, used to suppress watcher recreation during shutdown.
    /// </summary>
    private CancellationToken _stoppingToken;
    /// <summary>
    /// Running count of times the discovery queue was full, used in warning logs.
    /// </summary>
    private int _saturationSignals;

    /// <summary>
    /// Creates the watcher service with a bounded discovery channel sized by
    /// <see cref="ScheduledJobsOptions.DiscoveryQueueCapacity"/>.
    /// </summary>
    public ScheduledJobsWatcherService(
        ScheduledJobsPaths paths,
        ScheduledJobFileProcessor processor,
        IOptions<ScheduledJobsOptions> options,
        ILogger<ScheduledJobsWatcherService> logger)
    {
        _paths = paths;
        _processor = processor;
        _options = options;
        _logger = logger;
        _discovery = Channel.CreateBounded<string>(new BoundedChannelOptions(options.Value.DiscoveryQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Ensures folders, warns about Processing leftovers, starts watcher + ingestion workers,
    /// and periodically rescans Incoming until shutdown.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _paths.EnsureCreated();
        _logger.LogInformation(
            "ScheduledJobs watcher started. Root={Root}, ScanInterval={ScanInterval}, MaxConcurrentCsvImports={MaxConcurrentCsvImports}, DiscoveryQueueCapacity={DiscoveryQueueCapacity}",
            _paths.Root,
            _options.Value.ScanInterval,
            _options.Value.MaxConcurrentCsvImports,
            _options.Value.DiscoveryQueueCapacity);

        _processor.WarnAboutInterruptedProcessingFiles();
        _logger.LogInformation("Initial CSV reconciliation starting. Root={Root}", _paths.Root);
        EnqueueIncomingScan();
        _logger.LogInformation("Initial CSV reconciliation completed. Root={Root}", _paths.Root);

        StartWatcher();

        Task[] workers = Enumerable
            .Range(0, _options.Value.MaxConcurrentCsvImports)
            .Select(_ => RunIngestionWorkerAsync(stoppingToken))
            .ToArray();

        using PeriodicTimer timer = new(_options.Value.ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                EnqueueIncomingScan();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        finally
        {
            _discovery.Writer.TryComplete();
            StopWatcher();
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }

            _logger.LogInformation("ScheduledJobs watcher stopped");
        }
    }

    /// <summary>
    /// Drains the discovery channel and invokes <see cref="ScheduledJobFileProcessor.ProcessIncomingAsync"/>
    /// for paths that still look like Incoming CSVs; always clears the pending key afterward.
    /// </summary>
    private async Task RunIngestionWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (string path in _discovery.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                string key = NormalizeKey(path);
                try
                {
                    if (!IsIncomingCsv(path))
                    {
                        continue;
                    }

                    await _processor.ProcessIncomingAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error processing watched CSV {Path}", path);
                }
                finally
                {
                    CompleteDiscovery(key);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    /// <summary>Enqueues every top-level <c>*.csv</c> under the ScheduledJobs root.</summary>
    private void EnqueueIncomingScan()
    {
        foreach (string file in Directory.EnumerateFiles(_paths.Root, "*.csv", SearchOption.TopDirectoryOnly))
        {
            TryEnqueuePath(file);
        }
    }

    /// <summary>
    /// Coalesces by full path into the discovery channel. On channel saturation, drops the
    /// pending reservation so periodic reconciliation can retry.
    /// </summary>
    /// <returns><c>true</c> when enqueued or already pending; <c>false</c> when saturated.</returns>
    private bool TryEnqueuePath(string path)
    {
        string key = NormalizeKey(path);
        lock (_pendingGate)
        {
            if (!_pendingKeys.Add(key))
            {
                return true; // coalesced
            }
        }

        if (_discovery.Writer.TryWrite(path))
        {
            return true;
        }

        // Bounded channel is full: drop reservation so reconciliation can retry.
        lock (_pendingGate)
        {
            _pendingKeys.Remove(key);
        }

        int saturations = Interlocked.Increment(ref _saturationSignals);
        _logger.LogWarning(
            "Discovery queue saturated (capacity {Capacity}); deferred {Path}. SaturationCount={SaturationCount}. Periodic reconciliation will retry.",
            _options.Value.DiscoveryQueueCapacity,
            path,
            saturations);
        return false;
    }

    /// <summary>Removes a coalescing key after an ingestion attempt finishes.</summary>
    private void CompleteDiscovery(string key)
    {
        lock (_pendingGate)
        {
            _pendingKeys.Remove(key);
        }
    }

    /// <summary>Full-path key used for pending coalescing.</summary>
    private static string NormalizeKey(string path) =>
        Path.GetFullPath(path);

    /// <summary>Creates/replaces the FileSystemWatcher for root <c>*.csv</c> create/change/rename events.</summary>
    private void StartWatcher()
    {
        lock (_watcherGate)
        {
            if (_stoppingToken.IsCancellationRequested)
            {
                return;
            }

            StopWatcherLocked();

            FileSystemWatcher watcher = new(_paths.Root, "*.csv")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;

            _watcher = watcher;
            _logger.LogInformation("FileSystemWatcher listening for CSV files in {Root}", _paths.Root);
        }
    }

    /// <summary>Disposes the current watcher under <c>_watcherGate</c>.</summary>
    private void StopWatcher()
    {
        lock (_watcherGate)
        {
            StopWatcherLocked();
        }
    }

    /// <summary>Unsubscribes and disposes <c>_watcher</c>; caller must hold <c>_watcherGate</c>.</summary>
    private void StopWatcherLocked()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnChanged;
        _watcher.Changed -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>Created/Changed handler → discovery enqueue.</summary>
    private void OnChanged(object sender, FileSystemEventArgs e) => TryEnqueuePath(e.FullPath);

    /// <summary>Renamed handler → discovery enqueue of the new path.</summary>
    private void OnRenamed(object sender, RenamedEventArgs e) => TryEnqueuePath(e.FullPath);

    /// <summary>Recreates the watcher after an error unless shutdown is in progress.</summary>
    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error; recreating watcher");
        lock (_watcherGate)
        {
            if (_stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Skipping FileSystemWatcher recreation because shutdown is in progress");
                return;
            }
        }

        try
        {
            StartWatcher();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recreate FileSystemWatcher");
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is an existing <c>.csv</c> directly under the ScheduledJobs root.
    /// </summary>
    private bool IsIncomingCsv(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        string root = Path.GetFullPath(_paths.Root);
        string? directory = Path.GetDirectoryName(fullPath);
        return directory is not null
               && string.Equals(directory, root, StringComparison.OrdinalIgnoreCase)
               && File.Exists(fullPath);
    }
}
