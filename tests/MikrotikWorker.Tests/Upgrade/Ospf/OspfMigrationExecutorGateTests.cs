// -----------------------------------------------------------------------------
// File: OspfMigrationExecutorGateTests.cs
//
// Description:
// Tests for OSPF capture/validate and apply/verify gates as used by the
// upgrade executor.
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

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Tests for OSPF capture/validate and apply/verify executor gates.
/// </summary>
public sealed class OspfMigrationExecutorGateTests
{
    /// <summary>
    /// Scenario: the OSPF migration service fails plan validation before the v6 upgrade starts.
    /// Expected: the outcome is OspfPlanValidationFailed (not UpgradeSucceeded) and neither package fetch nor reboot happens.
    /// Invariant: an OSPF validation failure must block the firmware upgrade and never be reported as success.
    /// </summary>
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

    /// <summary>
    /// Scenario: OSPF capture succeeds but no validated v7 target version is available.
    /// Expected: the outcome is TargetUnavailable, the captured plan is deleted, apply is not called and no package is fetched.
    /// Invariant: a stored OSPF plan must not outlive an upgrade that cannot proceed, and v7 OSPF is never applied.
    /// </summary>
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

    /// <summary>
    /// Scenario: ACE and BR identities (e.g. ACE01.POP001.001.core, BR12.POP123.456.edge) on RouterOS 6.49.23 whose OSPF plan validation fails.
    /// Expected: the outcome is OspfPlanValidationFailed, not UpgradeSucceeded, and no fetch or reboot occurs.
    /// Invariant: the OSPF validation gate applies to ACE and BR routers exactly as it does to access routers.
    /// </summary>
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

    /// <summary>
    /// Scenario: ACE and BR identities already on 7.23.8 (AlreadyAtTarget) whose OSPF apply step fails.
    /// Expected: the outcome is OspfMigrationApplyFailed (not UpgradeSucceeded), the completed stage is RouterOs7, and no package is fetched.
    /// Invariant: an already-upgraded ACE/BR router still fails the job if its OSPF migration cannot be applied.
    /// </summary>
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

    /// <summary>
    /// Scenario: an access router already on 7.23.8 whose OSPF apply step fails.
    /// Expected: the outcome is OspfMigrationApplyFailed, not UpgradeSucceeded, the completed stage is RouterOs7, and no package is fetched.
    /// Invariant: reaching RouterOS 7 does not count as success while the OSPF migration is incomplete.
    /// </summary>
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

    /// <summary>
    /// Builds a <c>RouterOsV6UpgradeExecutor</c> with fast timeouts, a temp firmware root, the tracking ops, a
    /// non-reconnecting connection factory, a succeeding backup, the supplied OSPF service and the given v7 target.
    /// </summary>
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

    /// <summary>
    /// Creates a scheduled upgrade job for a fixed router with a window around the current time.
    /// </summary>
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

    /// <summary>
    /// Builds a mipsbe hEX PoE device snapshot reporting the given RouterOS version and identity, with RouterBOARD firmware already at 7.23.8.
    /// <param name="version">RouterOS version reported by the device.</param>
    /// <param name="identity">System identity reported by the device.</param>
    /// </summary>
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

    /// <summary>
    /// Builds a compatibility assessment targeting RouterOS 7.23.8 with the given overall status.
    /// <param name="status">Overall assessment status; defaults to ready-for-upgrade.</param>
    /// </summary>
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

    /// <summary>
    /// OSPF migration service fake that fails validation or apply on request and otherwise returns a valid minimal
    /// plan.
    /// </summary>
    private sealed class FailingOspfMigrationService(bool validation = false, bool apply = false)
        : IOspfMigrationService
    {
        /// <summary>
        /// Fails validation with a synthetic error when configured to; otherwise returns a valid minimal plan.
        /// </summary>
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

        /// <summary>
        /// Fails with <see cref="UpgradeOutcome.OspfMigrationApplyFailed"/> when configured to; otherwise succeeds with the supplied (or a minimal) plan.
        /// </summary>
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

        /// <summary>
        /// No-op; plan deletion is not relevant to the failure scenarios.
        /// </summary>
        public void DeletePlan(MikrotikScheduledJob job)
        {
        }
    }

