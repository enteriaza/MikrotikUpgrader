// -----------------------------------------------------------------------------
// File: PackageInventoryLoggingConsistencyTests.cs
//
// Description:
// Package inventory logging consistency across upgrade stages.
//
// -----------------------------------------------------------------------------
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

namespace MikrotikWorker.Tests.Logging;
/// <summary>
/// Checks that the package inventory is summarised consistently in the per-device log and that
/// the raw package dump never leaks into the service log.
/// </summary>
public sealed class PackageInventoryLoggingConsistencyTests
{
    /// <summary>
    /// Logs the inventory summary through the real Serilog pipeline using the
    /// <c>RouterOsV6UpgradeExecutor</c> logger category.
    /// Preconditions: An inventory of 11 packages (one independent <c>routeros-mipsbe</c> plus 10
    /// bundled components) is summarised and logged inside a <c>DeviceLogScope</c>; the service sink
    /// excludes device-detail events.
    /// Expected: The device log shows "Installed package count: 1", "Installed packages:
    /// routeros-mipsbe/6.49.23" and "Bundled components: 10" and no raw dump; the service sink has
    /// none of those lines or the raw dump.
    /// Invariant: The device log counts independent packages only and the service log never gets a
    /// package dump.
    /// </summary>
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

    /// <summary>
    /// Serilog sink that records events reaching the simulated service log so the test can assert
    /// what was excluded.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        /// <summary>
        /// Events received, in arrival order.
        /// </summary>
        public List<LogEvent> Events { get; } = [];

        /// <summary>
        /// Stores the event.
        /// </summary>
        /// <param name="logEvent">Event delivered by Serilog.</param>
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    /// <summary>
    /// Minimal development <c>IHostEnvironment</c> that satisfies
    /// <c>SerilogConfigurator.Configure</c> without a real host.
    /// </summary>
    private sealed class FakeEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        /// <summary>
        /// Environment name; "Development".
        /// </summary>
        public string EnvironmentName { get; set; } = "Development";

        /// <summary>
        /// Application name; "MikrotikWorker.Tests".
        /// </summary>
        public string ApplicationName { get; set; } = "MikrotikWorker.Tests";

        /// <summary>
        /// Content root; the test assembly's base directory.
        /// </summary>
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        /// <summary>
        /// Empty file provider, since no configuration files are read from the content root.
        /// </summary>
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
