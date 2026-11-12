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

namespace MikrotikWorker.Tests.Upgrade.Ospf;
public sealed class OspfMigrationExecutorGateTests
{
    [Fact]
    public async Task OspfValidationFailure_IsNotReportedAsSuccessfulUpgrade()
    {
        TrackingOps ops = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new FailingOspfMigrationService(validation: true));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"),
            CreateJob(),
            CreateDevice("6.49.23"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.OspfPlanValidationFailed, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task V7TargetUnavailable_DeletesPlanAndDoesNotApplyV7Ospf()
    {
        TrackingOps ops = new();
        RecordingOspfMigrationService ospf = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, ospf, v7Target: null);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"),
            CreateJob(),
            CreateDevice("6.49.23"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.TargetUnavailable, result.Outcome);
        Assert.True(ospf.CaptureCalled);
        Assert.False(ospf.ApplyCalled);
        Assert.True(ospf.DeleteCalled);
        Assert.False(ops.FetchCalled);
    }

    [Theory]
    [InlineData("ACE01.POP001.001.core")]
    [InlineData("BR12.POP123.456.edge")]
    public async Task AceOrBr_OspfValidationFailure_BlocksV7(string identity)
    {
        TrackingOps ops = new() { Identity = identity };
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new FailingOspfMigrationService(validation: true));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: identity, version: "6.49.23"),
            CreateJob(),
            CreateDevice("6.49.23", identity: identity),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.OspfPlanValidationFailed, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Theory]
    [InlineData("ACE01.POP001.001.core")]
    [InlineData("BR12.POP123.456.edge")]
    public async Task AceOrBr_AlreadyV7_ApplyFailure_BlocksSuccess(string identity)
    {
        TrackingOps ops = new()
        {
            Identity = identity,
            PostUpgradeVersion = "7.23.8",
        };
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new FailingOspfMigrationService(apply: true));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: identity, version: "7.23.8"),
            CreateJob(),
            CreateDevice("7.23.8", identity: identity),
            CreateAssessment(DeviceAssessmentStatus.AlreadyAtTarget),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.OspfMigrationApplyFailed, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterOs7, result.CompletedStage);
        Assert.False(ops.FetchCalled);
    }

    [Fact]
    public async Task AlreadyV7_ApplyFailure_IsNotSuccessfulUpgrade()
    {
        TrackingOps ops = new()
        {
            Identity = "AP01.POP001.001.access",
            PostUpgradeVersion = "7.23.8",
        };
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new FailingOspfMigrationService(apply: true));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
            CreateJob(),
            CreateDevice("7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.AlreadyAtTarget),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.OspfMigrationApplyFailed, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterOs7, result.CompletedStage);
        Assert.False(ops.FetchCalled);
    }

    private static RouterOsV6UpgradeExecutor CreateExecutor(
        TrackingOps ops,
        IOspfMigrationService ospf,
        string? v7Target = "7.23.8")
    {
        string root = Path.Combine(Path.GetTempPath(), "fw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        IOptions<FirmwareUpgradeOptions> upgradeOptions = Options.Create(new FirmwareUpgradeOptions
        {
            Enabled = true,
            AdvertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
            FetchTimeout = TimeSpan.FromSeconds(5),
            ReconnectInterval = TimeSpan.FromMilliseconds(10),
            RebootTimeout = TimeSpan.FromSeconds(5),
            PostUpgradeVerifyTimeout = TimeSpan.FromSeconds(5),
            BackupFileReadyTimeout = TimeSpan.FromMilliseconds(100),
            BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(20),
            BackupDirectory = Path.Combine(root, "Backups"),
        });
        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = root });
        IOptions<MikroTikOptions> mikrotikOptions = Options.Create(new MikroTikOptions { Port = 8728 });

        return new RouterOsV6UpgradeExecutor(
            ops,
            new ThrowingMikrotikConnectionFactory(new IOException("no reconnect")),
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(root),
                firmwareFilesOptions),
            new SucceedingBackup(),
            new FixedRouterOsV7TargetProvider(v7Target),
            new LegacyFleetUpgradeAutomationCleanup(
                ops,
                NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance),
            new RouterboardFirmwareUpgradeService(
                ops,
                new ThrowingMikrotikConnectionFactory(new IOException("no reconnect")),
                upgradeOptions,
                mikrotikOptions,
                TimeProvider.System,
                NullLogger<RouterboardFirmwareUpgradeService>.Instance),
            ospf,
            new PassingOspfStaticSummaryRedistributionService(),
            upgradeOptions,
            mikrotikOptions,
            TimeProvider.System,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
    }

    private static MikrotikScheduledJob CreateJob() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "ospf-gate.csv",
            RowNumber = 2,
            RouterName = "AP01",
            RouterIp = IPAddress.Parse("10.0.0.50"),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-5),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    private static MikrotikDeviceInfo CreateDevice(string version, string identity = "AP01.POP001.001.access") =>
        new()
        {
            Identity = identity,
            Version = version,
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.23.8",
        };

    private static DeviceCompatibilityAssessment CreateAssessment(
        DeviceAssessmentStatus status = DeviceAssessmentStatus.ReadyForUpgrade) =>
        new()
        {
            RouterIp = "10.0.0.50",
            OverallStatus = status,
            RouterOsVersionClassification = RouterOsVersionClassification.OlderThanTargetV6,
            TargetRouterOsVersion = "7.23.8",
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    private sealed class FailingOspfMigrationService(bool validation = false, bool apply = false)
        : IOspfMigrationService
    {
        public Task<OspfGateResult> CaptureAndValidateAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            string identity,
            string routerOsVersion,
            string? architecture,
            bool persistPlan,
            CancellationToken cancellationToken)
        {
            if (validation)
            {
                return Task.FromResult(OspfGateResult.ValidationFailed(
                    "synthetic validation failure",
                    PassingOspfMigrationService.CreateMinimalValidPlan(job, identity, routerOsVersion, architecture)));
            }

            return Task.FromResult(OspfGateResult.Ok(
                PassingOspfMigrationService.CreateMinimalValidPlan(job, identity, routerOsVersion, architecture)));
        }

        public Task<OspfApplyGateResult> ApplyAndVerifyAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            string identity,
            OspfMigrationPlan? inMemoryPlan,
            CancellationToken cancellationToken,
            string? managementHost = null)
        {
            if (apply)
            {
                return Task.FromResult(OspfApplyGateResult.Failed(
                    UpgradeOutcome.OspfMigrationApplyFailed,
                    "synthetic apply failure"));
            }

            return Task.FromResult(OspfApplyGateResult.Ok(
                inMemoryPlan ?? PassingOspfMigrationService.CreateMinimalValidPlan(job, identity, "7.23.8", null)));
        }

        public void DeletePlan(MikrotikScheduledJob job)
        {
        }
    }

    private sealed class RecordingOspfMigrationService : IOspfMigrationService
    {
        public bool CaptureCalled { get; private set; }

        public bool ApplyCalled { get; private set; }

        public bool DeleteCalled { get; private set; }

        public Task<OspfGateResult> CaptureAndValidateAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            string identity,
            string routerOsVersion,
            string? architecture,
            bool persistPlan,
            CancellationToken cancellationToken)
        {
            CaptureCalled = true;
            return Task.FromResult(OspfGateResult.Ok(
                PassingOspfMigrationService.CreateMinimalValidPlan(job, identity, routerOsVersion, architecture)));
        }

        public Task<OspfApplyGateResult> ApplyAndVerifyAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            string identity,
            OspfMigrationPlan? inMemoryPlan,
            CancellationToken cancellationToken,
            string? managementHost = null)
        {
            ApplyCalled = true;
            return Task.FromResult(OspfApplyGateResult.Ok(
                inMemoryPlan ?? PassingOspfMigrationService.CreateMinimalValidPlan(job, identity, "7.23.8", null)));
        }

        public void DeletePlan(MikrotikScheduledJob job) => DeleteCalled = true;
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
            Task.FromResult(new DeviceBackupResult { Succeeded = true, RemoteCleanupSucceeded = true });
    }

    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        public bool FetchCalled { get; private set; }

        public bool RebootCalled { get; private set; }

        public string Identity { get; init; } = "AP01.POP001.001.access";

        public string PostUpgradeVersion { get; init; } = "6.49.23";

        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MikrotikDeviceInfo
            {
                Identity = Identity,
                Version = PostUpgradeVersion,
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
                RouterboardCurrentFirmware = "7.23.8",
                RouterboardUpgradeFirmware = "7.23.8",
            });

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            bool v7 = RouterOsVersion.TryParse(PostUpgradeVersion, out RouterOsVersion ver) && ver.IsV7OrLater;
            string main = v7 ? "routeros" : "routeros-mipsbe";
            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>(
            [
                new(main, PostUpgradeVersion, false),
            ]);
        }

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

        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, 4));

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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            return Task.CompletedTask;
        }

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            return Task.CompletedTask;
        }

        public Task UpgradeRouterboardAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
    }
}
