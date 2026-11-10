using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Logging;

/// <summary>
/// Emits application-level service lifecycle events (config summary, ready, stopping)
/// into the production Serilog pipeline without device correlation context.
/// </summary>
internal sealed class ServiceLifecycleLoggingHostedService : IHostedService
{
    private readonly ILogger<ServiceLifecycleLoggingHostedService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IOptions<ListeningOptions> _listening;
    private readonly IOptions<FirmwareFilesOptions> _firmwareFiles;
    private readonly IOptions<ScheduledJobsOptions> _scheduledJobs;
    private readonly ScheduledJobsPaths _scheduledJobsPaths;
    private readonly IOptions<MikroTikOptions> _mikroTik;
    private readonly IOptions<FirmwareUpgradeOptions> _firmwareUpgrade;
    private readonly IOptions<FirmwareAssessmentOptions> _assessment;
    private readonly IOptions<WorkerOptions> _worker;
    private readonly IOptions<SerilogOptions> _serilog;
    private readonly IHostEnvironment _environment;
    private CancellationTokenRegistration _startedRegistration;
    private CancellationTokenRegistration _stoppingRegistration;

    public ServiceLifecycleLoggingHostedService(
        ILogger<ServiceLifecycleLoggingHostedService> logger,
        IHostApplicationLifetime lifetime,
        IOptions<ListeningOptions> listening,
        IOptions<FirmwareFilesOptions> firmwareFiles,
        IOptions<ScheduledJobsOptions> scheduledJobs,
        ScheduledJobsPaths scheduledJobsPaths,
        IOptions<MikroTikOptions> mikroTik,
        IOptions<FirmwareUpgradeOptions> firmwareUpgrade,
        IOptions<FirmwareAssessmentOptions> assessment,
        IOptions<WorkerOptions> worker,
        IOptions<SerilogOptions> serilog,
        IHostEnvironment environment)
    {
        _logger = logger;
        _lifetime = lifetime;
        _listening = listening;
        _firmwareFiles = firmwareFiles;
        _scheduledJobs = scheduledJobs;
        _scheduledJobsPaths = scheduledJobsPaths;
        _mikroTik = mikroTik;
        _firmwareUpgrade = firmwareUpgrade;
        _assessment = assessment;
        _worker = worker;
        _serilog = serilog;
        _environment = environment;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Hosted services starting");
        LogConfigurationSummary();

        _startedRegistration = _lifetime.ApplicationStarted.Register(static state =>
        {
            ((ServiceLifecycleLoggingHostedService)state!).OnApplicationStarted();
        }, this);

        _stoppingRegistration = _lifetime.ApplicationStopping.Register(static state =>
        {
            ((ServiceLifecycleLoggingHostedService)state!).OnApplicationStopping();
        }, this);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Hosted services stopping");
        _startedRegistration.Dispose();
        _stoppingRegistration.Dispose();
        _logger.LogInformation("Application shutdown completed");
        return Task.CompletedTask;
    }

    private void OnApplicationStarted()
    {
        // Actual listen addresses are emitted by Microsoft.Hosting.Lifecycle ("Now listening on: ...").
        _logger.LogInformation("Application ready");
    }

    private void OnApplicationStopping()
    {
        _logger.LogInformation("Application stopping");
    }

    private void LogConfigurationSummary()
    {
        // Never log credentials, tokens, or backup passwords.
        _logger.LogInformation(
            "Application configuration summary. Environment={EnvironmentName}, Listening={IpAddress}:{Port}, FirmwareFilesPath={FirmwareFilesPath}, ScheduledJobsPath={ScheduledJobsPath}, MikroTikEnabled={MikroTikEnabled}, MikroTikPort={MikroTikPort}, FirmwareUpgradeEnabled={FirmwareUpgradeEnabled}, TargetRouterOsVersion={TargetRouterOsVersion}, MaxConcurrentRouterJobs={MaxConcurrentRouterJobs}, MaxConcurrentCsvImports={MaxConcurrentCsvImports}, HeartbeatInterval={HeartbeatInterval}, LogDirectory={LogDirectory}, DeviceLogEnabled={DeviceLogEnabled}",
            _environment.EnvironmentName,
            _listening.Value.IpAddress,
            _listening.Value.Port,
            _firmwareFiles.Value.GetAbsolutePath(),
            string.IsNullOrWhiteSpace(_scheduledJobsPaths.Root)
                ? _scheduledJobsPaths.ResolveRoot()
                : _scheduledJobsPaths.Root,
            _mikroTik.Value.Enabled,
            _mikroTik.Value.Port,
            _firmwareUpgrade.Value.Enabled,
            _assessment.Value.TargetRouterOsVersion,
            _scheduledJobs.Value.MaxConcurrentRouterJobs,
            _scheduledJobs.Value.MaxConcurrentCsvImports,
            _worker.Value.HeartbeatInterval,
            _serilog.Value.GetAbsoluteLogDirectory(),
            _serilog.Value.DeviceLogEnabled);
    }
}
