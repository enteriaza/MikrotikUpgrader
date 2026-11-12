// -----------------------------------------------------------------------------
// File: FirmwareUploadStorageModelTests.cs
//
// Description:
// Tests for firmware upload/storage naming and path models used during package
// staging.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade;
    /// <summary>
    /// Scenario: BR and ACE identity strings classified through <c>DeviceIdentityClassifier</c>.
    /// Expected: each maps to the matching <c>DeviceType</c> and <c>ShouldUpgradeToV7</c> remains true.
    /// Invariant: low-flash device classes stay eligible for RouterOS 7; classification must not silently opt them out.
    /// </summary>
public sealed class FirmwareUploadStorageModelTests
{
    /// <summary>
    /// A device info with only identity, version, architecture and board name (no free-flash or memory fields) must
    /// still yield <c>MikrotikPreflightStatus.Succeeded</c> with a null <c>FailureReason</c>.
    /// </summary>
    [Fact]
    public void Preflight_DoesNotRequireFreeFlashOrMemoryFields()
    {
        MikrotikDeviceInfo info = new()
        {
            Identity = "BR01.POP011.016.Tommy",
            Version = "6.49.19",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

        MikrotikPreflightResult result = MikrotikPreflightResult.FromDeviceInfo(
            "198.18.240.11",
            info,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(MikrotikPreflightStatus.Succeeded, result.Status);
        Assert.Null(result.FailureReason);
    }

    /// <summary>
    /// Scenario: BR and ACE identity strings classified through DeviceIdentityClassifier.
    /// Expected: each maps to the matching DeviceType and ShouldUpgradeToV7 remains true.
    /// Invariant: low-flash device classes stay eligible for RouterOS 7.
    /// </summary>
    [Theory]
    [InlineData("BR01.POP011.016.Tommy", DeviceType.Br)]
    [InlineData("ACE01.POP001.001.core", DeviceType.Ace)]
    public void LowFlashDeviceTypes_RemainEligibleForRouterOs7(string identity, DeviceType expectedType)
    {
        DeviceIdentityClassification classification = DeviceIdentityClassifier.Classify(identity);

        Assert.Equal(expectedType, classification.DeviceType);
        Assert.True(classification.ShouldUpgradeToV7);
    }

    /// <summary>
    /// Scans the preflight, assessor, executor, resolver and API client sources and asserts that none contains a
    /// flash-capacity token such as <c>free-hdd</c>, <c>total-hdd-space</c>, <c>FreeDisk</c>, 'insufficient space',
    /// <c>StorageThreshold</c> or <c>MinimumFree</c> (case-insensitive).
    /// </summary>
    [Fact]
    public void FirmwareEligibilityPath_HasNoFlashSpaceThreshold()
    {
        string[] sources =
        [
            ReadProductionSource("MikroTik", "MikrotikPreflightResult.cs"),
            ReadProductionSource("Assessment", "DeviceCompatibilityAssessor.cs"),
            ReadProductionSource("Upgrade", "RouterOsV6UpgradeExecutor.cs"),
            ReadProductionSource("Upgrade", "UpgradePackageResolver.cs"),
            ReadProductionSource("MikroTik", "MikrotikApiClient.cs"),
        ];

        string[] forbidden =
        [
            "free-hdd",
            "FreeHdd",
            "free-hdd-space",
            "total-hdd-space",
            "FreeDisk",
            "flash free",
            "available flash",
            "insufficient space",
            "StorageThreshold",
            "MinimumFree",
        ];

        foreach (string source in sources)
        {
            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Source check: the API client issues <c>/tool/fetch</c> with <c>dst-path</c> set to the bare destination file
    /// name, and the executor calls <c>FetchUrlToFileAsync</c> then <c>WaitForFetchedPackageSizeAsync</c> without
    /// ever mentioning <c>free-hdd</c>.
    /// </summary>
    [Fact]
    public void FetchImplementation_UsesToolFetchWithBareDstPath_NotFlashCapacityGate()
    {
        string api = ReadProductionSource("MikroTik", "MikrotikApiClient.cs");
        string executor = ReadProductionSource("Upgrade", "RouterOsV6UpgradeExecutor.cs");

        Assert.Contains("/tool/fetch", api, StringComparison.Ordinal);
        Assert.Contains("AddParameter(\"dst-path\", destinationFileName)", api, StringComparison.Ordinal);
        Assert.Contains("FetchUrlToFileAsync", executor, StringComparison.Ordinal);
        Assert.Contains("WaitForFetchedPackageSizeAsync", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("free-hdd", executor, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: A 12 MiB routeros-mipsbe-6.49.23.npk sits in the firmware root and the executor runs for router
    /// BR01.POP011.016.Tommy (198.18.240.11) on 6.49.10, with no RouterOS 7 target configured and fake operations
    /// that expect a fetch of exactly that size.
    /// Expected: The fetch is attempted and one reboot occurs. The job stops at the v6 stepping stage with
    /// TargetUnavailable because no RouterOS 7 target exists; it is never reported as success.
    /// Invariant: Package size is not checked against a hypothetical flash capacity, so a large package never
    /// prevents the fetch attempt.
    /// </summary>
    [Fact]
    public async Task PackageLargerThanHypotheticalFlash_DoesNotPreventFetchAttempt()
    {
        string root = Directory.CreateTempSubdirectory("fw-storage-model-").FullName;
        const long packageBytes = 12L * 1024 * 1024;
        byte[] payload = new byte[packageBytes];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), payload);

        TrackingOps ops = new("BR01.POP011.016.Tommy")
        {
            VersionsAfterReboot = ["6.49.23"],
            ExpectedFetchSizeBytes = packageBytes,
        };

        RouterOsV6UpgradeExecutor executor = CreateExecutorWithoutV7Target(ops, root);
        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "BR01.POP011.016.Tommy", version: "6.49.10"),
            CreateJob("Tommy", "198.18.240.11"),
            CreateDevice("BR01.POP011.016.Tommy", "6.49.10"),
            CreateAssessment("198.18.240.11"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        // The v6 stage fetched and rebooted; with no RouterOS 7 target the job stops as TargetUnavailable (never success).
        Assert.Equal(UpgradeOutcome.TargetUnavailable, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.V6Stepping, result.CompletedStage);
        Assert.True(ops.FetchCalled);
        Assert.Equal(1, ops.RebootCount);
    }

    /// <summary>
    /// When the fake fetch throws, the executor must return <c>UpgradeFailed</c> after attempting the fetch
    /// (<c>FetchCalled</c>) and never reboot (<c>RebootCount</c> is 0).
    /// </summary>
    [Fact]
    public async Task GenuineFetchFailure_StillBlocksReboot()
    {
        string root = Directory.CreateTempSubdirectory("fw-storage-fail-").FullName;
        await File.WriteAllBytesAsync(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);

        TrackingOps ops = new("ACE01.POP001.001.core") { FetchThrows = true };
        RouterOsV6UpgradeExecutor executor = CreateExecutorWithoutV7Target(ops, root);
        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "ACE01.POP001.001.core", version: "6.49.10"),
            CreateJob("ACE", "10.0.0.50"),
            CreateDevice("ACE01.POP001.001.core", "6.49.10"),
            CreateAssessment("10.0.0.50"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeFailed, result.Outcome);
        Assert.Equal(0, ops.RebootCount);
        Assert.True(ops.FetchCalled);
    }

    /// <summary>
    /// An 8-byte package where the router reports only 3 bytes after the 50 ms fetch timeout must give
    /// <c>UpgradeFailed</c>, with the fetch attempted and no reboot.
    /// </summary>
    [Fact]
    public async Task IncompleteFetchSizeMismatch_StillBlocksReboot()
    {
        string root = Directory.CreateTempSubdirectory("fw-storage-partial-").FullName;
        await File.WriteAllBytesAsync(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4, 5, 6, 7, 8]);

        TrackingOps ops = new("BR01.POP011.016.Tommy")
        {
            ExpectedFetchSizeBytes = 8,
            IncompleteFetchSizeBytes = 3,
        };

        RouterOsV6UpgradeExecutor executor = CreateExecutorWithoutV7Target(
            ops,
            root,
            fetchTimeout: TimeSpan.FromMilliseconds(50));
        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "BR01.POP011.016.Tommy", version: "6.49.10"),
            CreateJob("Tommy", "198.18.240.11"),
            CreateDevice("BR01.POP011.016.Tommy", "6.49.10"),
            CreateAssessment("198.18.240.11"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeFailed, result.Outcome);
        Assert.Equal(0, ops.RebootCount);
        Assert.True(ops.FetchCalled);
    }

    /// <summary>
    /// A failing backup service must give <c>UpgradeOutcome.BackupFailed</c> before any fetch (<c>FetchCalled</c>
    /// false) and without a reboot.
    /// </summary>
    [Fact]
    public async Task BackupFailure_StillBlocksUpgrade()
    {
        string root = Directory.CreateTempSubdirectory("fw-storage-backup-").FullName;
        await File.WriteAllBytesAsync(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);

        TrackingOps ops = new("BR01.POP011.016.Tommy");
        RouterOsV6UpgradeExecutor executor = CreateExecutorWithoutV7Target(ops, root, backup: new FailingBackup());
        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "BR01.POP011.016.Tommy", version: "6.49.10"),
            CreateJob("Tommy", "198.18.240.11"),
            CreateDevice("BR01.POP011.016.Tommy", "6.49.10"),
            CreateAssessment("198.18.240.11"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.BackupFailed, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.Equal(0, ops.RebootCount);
    }

    /// <summary>
    /// Reads a production source file from <c>src/MikrotikWorker</c> (resolved relative to the test output directory)
    /// so source-level storage-model invariants can be asserted.
    /// </summary>
    private static string ReadProductionSource(string folder, string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MikrotikWorker",
            folder,
            fileName));
        Assert.True(File.Exists(path), $"Missing source {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Builds a scheduled job with the given router name and IP for the storage-model scenarios.</summary>
    private static MikrotikScheduledJob CreateJob(string name, string ip) =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "storage.csv",
            RowNumber = 2,
            RouterName = name,
            RouterIp = IPAddress.Parse(ip),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    /// <summary>
    /// Builds <see cref="MikrotikDeviceInfo"/> (mipsbe, hEX PoE) with the given identity and version.
    /// </summary>
    private static MikrotikDeviceInfo CreateDevice(string identity, string version) =>
        new()
        {
            Identity = identity,
            Version = version,
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

    /// <summary>
    /// Builds a ready-for-upgrade, compatible assessment for the given router IP targeting RouterOS 6.49.23.
    /// </summary>
    private static DeviceCompatibilityAssessment CreateAssessment(string routerIp) =>
        new()
        {
            RouterIp = routerIp,
            OverallStatus = DeviceAssessmentStatus.ReadyForUpgrade,
            RouterOsVersionClassification = RouterOsVersionClassification.OlderThanTargetV6,
            TargetRouterOsVersion = "6.49.23",
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Builds a v6-only <see cref="RouterOsV6UpgradeExecutor"/> with no RouterOS 7 target, a backup directory under
    /// the firmware root, and a reconnect factory returning a fully populated 6.49.23 connection.
    /// </summary>
    private static RouterOsV6UpgradeExecutor CreateExecutorWithoutV7Target(
        TrackingOps ops,
        string firmwareRoot,
        IDeviceBackupService? backup = null,
        TimeSpan? fetchTimeout = null)
    {
        IOptions<FirmwareUpgradeOptions> upgradeOptions = Options.Create(new FirmwareUpgradeOptions
        {
            Enabled = true,
            AdvertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
            FetchTimeout = fetchTimeout ?? TimeSpan.FromSeconds(30),
            ReconnectInterval = TimeSpan.FromMilliseconds(10),
            RebootTimeout = TimeSpan.FromSeconds(30),
            PostUpgradeVerifyTimeout = TimeSpan.FromSeconds(5),
            BackupFileReadyTimeout = TimeSpan.FromMilliseconds(50),
            BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(5),
            BackupDirectory = Path.Combine(firmwareRoot, "Backups"),
        });
        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot });
        IOptions<MikroTikOptions> mikrotikOptions = Options.Create(new MikroTikOptions { Port = 8728 });
        IMikrotikConnectionFactory factory =
            new ScriptedMikrotikConnectionFactory(_ =>
                FakeMikrotikConnections.CreateFullyPopulated(identity: ops.Identity, version: "6.49.23"));

        return new RouterOsV6UpgradeExecutor(
            ops,
            factory,
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(firmwareRoot),
                firmwareFilesOptions),
            backup ?? new SucceedingBackup(),
            new FixedRouterOsV7TargetProvider(null),
            new LegacyFleetUpgradeAutomationCleanup(ops, NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance),
            new RouterboardFirmwareUpgradeService(
                ops,
                factory,
                upgradeOptions,
                mikrotikOptions,
                TimeProvider.System,
                NullLogger<RouterboardFirmwareUpgradeService>.Instance),
            new PassingOspfMigrationService(),
            new PassingOspfStaticSummaryRedistributionService(),
            upgradeOptions,
            mikrotikOptions,
            TimeProvider.System,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
    }

    /// <summary>
    /// Router operations fake that models fetch results (success, failure, or a short/incomplete file) and the
    /// version progression across a reboot, for the firmware upload storage scenarios.
    /// </summary>
    private sealed class TrackingOps(string identity) : IMikrotikRouterOperations
    {
        /// <summary>
        /// 1-based index of the latest reboot into <see cref="VersionsAfterReboot"/>; zero before any reboot.
        /// </summary>
        private int _rebootIndex;

        /// <summary>System identity reported by the fake router.</summary>
        public string Identity { get; } = identity;

        /// <summary>RouterOS version reported before the first reboot.</summary>
        public string InitialVersion { get; init; } = "6.49.10";

        /// <summary>
        /// Versions reported after each successive reboot (default 6.49.23); the last entry repeats once exhausted.
        /// </summary>
        public List<string> VersionsAfterReboot { get; init; } = ["6.49.23"];

        /// <summary>
        /// When true, <see cref="FetchUrlToFileAsync"/> throws and the file is never reported as present.
        /// </summary>
        public bool FetchThrows { get; init; }

        /// <summary>Size reported for the fetched package once fetched; defaults to 4 bytes when null.</summary>
        public long? ExpectedFetchSizeBytes { get; init; }

        /// <summary>
        /// When set, the fetched file is reported with this (smaller) size, simulating a truncated transfer.
        /// </summary>
        public long? IncompleteFetchSizeBytes { get; init; }

        /// <summary>
        /// True once <see cref="FetchUrlToFileAsync"/> has been invoked, i.e. the firmware package fetch was
        /// attempted.
        /// </summary>
        public bool FetchCalled { get; private set; }

        /// <summary>Number of reboots requested so far.</summary>
        public int RebootCount { get; private set; }

        /// <summary>
        /// RouterOS version reported right now: <see cref="InitialVersion"/> before any reboot, then the entry of
        /// <see cref="VersionsAfterReboot"/> selected by the latest reboot.
        /// </summary>
        private string CurrentVersion =>
            RebootCount == 0
                ? InitialVersion
                : VersionsAfterReboot[Math.Min(_rebootIndex - 1, VersionsAfterReboot.Count - 1)];

        /// <summary>
        /// Marks the fetch as called and throws <see cref="InvalidOperationException"/> when <see
        /// cref="FetchThrows"/> is set.
        /// </summary>
        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            if (FetchThrows)
            {
                throw new InvalidOperationException("simulated fetch failure");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns null before the fetch (or after a failed fetch); afterwards the incomplete size if configured,
        /// otherwise <see cref="ExpectedFetchSizeBytes"/> or 4 bytes.
        /// </summary>
        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            if (!FetchCalled || FetchThrows)
            {
                return Task.FromResult<RouterFileInfo?>(null);
            }

            if (IncompleteFetchSizeBytes is long incomplete)
            {
                return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, incomplete));
            }

            long size = ExpectedFetchSizeBytes ?? 4;
            return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, size));
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

