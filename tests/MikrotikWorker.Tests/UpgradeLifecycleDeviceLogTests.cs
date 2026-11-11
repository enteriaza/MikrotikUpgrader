using System.Net;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using Serilog;
using Serilog.Extensions.Logging;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests;

/// <summary>
/// Exercises the production DeviceLogWriter + Serilog device sink across a synthetic
/// backup → fetch → reboot → reconnect upgrade lifecycle (no live router).
/// </summary>
public sealed class UpgradeLifecycleDeviceLogTests
{
    [Fact]
    public async Task SyntheticUpgradeLifecycle_WritesCompleteDeviceAndApplicationLogs()
    {
        string logRoot = Directory.CreateTempSubdirectory("upg-devlog-").FullName;
        string firmwareRoot = Directory.CreateTempSubdirectory("upg-devlog-fw-").FullName;
        string backupRoot = Directory.CreateTempSubdirectory("upg-devlog-bk-").FullName;
        File.WriteAllBytes(Path.Combine(firmwareRoot, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);

        SerilogOptions serilogOptions = new()
        {
            LogDirectory = logRoot,
            FileName = "application-.log",
            RollingInterval = "Day",
            WriteToConsole = false,
            WriteToFile = true,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
            DeviceLogAsyncCapacity = 1000,
            DeviceLogAsyncBlockWhenFull = true,
            AsyncBufferCapacity = 1000,
            AsyncBlockWhenFull = true,
            MinimumLevel = "Information",
        };

        DeviceLogWriter deviceLogWriter = new(Options.Create(serilogOptions));
        DeviceLogIdentityRegistry identityRegistry = new();
        await deviceLogWriter.StartAsync(CancellationToken.None);

        LoggerConfiguration loggerConfiguration = new();
        SerilogConfigurator.Configure(
            loggerConfiguration,
            configuration: new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            environment: new FakeHostEnvironment(),
            options: serilogOptions,
            deviceLogWriter: deviceLogWriter);
        Serilog.Core.Logger serilogLogger = loggerConfiguration.CreateLogger();
        Log.Logger = serilogLogger;

        using SerilogLoggerFactory loggerFactory = new(serilogLogger);

        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero));
        TrackingOps ops = new();
        RecordingConnectionFactory connections = new(ops);
        FirmwareUpgradeOptions upgradeOptions = new()
        {
            Enabled = true,
            AdvertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
            FetchTimeout = TimeSpan.FromSeconds(5),
            ReconnectInterval = TimeSpan.FromMilliseconds(5),
            RebootTimeout = TimeSpan.FromMinutes(5),
            PostUpgradeVerifyTimeout = TimeSpan.FromSeconds(5),
            BackupFileReadyTimeout = TimeSpan.FromMilliseconds(200),
            BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(10),
            BackupDirectory = backupRoot,
            ScpPort = 22,
            ScpTimeout = TimeSpan.FromSeconds(5),
        };
        DeviceBackupService backup = new(
            ops,
            new InMemoryFileTransfer(),
            Options.Create(upgradeOptions),
            time,
            loggerFactory.CreateLogger<DeviceBackupService>());

