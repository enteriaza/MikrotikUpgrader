using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Minimal dependency graph for the live read-only package compatibility audit.
/// Does not register upgrade, backup, fetch, reboot, scheduler, or mutation-capable RouterOS services.
/// </summary>
internal static class LivePackageCompatibilityAuditHost
{
    /// <summary>
    /// Creates a host builder that loads the same appsettings as the worker, without Kestrel or upgrade hosting.
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(string[]? args = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args ?? []);
        builder.Configuration.AddJsonFile(
            $"appsettings.{builder.Environment.EnvironmentName}.local.json",
            optional: true,
            reloadOnChange: true);

        ConfigureLiveAuditServices(builder.Services, builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        return builder;
    }

    /// <summary>
    /// Registers only services required for read-only fleet package compatibility auditing.
    /// </summary>
    public static IServiceCollection ConfigureLiveAuditServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<FirmwareFilesOptions>()
            .Bind(configuration.GetSection(FirmwareFilesOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FirmwareFilesOptions>, FirmwareFilesOptionsValidator>();

        services
            .AddOptions<MikroTikOptions>()
            .Bind(configuration.GetSection(MikroTikOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MikroTikOptions>, MikroTikOptionsValidator>();

        services
            .AddOptions<FirmwareUpgradeOptions>()
            .Bind(configuration.GetSection(FirmwareUpgradeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FirmwareUpgradeOptions>, FirmwareUpgradeOptionsValidator>();

        services
            .AddOptions<LivePackageCompatibilityAuditOptions>()
            .Bind(configuration.GetSection(LivePackageCompatibilityAuditOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<LivePackageCompatibilityAuditOptions>,
            LivePackageCompatibilityAuditOptionsValidator>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RouterOsV7TargetProvider>();
        services.AddSingleton<IRouterOsV7TargetProvider>(static sp =>
            sp.GetRequiredService<RouterOsV7TargetProvider>());
        // Only hosted service: version-file watch/load. No upgrade workers.
        services.AddHostedService(static sp => sp.GetRequiredService<RouterOsV7TargetProvider>());

        services.AddSingleton<IFirmwarePackageCatalog, FirmwarePackageCatalog>();
        services.AddSingleton<IUpgradePackageResolver, UpgradePackageResolver>();
        services.AddSingleton<IPackageCompatibilityAuditor, PackageCompatibilityAuditor>();
        services.AddSingleton<ICatalogueCoverageReporter, CatalogueCoverageReporter>();
        services.AddSingleton<IMikrotikConnectionFactory, Tik4netConnectionFactory>();
        services.AddSingleton<IReadOnlyMikrotikInventoryCollector, ReadOnlyMikrotikInventoryCollector>();
        services.AddSingleton<ILivePackageCompatibilityFleetAuditor, LivePackageCompatibilityFleetAuditor>();

        return services;
    }

    /// <summary>
    /// Types that must never appear in the live-audit service collection.
    /// </summary>
    internal static readonly Type[] ForbiddenMutationServiceTypes =
    [
        typeof(IRouterOsV6UpgradeExecutor),
        typeof(IDeviceBackupService),
        typeof(IMikrotikRouterOperations),
        typeof(IMikrotikApiClient),
        typeof(IMikrotikFileTransferClient),
        typeof(MikrotikApiClient),
        typeof(RouterOsV6UpgradeExecutor),
        typeof(DeviceBackupService),
        typeof(RouterJobExecutor),
        typeof(ScheduledJobsWatcherService),
        typeof(RouterJobExecutionService),
        typeof(InMemoryJobSchedulerService),
        typeof(Worker),
    ];
}