        /// <summary>No-op: pretends the <c>.rsc</c> configuration export was written on the router.</summary>
        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>No-op: pretends the binary <c>.backup</c> system backup was saved on the router.</summary>
        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>No-op: pretends the file was deleted from the router.</summary>
        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>Counts the reboot and selects the next post-reboot version.</summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCount++;
            _rebootIndex = RebootCount;
            return Task.CompletedTask;
        }

        /// <summary>Returns device info for the configured identity at the current version.</summary>
        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string fallbackHost,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MikrotikDeviceInfo
            {
                Identity = Identity,
                Version = CurrentVersion,
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
            });

        /// <summary>Returns a single enabled <c>routeros-mipsbe</c> package at the numeric current version.</summary>
        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            string version = CurrentVersion;
            string numeric = RouterOsVersion.TryParse(version, out RouterOsVersion parsed)
                ? parsed.ToString()
                : version;
            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>(
            [
                new("routeros-mipsbe", numeric, Disabled: false, Bundle: null),
            ]);
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

        /// <summary>No-op: pretends the RouterBOARD firmware upgrade command was accepted.</summary>
        public Task UpgradeRouterboardAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Backup fake that always succeeds with remote cleanup completed.</summary>
    private sealed class SucceedingBackup : IDeviceBackupService
    {
        /// <summary>Returns success with remote cleanup completed.</summary>
        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceBackupResult
            {
                Succeeded = true,
                RemoteCleanupSucceeded = true,
            });
    }

    /// <summary>
    /// Backup fake that always fails with <c>simulated backup failure</c>, used to prove no firmware is uploaded when
    /// the backup fails.
    /// </summary>
    private sealed class FailingBackup : IDeviceBackupService
    {
        /// <summary>Returns a failed result with the reason <c>simulated backup failure</c>.</summary>
        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceBackupResult
            {
                Succeeded = false,
                FailureReason = "simulated backup failure",
            });
    }
}