        RouterOsV6UpgradeExecutor executor = new(
            ops,
            connections,
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(firmwareRoot),
                Options.Create(new FirmwareFilesOptions { Path = firmwareRoot })),
            backup,
            new FixedRouterOsV7TargetProvider(null),
            Options.Create(upgradeOptions),
            Options.Create(new MikroTikOptions { Port = 8728 }),
            time,
            loggerFactory.CreateLogger<RouterOsV6UpgradeExecutor>());

        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "lifecycle.csv",
            RowNumber = 2,
            RouterName = "LIVE-TEST",
            RouterIp = IPAddress.Parse("198.18.0.83"),
            DateStartUtc = time.GetUtcNow().UtcDateTime.AddMinutes(-5),
            DateStopUtc = time.GetUtcNow().UtcDateTime.AddHours(1),
        };

        MikrotikDeviceInfo before = new()
        {
            Identity = "ACE01.POP001.001.core",
            Version = "6.49.10 (long-term)",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardModel = "960PGS",
            RouterboardFirmwareType = "qca9550L",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "6.49.23",
            RouterboardFactoryFirmware = "3.41",
        };

        DeviceCompatibilityAssessment assessment = new()
        {
            RouterIp = "198.18.0.83",
            OverallStatus = DeviceAssessmentStatus.ReadyForUpgrade,
            RouterOsVersionClassification = RouterOsVersionClassification.OlderThanTargetV6,
            TargetRouterOsVersion = "6.49.23",
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.UpgradeAvailable,
            AssessedAtUtc = time.GetUtcNow(),
        };

        string expectedDeviceLogKey = DeviceLogNaming.BuildDeviceLogFileName(before.Identity, job.RouterIp);
        UpgradeExecutionResult result;
        using (DeviceLogScope scope = DeviceLogScope.Begin(job, deviceLogWriter, identityRegistry, time))
        {
            ILogger<RouterJobMarker> jobLogger = loggerFactory.CreateLogger<RouterJobMarker>();
            using (DeviceLogging.BeginServiceOnly())
            {
                jobLogger.LogInformation(
                    "Router job started. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp);
            }

            jobLogger.LogInformation("Router job started");
            jobLogger.LogInformation("Connection attempt started");
            jobLogger.LogInformation("Connected successfully");
            scope.SetRouterIdentity(before.Identity);
            jobLogger.LogInformation("RouterOS version: {RouterOsVersion}", before.Version);
            jobLogger.LogInformation("Architecture: {Architecture}", before.ArchitectureName);

            result = await executor.ExecuteAsync(
                new TikFakeConnection(),
                job,
                before,
                assessment,
                "admin",
                "super-secret-password",
                static () => true,
                CancellationToken.None);

            using (DeviceLogging.BeginServiceSummary())
            {
                jobLogger.LogInformation(
                    "Final upgrade outcome: {UpgradeOutcome} (version={PostUpgradeVersion})",
                    result.Outcome,
                    result.PostUpgradeVersion);
            }
        }

        Assert.Equal(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
        Assert.True(ops.RebootCalled);
        Assert.True(ops.FetchCalled);

        await deviceLogWriter.StopAsync(CancellationToken.None);
        deviceLogWriter.Dispose();
        await Log.CloseAndFlushAsync();
        await serilogLogger.DisposeAsync();
        Log.Logger = Serilog.Core.Logger.None;

        string deviceLogPath = Path.Combine(deviceLogWriter.DeviceLogDirectory, expectedDeviceLogKey);
        Assert.True(File.Exists(deviceLogPath), $"Missing device log at {deviceLogPath}");
        string deviceLog = await File.ReadAllTextAsync(deviceLogPath);

        string[] requiredFragments =
        [
            "Device: ACE01.POP001.001.core",
            "Address: 198.18.0.83",
            "CSV name: LIVE-TEST",
            "Import ID:",
            "Job ID:",
            "Connected successfully",
            "RouterOS version:",
            "Installed package count:",
            "Installed packages:",
            "Bundled components:",
            "Backup started",
            "Creating RouterOS export",
            "Export verified",
            "Binary backup verified",
            "Remote backup cleanup succeeded",
            "v6-stepping backup prerequisite passed",
            "Fetching firmware package",
            "Staged package verified",
            "Package staging complete",
            "Initiating v6-stepping reboot",
            "Reconnection attempt",
            "Reconnected successfully",
            "Post-upgrade RouterOS version",
            "RouterBOARD RouterBOOT fields before v6-stepping",
            "RouterBOARD RouterBOOT fields after upgrade",
            "Final upgrade outcome",
            "UpgradeSucceededV6Only",
        ];

        foreach (string fragment in requiredFragments)
        {
            Assert.Contains(fragment, deviceLog, StringComparison.Ordinal);
        }

        Assert.Contains("Router job started", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportId=", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("JobId=", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("RouterIp=", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("RouterName=", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageCount=", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("system/6.49.23, ipv6", deviceLog, StringComparison.Ordinal);
        Assert.Contains("Installed package count: 1", deviceLog, StringComparison.Ordinal);
        Assert.Contains("Installed packages: routeros-mipsbe/", deviceLog, StringComparison.Ordinal);
        Assert.Contains("Bundled components: 10", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-password", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", deviceLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/system/routerboard/upgrade", deviceLog, StringComparison.Ordinal);

        string[] appLogs = Directory.GetFiles(logRoot, "application-*.log");
        Assert.NotEmpty(appLogs);
        string appLog = await File.ReadAllTextAsync(appLogs[0]);
        Assert.Contains("Router job started", appLog, StringComparison.Ordinal);
        Assert.Contains("ImportId=", appLog, StringComparison.Ordinal);
        Assert.Contains("Final upgrade outcome", appLog, StringComparison.Ordinal);
        Assert.Contains("UpgradeSucceededV6Only", appLog, StringComparison.Ordinal);
        // Device-detail lines must not flood the service log.
        Assert.DoesNotContain("Creating RouterOS export", appLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Staged package verified", appLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Installed package count:", appLog, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageCount=11", appLog, StringComparison.Ordinal);
        Assert.DoesNotContain("system/6.49.23, ipv6", appLog, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-password", appLog, StringComparison.Ordinal);
        Assert.Equal(0, deviceLogWriter.WriteFailureCount);

        string stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd_HHmmss_fff");
        string expectedStem = $"ACE01.POP001.001.core_198.18.0.83_{stamp}";
        Assert.True(File.Exists(Path.Combine(backupRoot, "Exports", expectedStem + ".rsc")));
        Assert.True(File.Exists(Path.Combine(backupRoot, "Binary", expectedStem + ".backup")));
        Assert.True(File.Exists(Path.Combine(backupRoot, "Manifests", expectedStem + ".json")));
    }

    private sealed class RouterJobMarker;

    private sealed class FakeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "MikrotikWorker.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class RecordingConnectionFactory(TrackingOps ops) : IMikrotikConnectionFactory
    {
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            ops.AdvanceToPostUpgrade();
            return Task.FromResult<ITikConnection>(new TikFakeConnection());
        }
    }

    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        private string _version = "6.49.10";
        private readonly Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);

        public bool FetchCalled { get; private set; }

        public bool RebootCalled { get; private set; }

        public void AdvanceToPostUpgrade() => _version = "6.49.23";

        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MikrotikDeviceInfo
            {
                Identity = "ACE01.POP001.001.core",
                Version = _version == "6.49.23" ? "6.49.23 (long-term)" : "6.49.10 (long-term)",
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
                RouterboardModel = "960PGS",
                RouterboardFirmwareType = "qca9550L",
                RouterboardCurrentFirmware = "7.23.8",
                RouterboardUpgradeFirmware = _version == "6.49.23" ? "6.49.23" : "6.49.10",
                RouterboardFactoryFirmware = "3.41",
            });

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackageInfo>>(
            [
                new("routeros-mipsbe", _version, false),
                new("system", _version, false, "routeros-mipsbe"),
                new("ipv6", _version, false, "routeros-mipsbe"),
                new("wireless", _version, false, "routeros-mipsbe"),
                new("hotspot", _version, false, "routeros-mipsbe"),
                new("mpls", _version, false, "routeros-mipsbe"),
                new("routing", _version, false, "routeros-mipsbe"),
                new("ppp", _version, false, "routeros-mipsbe"),
                new("dhcp", _version, false, "routeros-mipsbe"),
                new("security", _version, false, "routeros-mipsbe"),
                new("advanced-tools", _version, false, "routeros-mipsbe"),
            ]);

        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default)
        {
            _files[fileNameWithoutExtension + ".rsc"] = 12;
            return Task.CompletedTask;
        }

        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default)
        {
            _files[fileNameWithoutExtension + ".backup"] = 12;
            return Task.CompletedTask;
        }

        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            if (_files.TryGetValue(fileName, out long size))
            {
                return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, size, "*1"));
            }

            if (fileName.EndsWith(".npk", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, 4, "*2"));
            }

            return Task.FromResult<RouterFileInfo?>(null);
        }

        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            _files.Remove(fileName);
            return Task.CompletedTask;
        }

        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            _files[destinationFileName] = 4;
            return Task.CompletedTask;
        }

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryFileTransfer : IMikrotikFileTransferClient
    {
        public Task DownloadFileAsync(
            string host,
            int port,
            string username,
            string password,
            string remoteFileName,
            string localFilePath,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            File.WriteAllBytes(localFilePath, Enumerable.Repeat((byte)7, 12).ToArray());
            return Task.CompletedTask;
        }
    }
}
