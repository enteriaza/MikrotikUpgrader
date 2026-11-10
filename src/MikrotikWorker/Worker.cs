using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker;

/// <summary>
/// Background worker host loop. MikroTik device jobs will be driven by future CSV workloads.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly WorkerOptions _options;
    private readonly MikroTikOptions _mikroTikOptions;
    private readonly IMikrotikApiClient _mikrotikApiClient;

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

    // Expose for tests that verify the DI seam without triggering device work.
    internal IMikrotikApiClient ApiClient => _mikrotikApiClient;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            _logger.LogInformation("MikrotikWorker stopped");
        }
    }
}
