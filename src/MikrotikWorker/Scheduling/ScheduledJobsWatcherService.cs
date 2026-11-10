using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Watches ScheduledJobs for CSV files, reconciles missed events, and bounds concurrent ingestion.
/// </summary>
internal sealed class ScheduledJobsWatcherService : BackgroundService
{
    private readonly ScheduledJobsPaths _paths;
    private readonly ScheduledJobFileProcessor _processor;
    private readonly IOptions<ScheduledJobsOptions> _options;
    private readonly ILogger<ScheduledJobsWatcherService> _logger;
    private readonly Channel<string> _discovery;
    private readonly HashSet<string> _pendingKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingGate = new();
    private readonly object _watcherGate = new();
    private FileSystemWatcher? _watcher;
    private CancellationToken _stoppingToken;
    private int _saturationSignals;

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
        EnqueueIncomingScan();

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

    private void EnqueueIncomingScan()
    {
        foreach (string file in Directory.EnumerateFiles(_paths.Root, "*.csv", SearchOption.TopDirectoryOnly))
        {
            TryEnqueuePath(file);
        }
    }

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

    private void CompleteDiscovery(string key)
    {
        lock (_pendingGate)
        {
            _pendingKeys.Remove(key);
        }
    }

    private static string NormalizeKey(string path) =>
        Path.GetFullPath(path);

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

    private void StopWatcher()
    {
        lock (_watcherGate)
        {
            StopWatcherLocked();
        }
    }

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

    private void OnChanged(object sender, FileSystemEventArgs e) => TryEnqueuePath(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => TryEnqueuePath(e.FullPath);

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
