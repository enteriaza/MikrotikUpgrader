// -----------------------------------------------------------------------------
// File: Worker.cs
//
// Description:
// Long-running BackgroundService that emits a periodic heartbeat and logs the
// MikroTik enabled flag. CSV-driven upgrade work is owned by the scheduler and
// router execution hosted services registered in MikrotikWorkerHost; this type
// remains as the generic-host heartbeat / DI seam.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker;

/// <summary>
/// Host heartbeat <see cref="BackgroundService"/>. Device upgrade execution is performed by
/// the scheduled-jobs watcher, <c>InMemoryJobScheduler</c>, and router execution workers —
/// not by this loop.
/// </summary>
public sealed class Worker : BackgroundService
{
    /// <summary>
    /// Logger for heartbeat output.
    /// </summary>
    private readonly ILogger<Worker> _logger;
    /// <summary>
    /// Heartbeat settings.
    /// </summary>
    private readonly WorkerOptions _options;
    /// <summary>
    /// Shared MikroTik options; the worker only reports whether they are enabled.
    /// </summary>
    private readonly MikroTikOptions _mikroTikOptions;
    /// <summary>
    /// API client held so dependency injection and tests can verify it is registered; the heartbeat does not call it.
    /// </summary>
    private readonly IMikrotikApiClient _mikrotikApiClient;

    /// <summary>
    /// Creates the heartbeat worker.
    /// </summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="options">Heartbeat interval options.</param>
    /// <param name="mikroTikOptions">Shared MikroTik enablement/credentials options (logged only here).</param>
    /// <param name="mikrotikApiClient">API client retained for DI/test seam verification.</param>
    public Worker(
        ILogger<Worker> logger,
        IOptions<WorkerOptions> options,
        IOptions<MikroTikOptions> mikroTikOptions,
        IMikrotikApiClient mikrotikApiClient)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mikroTikOptions);
        ArgumentNullException.ThrowIfNull(mikrotikApiClient);

        _logger = logger;
        _options = options.Value;
        _mikroTikOptions = mikroTikOptions.Value;
        _mikrotikApiClient = mikrotikApiClient;
    }

    /// <summary>
    /// Test seam exposing the injected API client without performing device work.
    /// </summary>
    internal IMikrotikApiClient ApiClient => _mikrotikApiClient;

    /// <summary>
    /// Logs startup state and heartbeats until <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker starting");
        _logger.LogInformation(
            "MikrotikWorker started with heartbeat interval {HeartbeatInterval}. MikroTik integration enabled={MikrotikEnabled}",
            _options.HeartbeatInterval,
            _mikroTikOptions.Enabled);

        if (!_mikroTikOptions.Enabled)
        {
            _logger.LogInformation(
                "MikroTik integration is disabled. No device connections will be attempted until enabled and a device workload is configured.");
        }
        else
        {
            _logger.LogInformation(
                "MikroTik integration is enabled, but no device workload is configured yet. Remaining idle until CSV-driven jobs are introduced.");
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Device jobs will be scheduled here from a future CSV-driven workload.
                // Do not invent routers or open connections without explicit device parameters.
                _logger.LogInformation("Worker heartbeat at {Timestamp}", DateTimeOffset.UtcNow);
                await Task.Delay(_options.HeartbeatInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }
        finally
        {
            _logger.LogInformation("Worker stopping");
            _logger.LogInformation("MikrotikWorker stopped");
        }
    }
}
