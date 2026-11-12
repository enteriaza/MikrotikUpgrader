// -----------------------------------------------------------------------------
// File: AutomaticUpgradePathTests.cs
//
// Description:
// Unit tests for the automatic multi-stage upgrade path decisions and
// outcomes.
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
using tik4net;

namespace MikrotikWorker.Tests.Upgrade;
/// <summary>
/// Non-live coverage for the automatic upgrade path: eligible devices are always upgraded to the
/// configured RouterOS 7 target (v6 stepping first when the device runs RouterOS 6 older than 6.49.23).
/// There is no operator-selectable execution mode. Includes a synthetic lab fixture for
/// 198.18.240.11 — not a live router result. Uses fakes only.
/// </summary>
public sealed class AutomaticUpgradePathTests
{
    /// <summary>
    /// Component packages bundled into <c>routeros-mipsbe</c> on the lab RouterOS 6 router, used to build a realistic
    /// inventory.
    /// </summary>
    private static readonly string[] LabBundledComponents =
    [
        "system",
        "wireless",
        "hotspot",
        "dhcp",
        "routing",
        "mpls",
        "ppp",
        "security",
        "advanced-tools",
        "ipv6",
    ];

    /// <summary>
    /// Scenario: A mipsbe router on RouterOS 6.49.19, below the 6.49.23 stepping release, runs the full executor with
    /// scripted operations whose reboots report 6.49.23 and then 7.23.8. The identity varies across the cases: the
    /// generic name Mikrotik, a blank name, and AP, DCL, ACE and BR style names.
    /// Expected: The upgrade succeeds with the RouterBOARD firmware stage completed and 7.23.8 as the post-upgrade
    /// version. Three backups and two reboots occur, the first resolved target is the v6 stepping version and the
    /// second is a RouterOS 7 target, the 7.23.8 package is fetched, and FleetUpgradeInstall.rsc is not. The identity
    /// classifies to the expected device type.
    /// Invariant: Stepping and then RouterOS 7 is chosen regardless of device identity.
    /// </summary>
    [Theory]
    [InlineData("Mikrotik", DeviceType.Unknown)]
    [InlineData(" ", DeviceType.Unknown)]
    [InlineData("AP01.POP001.001.access", DeviceType.Ap)]
    [InlineData("DCL12.POP123.456.some.device", DeviceType.Dcl)]
    [InlineData("ACE01.POP001.001.core", DeviceType.Ace)]
    [InlineData("BR12.POP123.456.edge", DeviceType.Br)]
    public async Task RouterOs6BelowStepping_AnyIdentity_StepsThenUpgradesToRouterOs7(
        string identity,
        DeviceType expectedType)
    {
        string effectiveIdentity = string.IsNullOrWhiteSpace(identity) ? " " : identity;
        StageTrackingOps ops = new(effectiveIdentity)
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: effectiveIdentity,
                version: "6.49.23",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, backup, packages.FirmwareRoot, reconnect, packages, v7);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: effectiveIdentity,
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(),
            CreateDevice(effectiveIdentity, "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
        Assert.Equal(1, v7.CaptureCount);
        Assert.True(ops.SchedulerCountCalls > 0);
        Assert.Equal(2, packages.ResolvedTargets.Count);
        Assert.Equal(RouterOsUpgradeVersions.V6Stepping, packages.ResolvedTargets[0]);
        Assert.True(packages.ResolvedTargets[1].IsV7OrLater);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(
            ops.FetchedFiles,
            f => f.Equals("FleetUpgradeInstall.rsc", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expectedType, DeviceIdentityClassifier.Classify(identity).DeviceType);
    }

    /// <summary>
    /// Scenario: Synthetic lab fixture only; not a live router observation or live upgrade result. A router named
    /// Mikrotik at 198.18.240.11 on RouterOS 6.49.19 (mipsbe) with lab-bundled inventory runs the executor, with
    /// reboots reporting 6.49.23 and then 7.23.8.
    /// Expected: The upgrade succeeds with the RouterBOARD firmware stage completed and 7.23.8 mipsbe after the
    /// upgrade. Three backups and two reboots occur, routeros-mipsbe-6.49.23.npk is the first file fetched, the
    /// 7.23.8 package is fetched later, and the resolved targets run from the v6 stepping version to a RouterOS 7
    /// target.
    /// Invariant: Only fixture data is exercised; the result says nothing about any real router.
    /// </summary>
    [Fact]
    public async Task SyntheticLabFixture_198_18_240_11_StepsThenUpgradesToRouterOs7()
    {
        // SyntheticFixture only — not a live router observation or live upgrade result.
        const string identity = "Mikrotik";
        const string routerIp = "198.18.240.11";
        StageTrackingOps ops = new(identity)
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
            IncludeLabBundledInventory = true,
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: identity,
                version: "6.49.23",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            v7);

        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "lab-synthetic.csv",
            RowNumber = 2,
            RouterName = identity,
            RouterIp = IPAddress.Parse(routerIp),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: identity,
                version: "6.49.19",
                architecture: "mipsbe"),
            job,
            new MikrotikDeviceInfo
            {
                Identity = identity,
                Version = "6.49.19",
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
            },
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal("mipsbe", result.PostUpgradeArchitecture);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
        Assert.Contains("routeros-mipsbe-6.49.23.npk", ops.FetchedFiles[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.Equal(RouterOsUpgradeVersions.V6Stepping, packages.ResolvedTargets[0]);
        Assert.True(packages.ResolvedTargets[^1].IsV7OrLater);
    }

    /// <summary>
    /// Scenario: A router named Mikrotik already on 6.49.23 or 6.49.24 (at or above the stepping release) runs the
    /// executor, with a single reboot reporting 7.23.8.
    /// Expected: The upgrade succeeds with the RouterBOARD firmware stage completed and 7.23.8 as the post-upgrade
    /// version. One reboot and one RouterOS 7 target capture occur, exactly one package is resolved and it is a
    /// RouterOS 7 package, and no 6.49.23 file is fetched.
    /// Invariant: Devices at or above the stepping release skip v6 stepping and go straight to RouterOS 7.
    /// </summary>
    [Theory]
    [InlineData("6.49.23")]
    [InlineData("6.49.24")]
    public async Task RouterOs6AtOrAboveStepping_SkipsStepping_AndUpgradesDirectlyToRouterOs7(string version)
    {
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = version,
            VersionsAfterReboot = ["7.23.8"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "7.23.8",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            v7);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: version,
                architecture: "mipsbe"),
            CreateJob(),
            CreateDevice("Mikrotik", version),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(1, ops.RebootCount);
        Assert.Equal(1, v7.CaptureCount);
        RouterOsVersion only = Assert.Single(packages.ResolvedTargets);
        Assert.True(only.IsV7OrLater);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
    }

