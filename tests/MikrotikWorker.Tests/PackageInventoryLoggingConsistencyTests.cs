using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace MikrotikWorker.Tests;

public sealed class PackageInventoryLoggingConsistencyTests
{
    [Fact]
    public async Task ExecutorPackageSummary_IsConsistentOnDeviceAndAbsentAsRawDumpFromService()
    {
        string logRoot = Directory.CreateTempSubdirectory("pkg-log-").FullName;
        SerilogOptions options = new()
        {
            LogDirectory = logRoot,
            FileName = "application-.log",
            WriteToConsole = false,
            WriteToFile = true,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
            DeviceLogAsyncCapacity = 1000,
            DeviceLogAsyncBlockWhenFull = true,
            MinimumLevel = "Information",
        };

        DeviceLogWriter writer = new(Options.Create(options));
        await writer.StartAsync(CancellationToken.None);

        LoggerConfiguration configuration = new();
        SerilogConfigurator.Configure(
            configuration,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new FakeEnv(),
            options,
            writer);
        CollectingSink serviceSink = new();
        configuration.WriteTo.Logger(lc => lc
            .Filter.ByExcluding(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog)
            .WriteTo.Sink(serviceSink));
        using Logger serilog = configuration.CreateLogger();
        using SerilogLoggerFactory factory = new(serilog);
        ILogger<RouterOsV6UpgradeExecutor> logger = factory.CreateLogger<RouterOsV6UpgradeExecutor>();

        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "LIVE-TEST",
            RouterIp = IPAddress.Parse("198.18.0.83"),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-5),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

        InstalledPackageInfo[] inventory =
        [
            new("routeros-mipsbe", "6.49.23", false),
            new("system", "6.49.23", false, "routeros-mipsbe"),
            new("ipv6", "6.49.23", false, "routeros-mipsbe"),
            new("wireless", "6.49.23", false, "routeros-mipsbe"),
            new("hotspot", "6.49.23", false, "routeros-mipsbe"),
            new("mpls", "6.49.23", false, "routeros-mipsbe"),
            new("routing", "6.49.23", false, "routeros-mipsbe"),
            new("ppp", "6.49.23", false, "routeros-mipsbe"),
            new("dhcp", "6.49.23", false, "routeros-mipsbe"),
            new("security", "6.49.23", false, "routeros-mipsbe"),
            new("advanced-tools", "6.49.23", false, "routeros-mipsbe"),
        ];

        PackageInventorySummary summary = PackageInventoryReporting.Summarize(inventory);
        using (DeviceLogScope scope = DeviceLogScope.Begin(job, writer, new DeviceLogIdentityRegistry()))
        {
            scope.SetRouterIdentity("MikroTik");
            logger.LogInformation("Installed package count: {IndependentCount}", summary.IndependentCount);
            logger.LogInformation(
                "Installed packages: {Packages}",
                PackageInventoryReporting.FormatIndependentPackages(summary.IndependentPackages));
            logger.LogInformation("Bundled components: {BundledCount}", summary.BundledCount);
        }

        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();
        await serilog.DisposeAsync();

        string deviceLog = await File.ReadAllTextAsync(
            Path.Combine(writer.DeviceLogDirectory, "MikroTik_198.18.0.83.log"));
        string[] serviceMessages = serviceSink.Events.Select(static e => e.RenderMessage()).ToArray();

        Assert.Contains("Installed package count: 1", deviceLog, StringComparison.Ordinal);
        Assert.Contains("Installed packages: routeros-mipsbe/6.49.23", deviceLog, StringComparison.Ordinal);
        Assert.Contains("Bundled components: 10", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageCount=11", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("system/6.49.23, ipv6", deviceLog, StringComparison.Ordinal);

        Assert.DoesNotContain(serviceMessages, static m => m.Contains("Installed package count:", StringComparison.Ordinal));
        Assert.DoesNotContain(serviceMessages, static m => m.Contains("PackageCount=11", StringComparison.Ordinal));
        Assert.DoesNotContain(serviceMessages, static m => m.Contains("system/6.49.23, ipv6", StringComparison.Ordinal));
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class FakeEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "MikrotikWorker.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
