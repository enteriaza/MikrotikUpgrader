// -----------------------------------------------------------------------------
// File: ServiceLifecycleLoggingHostedService.cs
//
// Description:
// Emits credential-free config summary (including independent Mikrotik:Enabled and
// FirmwareUpgrade:Enabled) plus Application ready/stopping lifecycle events into
// the production Serilog pipeline without device correlation context.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Logging;

/// <summary>
/// Emits application-level service lifecycle events (config summary, ready, stopping)
/// into the production Serilog pipeline without device correlation context.
/// </summary>
internal sealed class ServiceLifecycleLoggingHostedService : IHostedService
{
    /// <summary>
    /// Logger that writes lifecycle events to the application log.
    /// </summary>
    private readonly ILogger<ServiceLifecycleLoggingHostedService> _logger;
    /// <summary>
    /// Host lifetime whose started and stopping notifications trigger the lifecycle messages.
    /// </summary>
    private readonly IHostApplicationLifetime _lifetime;
    /// <summary>
    /// Configured listening address and port reported in the summary.
    /// </summary>
    private readonly IOptions<ListeningOptions> _listening;
    /// <summary>
    /// Firmware file location settings reported in the summary.
    /// </summary>
    private readonly IOptions<FirmwareFilesOptions> _firmwareFiles;
    /// <summary>
    /// Scheduled-job concurrency settings reported in the summary.
    /// </summary>
    private readonly IOptions<ScheduledJobsOptions> _scheduledJobs;
    /// <summary>
    /// Resolves the scheduled-jobs root directory reported in the summary.
    /// </summary>
    private readonly ScheduledJobsPaths _scheduledJobsPaths;
    /// <summary>
    /// MikroTik connectivity flags reported in the summary; credentials are never logged.
    /// </summary>
    private readonly IOptions<MikroTikOptions> _mikroTik;
    /// <summary>
    /// Firmware upgrade settings, including the enabled flag, reported in the summary.
    /// </summary>
    private readonly IOptions<FirmwareUpgradeOptions> _firmwareUpgrade;
    /// <summary>
    /// OSPF summary redistribution setting reported in the summary.
    /// </summary>
    private readonly IOptions<OspfSummaryRedistributionOptions> _ospfSummaryRedistribution;
    /// <summary>
    /// Provides the current RouterOS 7 target version reported in the summary.
    /// </summary>
    private readonly IRouterOsV7TargetProvider _v7TargetProvider;
    /// <summary>
    /// Worker settings, such as the heartbeat interval, reported in the summary.
    /// </summary>
    private readonly IOptions<WorkerOptions> _worker;
    /// <summary>
    /// Logging settings reported in the summary, such as the log directory and device-log flag.
    /// </summary>
    private readonly IOptions<SerilogOptions> _serilog;
    /// <summary>
    /// Host environment whose name is reported in the summary.
    /// </summary>
    private readonly IHostEnvironment _environment;
    /// <summary>
    /// Registration for the application-started callback; disposed on stop.
    /// </summary>
    private CancellationTokenRegistration _startedRegistration;
    /// <summary>
    /// Registration for the application-stopping callback; disposed on stop.
    /// </summary>
    private CancellationTokenRegistration _stoppingRegistration;

    /// <summary>
    /// Creates the lifecycle logger with the options snapshots used in the configuration summary.
    /// </summary>
    public ServiceLifecycleLoggingHostedService(
        ILogger<ServiceLifecycleLoggingHostedService> logger,
        IHostApplicationLifetime lifetime,
        IOptions<ListeningOptions> listening,
        IOptions<FirmwareFilesOptions> firmwareFiles,
        IOptions<ScheduledJobsOptions> scheduledJobs,
        ScheduledJobsPaths scheduledJobsPaths,
        IOptions<MikroTikOptions> mikroTik,
        IOptions<FirmwareUpgradeOptions> firmwareUpgrade,
        IOptions<OspfSummaryRedistributionOptions> ospfSummaryRedistribution,
        IRouterOsV7TargetProvider v7TargetProvider,
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
        _ospfSummaryRedistribution = ospfSummaryRedistribution;
        _v7TargetProvider = v7TargetProvider;
        _worker = worker;
        _serilog = serilog;
        _environment = environment;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Hosted services stopping");
        _startedRegistration.Dispose();
        _stoppingRegistration.Dispose();
        _logger.LogInformation("Application shutdown completed");
        return Task.CompletedTask;
    }

    /// <summary>Logs <c>Application ready</c> when the host reports started.</summary>
    private void OnApplicationStarted()
    {
        // Actual listen addresses are emitted by Microsoft.Hosting.Lifecycle ("Now listening on: ...").
        _logger.LogInformation("Application ready");
    }

    /// <summary>Logs <c>Application stopping</c> when the host begins shutdown.</summary>
    private void OnApplicationStopping()
    {
        _logger.LogInformation("Application stopping");
    }

    /// <summary>
    /// Emits a one-line configuration summary (paths, flags, concurrency) without credentials.
    /// </summary>
    private void LogConfigurationSummary()
    {
        // Never log credentials, tokens, or backup passwords.
        RouterOsV7TargetSnapshot? v7 = _v7TargetProvider.TryGetCurrent();
        _logger.LogInformation(
            "Application configuration summary. Environment={EnvironmentName}, Listening={IpAddress}:{Port}, FirmwareFilesPath={FirmwareFilesPath}, ScheduledJobsPath={ScheduledJobsPath}, MikroTikEnabled={MikroTikEnabled}, MikroTikPort={MikroTikPort}, FirmwareUpgradeEnabled={FirmwareUpgradeEnabled}, RedistributeSummaryviaStatics={RedistributeSummaryviaStatics}, V6SteppingVersion={V6SteppingVersion}, RouterOsV7Target={RouterOsV7Target}, MaxConcurrentRouterJobs={MaxConcurrentRouterJobs}, MaxConcurrentCsvImports={MaxConcurrentCsvImports}, HeartbeatInterval={HeartbeatInterval}, LogDirectory={LogDirectory}, DeviceLogEnabled={DeviceLogEnabled}",
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
            _ospfSummaryRedistribution.Value.RedistributeSummaryviaStatics,
            RouterOsUpgradeVersions.V6SteppingVersionText,
            v7?.VersionText ?? "(unavailable)",
            _scheduledJobs.Value.MaxConcurrentRouterJobs,
            _scheduledJobs.Value.MaxConcurrentCsvImports,
            _worker.Value.HeartbeatInterval,
            _serilog.Value.GetAbsoluteLogDirectory(),
            _serilog.Value.DeviceLogEnabled);

        if (!_firmwareUpgrade.Value.Enabled)
        {
            _logger.LogWarning(
                "Firmware upgrades are administratively disabled (FirmwareUpgrade:Enabled=false). No device will be upgraded and every scheduled job will be reported as Failed until this is set to true.");
        }
    }
}