    /// <summary>
    /// A router on 7.22.0 that reports 7.23.8 after its reboot gives <c>UpgradeSucceeded</c> with
    /// <c>PostUpgradeVersion</c> 7.23.8 after exactly one reboot. Every package the resolver was asked for is a
    /// RouterOS 7 target, so no v6 stepping stage is used.
    /// </summary>
    [Fact]
    public async Task RouterOs7OlderThanTarget_UpgradesDirectlyToTarget()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            InitialVersion = "7.22.0",
            VersionsAfterReboot = ["7.23.8"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "AP01.POP001.001.access",
                version: "7.23.8",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, backup, packages.FirmwareRoot, reconnect, packages);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "AP01.POP001.001.access",
                version: "7.22.0",
                architecture: "mipsbe"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.22.0"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(1, ops.RebootCount);
        Assert.All(packages.ResolvedTargets, static t => Assert.True(t.IsV7OrLater));
    }

    /// <summary>
    /// A router already on 7.23.8 gives <c>SkippedAlreadyAtTarget</c>, with <c>CompletedStage</c>
    /// RouterboardFirmware and <c>PostUpgradeVersion</c> 7.23.8. <c>RebootInitiated</c> is false, there are no
    /// reboots or fetches and the package resolver is never called. The job outcome maps to <c>AlreadyUpToDate</c>.
    /// </summary>
    [Fact]
    public async Task RouterOs7AtTarget_ReportsAlreadyAtTarget_WithoutPackageStageOrReboot()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access") { InitialVersion = "7.23.8" };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, backup, packages.FirmwareRoot, packageResolver: packages);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "AP01.POP001.001.access",
                version: "7.23.8",
                architecture: "mipsbe"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.AlreadyAtTarget),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.SkippedAlreadyAtTarget, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.False(result.RebootInitiated);
        Assert.Equal(0, ops.RebootCount);
        Assert.Empty(ops.FetchedFiles);
        Assert.Empty(packages.ResolvedTargets);
        Assert.Equal(
            (ScheduledJobTerminalState.AlreadyUpToDate, true),
            RouterJobExecutor.MapUpgradeOutcome(result.Outcome));
    }

    /// <summary>
    /// With upgrades administratively disabled the result is <c>SkippedUpgradeDisabled</c>, with
    /// <c>CompletedStage</c> None and a detail containing 'administratively disabled'. No backup, reboot, scheduler
    /// lookup, fetch, package resolution or v7 target capture happens. The job maps to
    /// <c>ScheduledJobTerminalState.Failed</c> and is not reported as a success.
    /// </summary>
    [Fact]
    public async Task UpgradeDisabled_DoesNotMutate_AndIsNeverReportedAsSuccess()
    {
        StageTrackingOps ops = new("Mikrotik") { InitialVersion = "6.49.19", VersionsAfterReboot = ["6.49.23"] };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            packageResolver: packages,
            v7TargetProvider: v7,
            enabled: false);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.SkippedUpgradeDisabled, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Contains("administratively disabled", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backup.CallCount);
        Assert.Equal(0, ops.RebootCount);
        Assert.Equal(0, ops.SchedulerCountCalls);
        Assert.Empty(ops.FetchedFiles);
        Assert.Empty(packages.ResolvedTargets);
        Assert.Equal(0, v7.CaptureCount);

        (ScheduledJobTerminalState terminal, bool success) = RouterJobExecutor.MapUpgradeOutcome(result.Outcome);
        Assert.Equal(ScheduledJobTerminalState.Failed, terminal);
        Assert.False(success);
    }

    /// <summary>
    /// A RouterOS 5.26 router gives <c>PolicyBlocked</c> with a detail containing 'Unsupported RouterOS major
    /// version'. There is no backup, reboot or fetch, and <c>MapUpgradeOutcome</c> maps the outcome to
    /// <c>ScheduledJobTerminalState.Blocked</c>.
    /// </summary>
    [Fact]
    public async Task UnsupportedRouterOsMajor_IsBlocked_WithoutMutation()
    {
        StageTrackingOps ops = new("Mikrotik") { InitialVersion = "5.26" };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, backup, packages.FirmwareRoot, packageResolver: packages);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "Mikrotik", version: "5.26", architecture: "mipsbe"),
            CreateJob(),
            CreateDevice("Mikrotik", "5.26"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.PolicyBlocked, result.Outcome);
        Assert.Contains("Unsupported RouterOS major version", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, backup.CallCount);
        Assert.Equal(0, ops.RebootCount);
        Assert.Empty(ops.FetchedFiles);
        Assert.Equal(ScheduledJobTerminalState.Blocked, RouterJobExecutor.MapUpgradeOutcome(result.Outcome).Terminal);
    }

    /// <summary>
    /// A 6.49.19 router that comes back from the stepping reboot on 6.48.1 gives <c>UpgradeVersionMismatch</c> with
    /// <c>CompletedStage</c> None. Exactly one backup and one reboot occurred, the v7 target was never captured, no
    /// resolved package is a RouterOS 7 one, and no v7 mutation happened. The job maps to <c>Failed</c>.
    /// </summary>
    [Fact]
    public async Task SteppingVerificationFailure_DoesNotContinueToRouterOs7_OrClaimSuccess()
    {
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.48.1"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "Mikrotik", architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            v7,
            rebootTimeout: TimeSpan.FromSeconds(5),
            postUpgradeVerifyTimeout: TimeSpan.FromMilliseconds(80));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeVersionMismatch, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.Equal(0, v7.CaptureCount);
        Assert.DoesNotContain(packages.ResolvedTargets, static t => t.IsV7OrLater);
        AssertNoV7Mutation(ops);
        Assert.Equal(ScheduledJobTerminalState.Failed, RouterJobExecutor.MapUpgradeOutcome(result.Outcome).Terminal);
    }

    /// <summary>
    /// When every reconnect attempt after the first reboot throws, the result is <c>UpgradeTimedOut</c> with
    /// <c>CompletedStage</c> None, and no v7 mutation follows.
    /// </summary>
    [Fact]
    public async Task SteppingRebootTimeout_PreservesTimeoutOutcome_AndDoesNotContinue()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingThrowFactory reconnect = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            rebootTimeout: TimeSpan.FromMilliseconds(40),
            postUpgradeVerifyTimeout: TimeSpan.FromMilliseconds(40),
            timeProvider: time);

        Task<UpgradeExecutionResult> running = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCount >= 1, TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromMilliseconds(80));
        UpgradeExecutionResult result = await running;

        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        AssertNoV7Mutation(ops);
    }

    /// <summary>
    /// Cancelling once the post-reboot read has started gives <c>UpgradeInterrupted</c> with <c>CompletedStage</c>
    /// None and no v7 mutation. <c>MapUpgradeOutcome</c> maps it to <c>ScheduledJobTerminalState.Cancelled</c>.
    /// </summary>
    [Fact]
    public async Task Cancellation_DuringSteppingVerification_PreservesInterruptedOutcome()
    {
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23"],
            HangPostRebootReadUntilCancelled = true,
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.23",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            rebootTimeout: TimeSpan.FromSeconds(30),
            postUpgradeVerifyTimeout: TimeSpan.FromSeconds(30));

        using CancellationTokenSource cts = new();
        Task<UpgradeExecutionResult> running = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            cts.Token);

        await WaitForAsync(() => ops.PostRebootReadStarted.Task.IsCompleted, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        UpgradeExecutionResult result = await running;

        Assert.Equal(UpgradeOutcome.UpgradeInterrupted, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        AssertNoV7Mutation(ops);
        Assert.Equal(ScheduledJobTerminalState.Cancelled, RouterJobExecutor.MapUpgradeOutcome(result.Outcome).Terminal);
    }

    /// <summary>
    /// When the legacy FleetUpgrade scheduler cleanup fails, the result is
    /// <c>LegacyFirmwareAutomationCleanupFailed</c> with <c>CompletedStage</c> None. The cleanup did query
    /// schedulers (<c>SchedulerCountCalls</c> above 0), but no reboot, backup, fetch or package resolution
    /// followed.
    /// </summary>
    [Fact]
    public async Task LegacyCleanupFailure_SkipsFirmwareMutation()
    {
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23"],
            PresentSchedulers = [LegacyFleetUpgradeObjectNames.SchedulerName],
            ThrowOnSchedulerRemove = new InvalidOperationException("cleanup boom"),
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            packageResolver: packages,
            v7TargetProvider: v7);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.LegacyFirmwareAutomationCleanupFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Equal(0, ops.RebootCount);
        Assert.Equal(0, backup.CallCount);
        Assert.Empty(ops.FetchedFiles);
        Assert.Empty(packages.ResolvedTargets);
        Assert.True(ops.SchedulerCountCalls > 0);
    }

    /// <summary>
    /// A CSV with no execution-mode column must still be accepted by the importer, and the resulting 6.49.19 job
    /// runs the automatic path through 6.49.23 and then 7.23.8. The outcome is <c>UpgradeSucceeded</c> with
    /// <c>CompletedStage</c> RouterboardFirmware, the v7 target is captured once, and a RouterOS 7 package was
    /// resolved.
    /// </summary>
    [Fact]
    public async Task CsvWithoutExecutionMode_ImportsAndRunsAutomaticPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"auto-path-import-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(
            path,
            """
            datestart,datestop,routername,routerip
            01/12/2026 08:00:00,02/12/2026 18:00:00,Mikrotik,198.18.240.11
            """);

        try
        {
            ScheduledJobCsvParser parser = new(
                Options.Create(new ScheduledJobsOptions()),
                new CsvDateTimeParser(Options.Create(new CsvOptions
                {
                    Culture = "en-ZA",
                    TimeZoneId = "Africa/Johannesburg",
                    DateTimeFormats =
                    [
                        "dd/MM/yyyy HH:mm:ss",
                        "dd/MM/yyyy HH:mm",
                    ],
                })));

            MikrotikScheduledJob? accepted = null;
            await foreach (MikrotikScheduledJob job in parser.EnumerateJobsAsync(path, Guid.NewGuid(), CancellationToken.None))
            {
                accepted = job;
            }

            Assert.NotNull(accepted);

            CountingPackageResolver packages = CreateCountingResolver();
            CountingV7TargetProvider v7 = new("7.23.8");
            StageTrackingOps ops = new("Mikrotik")
            {
                InitialVersion = "6.49.19",
                VersionsAfterReboot = ["6.49.23", "7.23.8"],
            };
            CountingBackup backup = new();
            ScriptedMikrotikConnectionFactory reconnect = new(_ =>
                FakeMikrotikConnections.CreateFullyPopulated(
                    identity: "Mikrotik",
                    version: "6.49.23",
                    architecture: "mipsbe"));
            RouterOsV6UpgradeExecutor executor = CreateExecutor(
                ops,
                backup,
                packages.FirmwareRoot,
                reconnect,
                packages,
                v7);

            MikrotikScheduledJob executable = new()
            {
                ImportId = accepted.ImportId,
                SourceFileName = accepted.SourceFileName,
                RowNumber = accepted.RowNumber,
                RouterName = accepted.RouterName,
                RouterIp = accepted.RouterIp,
                DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
                DateStopUtc = DateTime.UtcNow.AddHours(1),
            };

            UpgradeExecutionResult result = await executor.ExecuteAsync(
                FakeMikrotikConnections.CreateFullyPopulated(
                    identity: "Mikrotik",
                    version: "6.49.19",
                    architecture: "mipsbe"),
                executable,
                CreateDevice("Mikrotik", "6.49.19"),
                CreateAssessment(),
                "admin",
                "secret",
                static () => true,
                CancellationToken.None);

            Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
            Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
            Assert.Equal(1, v7.CaptureCount);
            Assert.Contains(packages.ResolvedTargets, static t => t.IsV7OrLater);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Asserts that no RouterOS 7 package was fetched and at most one reboot happened, i.e. the router was never
    /// moved to RouterOS 7.
    /// </summary>
    private static void AssertNoV7Mutation(StageTrackingOps ops)
    {
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedUrls, u => u.Contains("7.23.8", StringComparison.Ordinal));
        Assert.True(ops.RebootCount <= 1);
    }

    /// <summary>
    /// Asserts that no <c>/system/routerboard/upgrade</c> command was issued on the recorded connection.
    /// </summary>
    private static void AssertNoRouterboardUpgrade(CommandRecordingConnection recording) =>
        Assert.DoesNotContain(
            recording.Commands,
            static c => c.Contains("/system/routerboard/upgrade", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Creates a counting package resolver over a temporary firmware directory holding stub RouterOS 6.49.23 and
    /// 7.23.8 mipsbe packages.
    /// </summary>
    private static CountingPackageResolver CreateCountingResolver()
    {
        string root = Directory.CreateTempSubdirectory("v6only-fw-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [5, 6, 7, 8]);
        return new CountingPackageResolver(root);
    }

    /// <summary>
    /// Builds a <see cref="RouterOsV6UpgradeExecutor"/> wired to the supplied fakes with short test timeouts.
    /// Optional resolver, RouterOS 7 target provider, clock and enabled flag can be overridden; the verify timeout is
    /// capped at the reboot timeout.
    /// </summary>
    private static RouterOsV6UpgradeExecutor CreateExecutor(
        StageTrackingOps ops,
        IDeviceBackupService backup,
        string firmwareRoot,
        IMikrotikConnectionFactory? connectionFactory = null,
        IUpgradePackageResolver? packageResolver = null,
        IRouterOsV7TargetProvider? v7TargetProvider = null,
        TimeSpan? rebootTimeout = null,
        TimeSpan? postUpgradeVerifyTimeout = null,
        TimeProvider? timeProvider = null,
        bool enabled = true)
    {
        TimeSpan reboot = rebootTimeout ?? TimeSpan.FromSeconds(5);
        TimeSpan verify = postUpgradeVerifyTimeout ?? reboot;
        if (verify > reboot)
        {
            verify = reboot;
        }

        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot });
        IUpgradePackageResolver resolver = packageResolver
            ?? new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(firmwareRoot),
                firmwareFilesOptions);
        IMikrotikConnectionFactory factory =
            connectionFactory ?? new ThrowingMikrotikConnectionFactory(new IOException("no reconnect"));
        IOptions<FirmwareUpgradeOptions> upgradeOptions = Options.Create(new FirmwareUpgradeOptions
        {
            Enabled = enabled,
            AdvertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
            FetchTimeout = TimeSpan.FromSeconds(5),
            ReconnectInterval = TimeSpan.FromMilliseconds(10),
            RebootTimeout = reboot,
            PostUpgradeVerifyTimeout = verify,
            BackupFileReadyTimeout = TimeSpan.FromMilliseconds(100),
            BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(20),
        });
        IOptions<MikroTikOptions> mikrotikOptions = Options.Create(new MikroTikOptions { Port = 8728 });
        TimeProvider clock = timeProvider ?? TimeProvider.System;

        return new RouterOsV6UpgradeExecutor(
            ops,
            factory,
            resolver,
            backup,
            v7TargetProvider ?? new FixedRouterOsV7TargetProvider("7.23.8"),
            new LegacyFleetUpgradeAutomationCleanup(
                ops,
                NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance),
            new RouterboardFirmwareUpgradeService(
                ops,
                factory,
                upgradeOptions,
                mikrotikOptions,
                clock,
                NullLogger<RouterboardFirmwareUpgradeService>.Instance),
            new PassingOspfMigrationService(),
            new PassingOspfStaticSummaryRedistributionService(),
            upgradeOptions,
            mikrotikOptions,
            clock,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
    }

    /// <summary>Builds a minimal scheduled job (router <c>R1</c>, 10.0.0.50) for executor calls.</summary>
    private static MikrotikScheduledJob CreateJob() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "R1",
            RouterIp = IPAddress.Parse("10.0.0.50"),
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
    /// Builds a compatible, path-eligible assessment targeting RouterOS 6.49.23 with the given overall status.
    /// </summary>
    private static DeviceCompatibilityAssessment CreateAssessment(
        DeviceAssessmentStatus status = DeviceAssessmentStatus.ReadyForUpgrade) =>
        new()
        {
            RouterIp = "10.0.0.50",
            OverallStatus = status,
            RouterOsVersionClassification = RouterOsVersionClassification.OlderThanTargetV6,
            TargetRouterOsVersion = "6.49.23",
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Polls a condition until timeout or success.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    /// <summary>Backup fake that always succeeds and counts how many backups were requested.</summary>
    private sealed class CountingBackup : IDeviceBackupService
    {
        /// <summary>Number of pre-upgrade backups requested.</summary>
        public int CallCount { get; private set; }

        /// <summary>Counts the call and returns success with remote cleanup completed.</summary>
        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new DeviceBackupResult
            {
                Succeeded = true,
                RemoteCleanupSucceeded = true,
            });
        }
    }

    /// <summary>
    /// Package resolver decorator that records each requested target version while delegating to the production
    /// resolver over a temporary firmware directory.
    /// </summary>
    private sealed class CountingPackageResolver(string firmwareRoot) : IUpgradePackageResolver
    {
        /// <summary>Production resolver over the temporary firmware directory.</summary>
        private readonly UpgradePackageResolver _inner = new(
            new StaticFirmwarePackageCatalog(firmwareRoot),
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot }));

        /// <summary>Temporary firmware directory the resolver reads from.</summary>
        public string FirmwareRoot { get; } = firmwareRoot;

        /// <summary>
        /// Target versions passed to <see cref="Resolve"/>, in call order (v6 stepping first, then RouterOS 7).
        /// </summary>
        public List<RouterOsVersion> ResolvedTargets { get; } = [];

        /// <summary>Records the target version and delegates to the production resolver.</summary>
        public UpgradePackageResolution Resolve(
            string? deviceArchitecture,
            IReadOnlyList<InstalledPackageInfo> installedPackages,
            RouterOsVersion targetVersion)
        {
            ResolvedTargets.Add(targetVersion);
            return _inner.Resolve(deviceArchitecture, installedPackages, targetVersion);
        }
    }

    /// <summary>RouterOS 7 target provider decorator that counts how often a snapshot is captured.</summary>
    private sealed class CountingV7TargetProvider(string? versionText) : IRouterOsV7TargetProvider
    {
        /// <summary>Fixed provider supplying the configured target version.</summary>
        private readonly FixedRouterOsV7TargetProvider _inner = new(versionText);

        /// <summary>Number of times <see cref="CaptureSnapshot"/> was called.</summary>
        public int CaptureCount { get; private set; }

        /// <summary>Source path reported by the inner provider.</summary>
        public string VersionFilePath => _inner.VersionFilePath;

        /// <summary>Returns the inner snapshot without counting it.</summary>
        public RouterOsV7TargetSnapshot? TryGetCurrent() => _inner.TryGetCurrent();

        /// <summary>Counts the capture and returns the inner snapshot.</summary>
        public RouterOsV7TargetSnapshot? CaptureSnapshot()
        {
            CaptureCount++;
            return _inner.CaptureSnapshot();
        }
    }

    /// <summary>
    /// Connection factory that counts attempts and always fails, simulating a router that stays unreachable.
    /// </summary>
    private sealed class CountingThrowFactory : IMikrotikConnectionFactory
    {
        /// <summary>Number of reconnect attempts made.</summary>
        public int Attempts { get; private set; }

        /// <summary>Counts the attempt and throws <see cref="IOException"/> (<c>still down</c>).</summary>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new IOException("still down");
        }
    }

    /// <summary>
    /// Stateful <see cref="IMikrotikRouterOperations"/> fake that models a router across upgrade stages: the reported
    /// version changes per reboot, and fetches, reboots and cleanup are recorded.
    /// </summary>
    private sealed class StageTrackingOps(string identity) : IMikrotikRouterOperations
    {
        /// <summary>
        /// 1-based index of the latest reboot into <see cref="VersionsAfterReboot"/>; zero before any reboot.
        /// </summary>
        private int _rebootIndex;

        /// <summary>System identity reported by the fake router.</summary>
        public string Identity { get; } = identity;

        /// <summary>
        /// RouterOS version reported before the first reboot (default 6.49.19, older than the 6.49.23 stepping
        /// target).
        /// </summary>
        public string InitialVersion { get; init; } = "6.49.19";

        /// <summary>
        /// RouterOS versions reported after each successive reboot; the last entry repeats once the list is
        /// exhausted.
        /// </summary>
        public List<string> VersionsAfterReboot { get; init; } = [];

        /// <summary>
        /// When true, RouterOS 6 inventories also list the lab bundled components (system, wireless, hotspot, ...) as
        /// members of the <c>routeros-mipsbe</c> bundle.
        /// </summary>
        public bool IncludeLabBundledInventory { get; init; }

        /// <summary>
        /// When true, the first post-reboot device-info read blocks until cancelled, simulating a router that never
        /// answers.
        /// </summary>
        public bool HangPostRebootReadUntilCancelled { get; init; }

        /// <summary>
        /// Completed when a post-reboot device-info read begins, so tests can cancel at a deterministic point.
        /// </summary>
        public TaskCompletionSource PostRebootReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>RouterBOARD firmware version currently running on the fake router.</summary>
        public string RouterboardCurrentFirmware { get; init; } = "7.23.8";

        /// <summary>
        /// RouterBOARD firmware version available for upgrade; equal to the current firmware means no RouterBOARD
        /// upgrade is needed.
        /// </summary>
        public string RouterboardUpgradeFirmware { get; init; } = "7.23.8";

        /// <summary>Names of legacy fleet-upgrade scripts currently present on the fake router.</summary>
        public List<string> PresentScripts { get; init; } = [];

        /// <summary>Names of legacy fleet-upgrade schedulers currently present on the fake router.</summary>
        public List<string> PresentSchedulers { get; init; } = [];

        /// <summary>
        /// Ordered log of removals (<c>script:name</c> / <c>scheduler:name</c>) used to assert cleanup order.
        /// </summary>
        public List<string> RemovalOrder { get; } = [];

        /// <summary>
        /// When set, <see cref="RemoveSchedulersByNameAsync"/> throws this exception instead of removing the
        /// scheduler.
        /// </summary>
        public Exception? ThrowOnSchedulerRemove { get; init; }

        /// <summary>
        /// When set, <see cref="RemoveSystemScriptsByNameAsync"/> throws this exception instead of removing the
        /// script.
        /// </summary>
        public Exception? ThrowOnScriptRemove { get; init; }

        /// <summary>Number of reboots requested so far.</summary>
        public int RebootCount { get; private set; }

        /// <summary>
        /// Number of scheduler inspections performed; zero proves cleanup never looked for schedulers.
        /// </summary>
        public int SchedulerCountCalls { get; private set; }

        /// <summary>Number of script inspections performed; zero proves cleanup never looked for scripts.</summary>
        public int ScriptCountCalls { get; private set; }

        /// <summary>Total scheduler and script inspections; zero proves legacy cleanup was never attempted.</summary>
        public int CleanupInspectionCount => SchedulerCountCalls + ScriptCountCalls;

        /// <summary>
        /// Largest number of bundled components returned by any package listing; zero when none were listed.
        /// </summary>
        public int MaxBundledComponentCount { get; private set; }

        /// <summary>
        /// Per-file sizes (case-insensitive) reported by <see cref="FindFileAsync"/>, letting tests simulate
        /// truncated or mismatched uploads.
        /// </summary>
        public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Destination file names passed to <see cref="FetchUrlToFileAsync"/>, in call order.</summary>
        public List<string> FetchedFiles { get; } = [];

        /// <summary>Source URLs passed to <see cref="FetchUrlToFileAsync"/>, in call order.</summary>
        public List<string> FetchedUrls { get; } = [];

        /// <summary>
        /// RouterOS version reported right now: <see cref="InitialVersion"/> before any reboot, then the entry of
        /// <see cref="VersionsAfterReboot"/> selected by the latest reboot.
        /// </summary>
        private string CurrentVersion =>
            RebootCount == 0
                ? InitialVersion
                : VersionsAfterReboot[Math.Min(_rebootIndex - 1, VersionsAfterReboot.Count - 1)];

        /// <summary>
        /// Returns device info at the current stage version. After a reboot, blocks until cancelled when <see
        /// cref="HangPostRebootReadUntilCancelled"/> is set (signalling <see cref="PostRebootReadStarted"/> first).
        /// </summary>
        public async Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default)
        {
            if (RebootCount >= 1 && HangPostRebootReadUntilCancelled)
            {
                PostRebootReadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new MikrotikDeviceInfo
            {
                Identity = Identity,
                Version = CurrentVersion,
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
                RouterboardCurrentFirmware = RouterboardCurrentFirmware,
                RouterboardUpgradeFirmware = RouterboardUpgradeFirmware,
            };
        }

        /// <summary>
        /// Returns the main package (<c>routeros-mipsbe</c> on RouterOS 6, <c>routeros</c> on 7+). With <see
        /// cref="IncludeLabBundledInventory"/> on RouterOS 6 it also lists the lab bundled components and tracks the
        /// largest bundle seen.
        /// </summary>
        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            string version = CurrentVersion;
            string numeric = RouterOsVersion.TryParse(version, out RouterOsVersion parsed)
                ? parsed.ToString()
                : version;

            bool isV7 = RouterOsVersion.TryParse(version, out RouterOsVersion ver) && ver.IsV7OrLater;
            string mainName = isV7 ? "routeros" : "routeros-mipsbe";
            List<InstalledPackageInfo> packages =
            [
                new(mainName, numeric, false),
            ];

            if (IncludeLabBundledInventory && !isV7)
            {
                foreach (string component in LabBundledComponents)
                {
                    packages.Add(new InstalledPackageInfo(component, numeric, false, "routeros-mipsbe"));
                }

                MaxBundledComponentCount = Math.Max(MaxBundledComponentCount, LabBundledComponents.Length);
            }

            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>(packages);
        }

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

        /// <summary>
        /// Reports the file with the size registered in <see cref="FileSizes"/>, or a 4-byte file that matches the
        /// stub firmware payloads.
        /// </summary>
        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            if (FileSizes.TryGetValue(fileName, out long known))
            {
                return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, known));
            }

            return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, 4));
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

        /// <summary>No-op: pretends the file was deleted from the router.</summary>
        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>Records the URL and destination file name without transferring anything.</summary>
        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchedUrls.Add(url);
            FetchedFiles.Add(destinationFileName);
            return Task.CompletedTask;
        }

        /// <summary>Counts the reboot and selects the next post-reboot version.</summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCount++;
            _rebootIndex = RebootCount;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Counts the call and returns how many entries in <see cref="PresentScripts"/> match the name exactly.
        /// </summary>
        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default)
        {
            ScriptCountCalls++;
            return Task.FromResult(PresentScripts.Count(n => n.Equals(scriptName, StringComparison.Ordinal)));
        }

        /// <summary>
        /// Throws <see cref="ThrowOnScriptRemove"/> when configured; otherwise records <c>script:name</c> in <see
        /// cref="RemovalOrder"/> and removes matching entries from <see cref="PresentScripts"/>.
        /// </summary>
        public Task RemoveSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnScriptRemove is not null)
            {
                throw ThrowOnScriptRemove;
            }

            RemovalOrder.Add("script:" + scriptName);
            PresentScripts.RemoveAll(n => n.Equals(scriptName, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Counts the call and returns how many entries in <see cref="PresentSchedulers"/> match the name exactly.
        /// </summary>
        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default)
        {
            SchedulerCountCalls++;
            return Task.FromResult(PresentSchedulers.Count(n => n.Equals(schedulerName, StringComparison.Ordinal)));
        }

        /// <summary>
        /// Throws <see cref="ThrowOnSchedulerRemove"/> when configured; otherwise records <c>scheduler:name</c> in
        /// <see cref="RemovalOrder"/> and removes matching entries from <see cref="PresentSchedulers"/>.
        /// </summary>
        public Task RemoveSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnSchedulerRemove is not null)
            {
                throw ThrowOnSchedulerRemove;
            }

            RemovalOrder.Add("scheduler:" + schedulerName);
            PresentSchedulers.RemoveAll(n => n.Equals(schedulerName, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op: RouterBOARD upgrades are not modelled here; tests assert on recorded router commands instead.
        /// </summary>
        public Task UpgradeRouterboardAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
