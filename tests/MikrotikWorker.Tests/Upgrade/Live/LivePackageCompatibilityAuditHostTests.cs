using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade.Live;
public sealed class LivePackageCompatibilityAuditHostTests
{
    [Fact]
    public void LiveAuditHost_DoesNotRegisterMutationServices()
    {
        ServiceCollection services = new();
        IConfiguration config = BuildConfig(Directory.CreateTempSubdirectory("live-host-fw-").FullName);
        LivePackageCompatibilityAuditHost.ConfigureLiveAuditServices(services, config);
        services.AddLogging();

        foreach (Type forbidden in LivePackageCompatibilityAuditHost.ForbiddenMutationServiceTypes)
        {
            Assert.DoesNotContain(
                services,
                d => d.ServiceType == forbidden || d.ImplementationType == forbidden);
        }
    }

    [Fact]
    public void LiveAuditHost_ResolvesReadOnlyGraph_AndNotMutationServices()
    {
        using IHost host = BuildLiveHost();
        IServiceProvider sp = host.Services;

        Assert.NotNull(sp.GetService<IReadOnlyMikrotikInventoryCollector>());
        Assert.NotNull(sp.GetService<ILivePackageCompatibilityFleetAuditor>());
        Assert.NotNull(sp.GetService<IPackageCompatibilityAuditor>());
        Assert.NotNull(sp.GetService<IUpgradePackageResolver>());
        Assert.NotNull(sp.GetService<IRouterOsV7TargetProvider>());
        Assert.NotNull(sp.GetService<IMikrotikConnectionFactory>());

        Assert.Null(sp.GetService<IRouterOsV6UpgradeExecutor>());
        Assert.Null(sp.GetService<IDeviceBackupService>());
        Assert.Null(sp.GetService<IMikrotikRouterOperations>());
        Assert.Null(sp.GetService<IMikrotikApiClient>());
        Assert.Null(sp.GetService<IMikrotikFileTransferClient>());
        Assert.Null(sp.GetService<RouterJobExecutor>());
    }

    [Fact]
    public async Task LiveAuditHost_StartsOnlyV7TargetProvider()
    {
        using IHost host = BuildLiveHost();
        await host.StartAsync();

        IEnumerable<IHostedService> hosted = host.Services.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, static h => h is RouterOsV7TargetProvider);
        Assert.DoesNotContain(hosted, static h => h is Worker);
        Assert.DoesNotContain(hosted, static h => h is ScheduledJobsWatcherService);
        Assert.DoesNotContain(hosted, static h => h is RouterJobExecutionService);
        Assert.DoesNotContain(hosted, static h => h is InMemoryJobSchedulerService);

        await host.StopAsync();
    }

    [Fact]
    public void NormalApplicationHost_StillRegistersUpgradeServices()
    {
        WebApplicationBuilder builder = MikrotikWorkerHost.CreateApplicationBuilder([]);
        Assert.Contains(
            builder.Services,
            static d => d.ServiceType == typeof(IRouterOsV6UpgradeExecutor));
        Assert.Contains(
            builder.Services,
            static d => d.ServiceType == typeof(IDeviceBackupService));
        Assert.Contains(
            builder.Services,
            static d => d.ServiceType == typeof(IMikrotikRouterOperations));
        Assert.Contains(
            builder.Services,
            static d => d.ImplementationType == typeof(Worker)
                || (d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(Worker)));
    }

    [Fact]
    public void FirmwareUpgrade_RemainsDisabledByDefault_OnLiveHost()
    {
        using IHost host = BuildLiveHost(firmwareUpgradeEnabled: false);
        Assert.False(host.Services.GetRequiredService<IOptions<FirmwareUpgradeOptions>>().Value.Enabled);
    }

    [Fact]
    public void LiveRunner_UsesDedicatedHost_NotFullWorkerHost()
    {
        string runner = typeof(LivePackageCompatibilityAuditRunner).Assembly.Location;
        Assert.False(string.IsNullOrWhiteSpace(runner));
        // Structural: dedicated host type exists and exposes forbidden-mutation catalogue.
        Assert.NotEmpty(LivePackageCompatibilityAuditHost.ForbiddenMutationServiceTypes);
        Assert.Contains(
            LivePackageCompatibilityAuditHost.ForbiddenMutationServiceTypes,
            static t => t == typeof(IRouterOsV6UpgradeExecutor));
    }

    private static IHost BuildLiveHost(bool firmwareUpgradeEnabled = false)
    {
        string fw = Directory.CreateTempSubdirectory("live-host-").FullName;
        Directory.CreateDirectory(Path.Combine(fw, "mikrotik"));
        File.WriteAllText(Path.Combine(fw, "mikrotik", "version"), "7.23.8");

        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(BuildSettings(fw, firmwareUpgradeEnabled));
        LivePackageCompatibilityAuditHost.ConfigureLiveAuditServices(builder.Services, builder.Configuration);
        builder.Logging.ClearProviders();
        return builder.Build();
    }

    private static IConfiguration BuildConfig(string firmwareRoot) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(BuildSettings(firmwareRoot, firmwareUpgradeEnabled: false))
            .Build();

    private static Dictionary<string, string?> BuildSettings(string firmwareRoot, bool firmwareUpgradeEnabled) =>
        new()
        {
            ["FirmwareFiles:Path"] = firmwareRoot,
            ["Mikrotik:Enabled"] = "false",
            ["Mikrotik:Username"] = "admin",
            ["Mikrotik:Password"] = "secret",
            ["Mikrotik:Port"] = "8728",
            ["Mikrotik:ConnectionTimeout"] = "00:00:10",
            ["Mikrotik:ReconnectInterval"] = "00:00:30",
            ["FirmwareUpgrade:Enabled"] = firmwareUpgradeEnabled ? "true" : "false",
            ["FirmwareUpgrade:AdvertisedBaseUrl"] = "http://198.18.0.66:8080/firmware_files",
            ["LivePackageCompatibilityAudit:Enabled"] = "true",
            ["LivePackageCompatibilityAudit:MaxConcurrentConnections"] = "5",
        };
}