    /// <summary>
    /// OSPF migration service fake that records capture, apply and delete calls and otherwise succeeds with a valid
    /// minimal plan.
    /// </summary>
    private sealed class RecordingOspfMigrationService : IOspfMigrationService
    {
        /// <summary>
        /// Whether <see cref="CaptureAndValidateAsync"/> was invoked.
        /// </summary>
        public bool CaptureCalled { get; private set; }

        /// <summary>
        /// Whether <see cref="ApplyAndVerifyAsync"/> was invoked.
        /// </summary>
        public bool ApplyCalled { get; private set; }

        /// <summary>
        /// Whether <see cref="DeletePlan"/> was invoked.
        /// </summary>
        public bool DeleteCalled { get; private set; }

        /// <summary>
        /// Records the call and returns a valid minimal plan.
        /// </summary>
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

        /// <summary>
        /// Records the call and succeeds with the supplied (or a minimal) plan.
        /// </summary>
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

        /// <summary>
        /// Records that the plan was deleted.
        /// </summary>
        public void DeletePlan(MikrotikScheduledJob job) => DeleteCalled = true;
    }

    /// <summary>
    /// Backup service fake that reports a successful pre-upgrade backup with remote cleanup without touching the
    /// device.
    /// </summary>
    private sealed class SucceedingBackup : IDeviceBackupService
    {
        /// <summary>
        /// Reports a successful pre-upgrade backup, including remote cleanup, without touching the device.
        /// </summary>
        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceBackupResult { Succeeded = true, RemoteCleanupSucceeded = true });
    }

    /// <summary>
    /// Router operations fake that simulates a healthy mipsbe device and records whether a firmware fetch or reboot
    /// was requested, so tests can assert OSPF gates block them.
    /// </summary>
    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        /// <summary>
        /// Whether a firmware package fetch was requested; must stay <see langword="false"/> when OSPF gates block the upgrade.
        /// </summary>
        public bool FetchCalled { get; private set; }

        /// <summary>
        /// Whether a reboot was requested; must stay <see langword="false"/> when OSPF gates block the upgrade.
        /// </summary>
        public bool RebootCalled { get; private set; }

        /// <summary>
        /// System identity reported by <see cref="ReadDeviceInfoAsync"/>.
        /// </summary>
        public string Identity { get; init; } = "AP01.POP001.001.access";

        /// <summary>
        /// RouterOS version reported by device-info and package reads.
        /// </summary>
        public string PostUpgradeVersion { get; init; } = "6.49.23";

        /// <summary>
        /// Returns a mipsbe hEX PoE device using the configured identity and version.
        /// </summary>
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

        /// <summary>
        /// Returns the single main routeros package matching <see cref="PostUpgradeVersion"/> (v7 package name for v7 versions, mipsbe v6 otherwise).
        /// </summary>
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

        /// <summary>
        /// No-op; configuration export is simulated as successful.
        /// </summary>
        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; system backup is simulated as successful.
        /// </summary>
        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Reports that the file exists with a small fixed size.
        /// </summary>
        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, 4));

        /// <summary>
        /// No-op; the file is treated as immediately present.
        /// </summary>
        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; file removal is simulated as successful.
        /// </summary>
        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Records that a fetch was requested without downloading anything.
        /// </summary>
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

        /// <summary>
        /// Records that a reboot was requested without rebooting.
        /// </summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op; RouterBOARD firmware upgrade is not exercised.
        /// </summary>
        public Task UpgradeRouterboardAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Reports that no matching system scripts exist.
        /// </summary>
        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// No-op; there are no scripts to remove.
        /// </summary>
        public Task RemoveSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Reports that no matching schedulers exist.
        /// </summary>
        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// No-op; there are no schedulers to remove.
        /// </summary>
        public Task RemoveSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
