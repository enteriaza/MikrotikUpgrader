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
/// Guards the corrected resource model: NPK fetch is not gated on free flash capacity.
/// </summary>
public sealed class FirmwareUploadStorageModelTests
{
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

    [Theory]
    [InlineData("BR01.POP011.016.Tommy", DeviceType.Br)]
    [InlineData("ACE01.POP001.001.core", DeviceType.Ace)]
    public void LowFlashDeviceTypes_RemainEligibleForRouterOs7(string identity, DeviceType expectedType)
    {
        DeviceIdentityClassification classification = DeviceIdentityClassifier.Classify(identity);

        Assert.Equal(expectedType, classification.DeviceType);
        Assert.True(classification.ShouldUpgradeToV7);
    }

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

    private static MikrotikDeviceInfo CreateDevice(string identity, string version) =>
        new()
        {
            Identity = identity,
            Version = version,
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

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

    private sealed class TrackingOps(string identity) : IMikrotikRouterOperations
    {
        private int _rebootIndex;

        public string Identity { get; } = identity;

        public string InitialVersion { get; init; } = "6.49.10";

        public List<string> VersionsAfterReboot { get; init; } = ["6.49.23"];

        public bool FetchThrows { get; init; }

        public long? ExpectedFetchSizeBytes { get; init; }

        public long? IncompleteFetchSizeBytes { get; init; }

        public bool FetchCalled { get; private set; }

        public int RebootCount { get; private set; }

        private string CurrentVersion =>
            RebootCount == 0
                ? InitialVersion
                : VersionsAfterReboot[Math.Min(_rebootIndex - 1, VersionsAfterReboot.Count - 1)];

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

        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCount++;
            _rebootIndex = RebootCount;
            return Task.CompletedTask;
        }

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

        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task RemoveSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task RemoveSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpgradeRouterboardAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class SucceedingBackup : IDeviceBackupService
    {
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

    private sealed class FailingBackup : IDeviceBackupService
    {
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
