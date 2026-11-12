// -----------------------------------------------------------------------------
// File: UpgradeLifecycleDeviceLogTests.cs
//
// Description:
// Exercises the production DeviceLogWriter + Serilog device sink across a
// synthetic backup → fetch → reboot → reconnect upgrade lifecycle (no live
// router).
//
// -----------------------------------------------------------------------------

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

namespace MikrotikWorker.Tests.Upgrade;

/// <summary>
/// Exercises the production DeviceLogWriter + Serilog device sink across a synthetic
/// backup → fetch → reboot → reconnect upgrade lifecycle (no live router).
/// </summary>
/// <remarks>
/// Serialized with other tests that mutate the process-wide <c>Log.Logger</c>.
/// </remarks>
[Collection("Serilog")]
public sealed class UpgradeLifecycleDeviceLogTests
{
    /// <summary>
    /// Scenario: A synthetic (not live) upgrade lifecycle runs with real Serilog and device-log writing, a temporary
    /// backup root, a short backup-file-ready timeout, no RouterOS 7 target, and a router whose password is
    /// super-secret-password.
    /// Expected: The outcome is TargetUnavailable at the v6 stepping stage, with the reboot and fetch both performed.
    /// The device log exists and carries the lifecycle fragments, installed package count and bundled components. The
    /// application log carries the job start and the no-validated-target message. The export, binary backup and
    /// manifest files exist, and no device-log write failure is counted.
    /// Invariant: The device log is complete and free of correlation identifiers, the application log stays
    /// service-level without device detail, and the router password appears in neither log.
    /// </summary>
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
        IOptions<FirmwareUpgradeOptions> upgradeOptionsWrapper = Options.Create(upgradeOptions);
        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot });
        IOptions<MikroTikOptions> mikrotikOptions = Options.Create(new MikroTikOptions { Port = 8728 });
        DeviceBackupService backup = new(
            ops,
            new InMemoryFileTransfer(),
            upgradeOptionsWrapper,
            time,
            loggerFactory.CreateLogger<DeviceBackupService>());

        RouterOsV6UpgradeExecutor executor = new(
            ops,
            connections,
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(firmwareRoot),
                firmwareFilesOptions),
            backup,
            new FixedRouterOsV7TargetProvider(null),
            new LegacyFleetUpgradeAutomationCleanup(
                ops,
                loggerFactory.CreateLogger<LegacyFleetUpgradeAutomationCleanup>()),
            new RouterboardFirmwareUpgradeService(
                ops,
                connections,
                upgradeOptionsWrapper,
                mikrotikOptions,
                time,
                loggerFactory.CreateLogger<RouterboardFirmwareUpgradeService>()),
            new PassingOspfMigrationService(),
            new PassingOspfStaticSummaryRedistributionService(),
            upgradeOptionsWrapper,
            mikrotikOptions,
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

        // No RouterOS 7 target is configured: the v6 stage completes and the job stops as TargetUnavailable (never success).
        Assert.Equal(UpgradeOutcome.TargetUnavailable, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.V6Stepping, result.CompletedStage);
        Assert.True(ops.RebootCalled);
        Assert.True(ops.FetchCalled);

        // Flush Serilog (including its async buffer) BEFORE stopping DeviceLogWriter.
        // Otherwise late DeviceLogSink.Emit calls are dropped once _stopped is set, which
        // intermittently omits "Final upgrade outcome" under suite load.
        await Log.CloseAndFlushAsync();
        await serilogLogger.DisposeAsync();
        Log.Logger = Serilog.Core.Logger.None;
        await deviceLogWriter.StopAsync(CancellationToken.None);
        deviceLogWriter.Dispose();

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
            "TargetUnavailable",
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
        Assert.Contains("No validated RouterOS 7 target is available", appLog, StringComparison.Ordinal);
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

    /// <summary>Marker type used as the logger category for router-job log output.</summary>
    private sealed class RouterJobMarker;

    /// <summary>
    /// Minimal <see cref="Microsoft.Extensions.Hosting.IHostEnvironment"/> for building logging infrastructure in
    /// tests, with no content files.
    /// </summary>
    private sealed class FakeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        /// <summary>Hosting environment name (default <c>Development</c>).</summary>
        public string EnvironmentName { get; set; } = "Development";

        /// <summary>Application name (default <c>MikrotikWorker.Tests</c>).</summary>
        public string ApplicationName { get; set; } = "MikrotikWorker.Tests";

        /// <summary>Content root (default the test base directory).</summary>
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        /// <summary>File provider for the content root; an empty provider since no content files are used.</summary>
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>
    /// Connection factory that flips the fake router to its post-upgrade state when a reconnect happens and returns a
    /// fake connection.
    /// </summary>
    private sealed class RecordingConnectionFactory(TrackingOps ops) : IMikrotikConnectionFactory
    {
        /// <summary>Advances the router fake to the post-upgrade version and returns a fake connection.</summary>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            ops.AdvanceToPostUpgrade();
            return Task.FromResult<ITikConnection>(new TikFakeConnection());
        }
    }

    /// <summary>
    /// Router operations fake with an in-memory file store and a version that moves from 6.49.10 to 6.49.23 on
    /// reconnect; it reports a full bundled-package inventory so lifecycle log output can be inspected.
    /// </summary>
    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        /// <summary>Version the fake router currently reports, 6.49.10 until advanced to post-upgrade.</summary>
        private string _version = "6.49.10";
        /// <summary>In-memory router file store (name to size), case-insensitive.</summary>
        private readonly Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True once <see cref="FetchUrlToFileAsync"/> has been invoked, i.e. the firmware package fetch was
        /// attempted.
        /// </summary>
        public bool FetchCalled { get; private set; }

        /// <summary>
        /// True once <see cref="RebootAsync"/> has been invoked, i.e. the executor requested a router reboot.
        /// </summary>
        public bool RebootCalled { get; private set; }

        /// <summary>Switches the reported version to the post-upgrade 6.49.23.</summary>
        public void AdvanceToPostUpgrade() => _version = "6.49.23";

        /// <summary>
        /// Returns device info for <c>ACE01.POP001.001.core</c> with a long-term version suffix and full RouterBOARD
        /// details that follow the current version.
        /// </summary>
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

        /// <summary>
        /// Returns <c>routeros-mipsbe</c> plus ten bundled component packages at the current version.
        /// </summary>
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

        /// <summary>Registers a 12-byte <c>.rsc</c> file in the in-memory store.</summary>
        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default)
        {
            _files[fileNameWithoutExtension + ".rsc"] = 12;
            return Task.CompletedTask;
        }

        /// <summary>Registers a 12-byte <c>.backup</c> file in the in-memory store.</summary>
        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default)
        {
            _files[fileNameWithoutExtension + ".backup"] = 12;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns the stored file, a 4-byte entry for any <c>.npk</c>, or null for everything else.
        /// </summary>
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

        /// <summary>
        /// No-op: files are reported ready immediately, so the executor never waits on backup-file polling.
        /// </summary>
        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>Removes the file from the in-memory store.</summary>
        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            _files.Remove(fileName);
            return Task.CompletedTask;
        }

        /// <summary>Marks the fetch as called and registers the destination file as 4 bytes.</summary>
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

        /// <summary>Marks the reboot as requested.</summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reports zero matching system scripts, so legacy automation cleanup finds nothing to inspect.
        /// </summary>
        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>No-op: there are no legacy scripts to remove.</summary>
        public Task RemoveSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Reports zero matching schedulers, so legacy automation cleanup finds nothing to inspect.
        /// </summary>
        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>No-op: there are no legacy schedulers to remove.</summary>
        public Task RemoveSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// File transfer client that writes a fixed 12-byte payload locally instead of downloading from a router.
    /// </summary>
    private sealed class InMemoryFileTransfer : IMikrotikFileTransferClient
    {
        /// <summary>Creates the destination directory and writes 12 bytes of <c>0x07</c> to the local path.</summary>
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
