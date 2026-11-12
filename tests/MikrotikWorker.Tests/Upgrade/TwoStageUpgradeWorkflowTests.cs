// -----------------------------------------------------------------------------
// File: TwoStageUpgradeWorkflowTests.cs
//
// Description:
// Coverage for the two-stage (v6 stepping then RouterOS 7) upgrade workflow
// sequencing.
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
/// Coverage for v6-stepping then RouterOS 7 workflow sequencing.
/// </summary>
public sealed class TwoStageUpgradeWorkflowTests
{
    /// <summary>
    /// Scenario: Routers with ACE (ACE01.POP001.001.core) and BR (BR12.POP123.456.edge) identities run the full
    /// executor, with reboots reporting 6.49.23 and then 7.23.8.
    /// Expected: The upgrade succeeds with the RouterBOARD firmware stage completed and 7.23.8 as the post-upgrade
    /// version. Three backups and two reboots occur, both the 6.49.23 and 7.23.8 packages are fetched, and the
    /// identity classifies to the expected device type.
    /// Invariant: ACE and BR identities keep their RouterOS 7 eligibility.
    /// </summary>
    [Theory]
    [InlineData("ACE01.POP001.001.core", DeviceType.Ace)]
    [InlineData("BR12.POP123.456.edge", DeviceType.Br)]
    public async Task AceOrBr_RunsThroughV7AndRouterboard(string identity, DeviceType expectedType)
    {
        StageTrackingOps ops = new(identity)
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: identity, version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: identity, version: "6.49.10"),
            CreateJob(),
            CreateDevice(identity, "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.Equal(expectedType, DeviceIdentityClassifier.Classify(identity).DeviceType);
        Assert.True(DeviceIdentityClassifier.Classify(identity).ShouldUpgradeToV7);
    }

    /// <summary>
    /// Scenario: Routers with AP, DCL, generic (MikroTik, router-01) and empty identities run the full executor, with
    /// reboots reporting 6.49.23 and then 7.23.8.
    /// Expected: The upgrade succeeds with the RouterBOARD firmware stage completed and 7.23.8 as the post-upgrade
    /// version. Three backups and two reboots occur, the router scheduler is inspected for legacy FleetUpgrade
    /// automation, the stub records no RouterBOARD upgrade call, both packages are fetched, and
    /// FleetUpgradeInstall.rsc is not fetched.
    /// Invariant: Every identity type is eligible for the second stage (backup, upload and reboot).
    /// </summary>
    [Theory]
    [InlineData("AP01.POP001.001.access", DeviceType.Ap)]
    [InlineData("DCL12.POP123.456.some.device.name", DeviceType.Dcl)]
    [InlineData("MikroTik", DeviceType.Unknown)]
    [InlineData("router-01", DeviceType.Unknown)]
    [InlineData("", DeviceType.Unknown)]
    public async Task EligibleIdentities_RunSecondStageBackupUploadAndReboot(
        string identity,
        DeviceType expectedType)
    {
        StageTrackingOps ops = new(string.IsNullOrEmpty(identity) ? " " : identity)
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: string.IsNullOrWhiteSpace(identity) ? " " : identity,
                version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: string.IsNullOrWhiteSpace(identity) ? " " : identity,
                version: "6.49.10"),
            CreateJob(),
            CreateDevice(string.IsNullOrWhiteSpace(identity) ? " " : identity, "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
        Assert.True(ops.SchedulerCountCalls > 0);
        Assert.DoesNotContain(
            ops.FetchedFiles,
            f => f.Equals("FleetUpgradeInstall.rsc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.Equal(expectedType, DeviceIdentityClassifier.Classify(identity).DeviceType);
    }

    /// <summary>
    /// A router whose identity is blank still completes the full 6.49.10 to 6.49.23 to 7.23.8 chain. The outcome is
    /// <c>UpgradeSucceeded</c> at RouterboardFirmware with <c>PostUpgradeVersion</c> 7.23.8, after three backups
    /// and two reboots.
    /// </summary>
    [Fact]
    public async Task UnavailableIdentity_StillProceedsToV7()
    {
        StageTrackingOps ops = new(" ")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: " ", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: " ", version: "6.49.10"),
            CreateJob(),
            CreateDevice(" ", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
    }

    /// <summary>
    /// Scenario: An ACE router completes the v6 stage, but the second post-reboot device-information read (the
    /// classification re-read that follows the stage-1 verification) fails.
    /// Expected: The upgrade still succeeds with the RouterBOARD firmware stage completed and 7.23.8 as the
    /// post-upgrade version. Three backups and two reboots occur and the 7.23.8 package is fetched.
    /// Invariant: Classification falls back to the already verified ACE identity, which remains eligible for RouterOS
    /// 7.
    /// </summary>
    [Fact]
    public async Task IdentityRereadFailure_KeepsVerifiedAceAndStillProceedsToV7()
    {
        StageTrackingOps ops = new("ACE01.POP001.001.core")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
            // First post-reboot read is stage-1 verify; second is classification re-read.
            FailOnPostRebootDeviceInfoReadNumber = 2,
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "ACE01.POP001.001.core", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "ACE01.POP001.001.core", version: "6.49.10"),
            CreateJob(),
            CreateDevice("ACE01.POP001.001.core", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        // Classification falls back to the pre-reread ACE identity; ACE remains v7-eligible.
        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// Short verify poll so mismatch is reported before the absolute reboot deadline.
    /// If the router comes back from the 6.49.23 stage reporting 6.48.1, the result is
    /// <c>UpgradeVersionMismatch</c> after one backup and one reboot. No 7.23.8 file is ever fetched.
    /// </summary>
    [Fact]
    public async Task FirstStageVerificationFailure_PreventsSecondStage()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.48.1"], // wrong version
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access"));
        // Short verify poll so mismatch is reported before the absolute reboot deadline.
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            reconnect,
            rebootTimeout: TimeSpan.FromSeconds(5),
            postUpgradeVerifyTimeout: TimeSpan.FromMilliseconds(80));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeVersionMismatch, result.Outcome);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// When the backup taken after the 6.49.23 stage fails, the result is <c>BackupFailed</c> after two backup
    /// calls and a single reboot. No 7.23.8 file is fetched.
    /// </summary>
    [Fact]
    public async Task SecondStageBackupFailure_PreventsV7UploadAndReboot()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23"],
        };
        CountingBackup backup = new() { FailOnCallNumber = 2 };
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.BackupFailed, result.Outcome);
        Assert.Equal(2, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// With only the 6.49.23 package in the firmware folder, the 6.49.23 stage completes (one backup, one reboot)
    /// and the result is then <c>PackageResolutionFailed</c>. No 7.23.8 file is fetched.
    /// </summary>
    [Fact]
    public async Task MissingV7Package_PreventsSecondReboot()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23"],
        };
        CountingBackup backup = new();
        string root = Directory.CreateTempSubdirectory("twostage-nov7-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.PackageResolutionFailed, result.Outcome);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// An AP already on 6.49.23 skips the stepping stage and goes straight to 7.23.8. The outcome is
    /// <c>UpgradeSucceeded</c> at RouterboardFirmware with <c>PostUpgradeVersion</c> 7.23.8, after two backups
    /// (before the v7 install and before RouterBOARD) and one reboot. A 7.23.8 file is fetched and no 6.49.23 file
    /// is.
    /// </summary>
    [Fact]
    public async Task AlreadyAtStage1_ApDevice_StillRunsSecondStage()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["7.23.8"],
            InitialVersion = "6.49.23",
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.23"),
            CreateAssessment(DeviceAssessmentStatus.AlreadyAtTarget, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(2, backup.CallCount); // v7 backup + post-v7 backup before RouterBOARD
        Assert.Equal(1, ops.RebootCount);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: Routers on 6.49.18, 6.49.19 and 6.49.20 run the executor with reboots reporting 6.49.23 and then
    /// 7.23.8, while the package catalog deliberately includes a 6.49.19 package.
    /// Expected: The upgrade succeeds with the RouterBOARD firmware stage completed and 7.23.8 as the post-upgrade
    /// version. Three backups and two reboots occur, and the 6.49.23 and 7.23.8 packages are fetched.
    /// Invariant: The 6.49.19 package is never selected for stepping, by file name or by download URL.
    /// </summary>
    [Theory]
    [InlineData("6.49.18")]
    [InlineData("6.49.19")]
    [InlineData("6.49.20")]
    public async Task OlderV6_StepsTo64923_ThenProceedsToV7(string currentVersion)
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
            InitialVersion = currentVersion,
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        // Deliberately include a 6.49.19 package that must never be selected for stepping.
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.19.npk"), [9, 9, 9, 9]);
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: currentVersion),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", currentVersion),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.19", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedUrls, u => u.Contains("6.49.19", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: Routers already on 6.49.23, 6.49.24 or 6.50.1 run the executor with a single reboot reporting
    /// 7.23.8.
    /// Expected: The upgrade succeeds with the RouterBOARD firmware stage completed and 7.23.8 as the post-upgrade
    /// version. Two backups and one reboot occur, and only the 7.23.8 package is fetched, never 6.49.23.
    /// Invariant: Routers at or above the stepping release never receive stepping packages.
    /// </summary>
    [Theory]
    [InlineData("6.49.23")]
    [InlineData("6.49.24")]
    [InlineData("6.50.1")]
    public async Task AtOrNewerV6_NeverReceivesSteppingPackages_GoesToV7(string currentVersion)
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["7.23.8"],
            InitialVersion = currentVersion,
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: currentVersion),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", currentVersion),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(2, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// An ACE router on 6.49.24, newer than the 6.49.23 stepping version, skips stepping. The outcome is
    /// <c>UpgradeSucceeded</c> at RouterboardFirmware with <c>PostUpgradeVersion</c> 7.23.8, after two backups and
    /// one reboot. No 6.49.23 file is fetched and a 7.23.8 file is.
    /// </summary>
    [Fact]
    public async Task AceOnNewerThanStepping_SkipsSteppingAndRunsV7()
    {
        StageTrackingOps ops = new("ACE01.POP001.001.core")
        {
            InitialVersion = "6.49.24",
            VersionsAfterReboot = ["7.23.8"],
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "ACE01.POP001.001.core", version: "6.49.24"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "ACE01.POP001.001.core", version: "6.49.24"),
            CreateJob(),
            CreateDevice("ACE01.POP001.001.core", "6.49.24"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(2, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// An ACE router on 6.49.20 steps to 6.49.23 and then to 7.23.8, with three backups and two reboots. The
    /// outcome is <c>UpgradeSucceeded</c> at RouterboardFirmware. The 6.49.23 and 7.23.8 packages are fetched,
    /// while a 6.49.19 package in the folder is ignored.
    /// </summary>
    [Fact]
    public async Task AceOnOlderV6_StepsTo64923_ThenRunsV7()
    {
        StageTrackingOps ops = new("ACE01.POP001.001.core")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
            InitialVersion = "6.49.20",
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.19.npk"), [9, 9, 9, 9]);
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "ACE01.POP001.001.core", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "ACE01.POP001.001.core", version: "6.49.20"),
            CreateJob(),
            CreateDevice("ACE01.POP001.001.core", "6.49.20"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.19", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: Regression from a live run: a router with an unknown identity already on 6.49.23 takes the automatic
    /// RouterOS 7 upgrade with a single reboot reporting 7.23.8.
    /// Expected: The upgrade succeeds with the RouterBOARD firmware stage completed and 7.23.8 mipsbe afterwards. Two
    /// backups and one reboot occur, no 6.49.23 file is fetched, the fetched file is exactly
    /// routeros-7.23.8-mipsbe.npk, and the installed main package is named routeros.
    /// Invariant: The RouterOS 7 package is fetched as routeros-7.23.8-mipsbe.npk, not the routeros-mipsbe naming
    /// used for RouterOS 6.
    /// </summary>
    [Fact]
    public async Task AlreadyAtV6Stepping_SkipsV6Fetch_VerifiesV7PackageName()
    {
        // Live regression: 6.49.23 Unknown → automatic RouterOS 7 upgrade → routeros/7.23.8 (not routeros-mipsbe).
        StageTrackingOps ops = new("Mikrotik")
        {
            VersionsAfterReboot = ["7.23.8"],
            InitialVersion = "6.49.23",
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "7.23.8",
                architecture: "mipsbe",
                boardName: "hEX PoE"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.23",
                architecture: "mipsbe",
                boardName: "hEX PoE"),
            CreateJob(),
            CreateDevice("Mikrotik", "6.49.23"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal("mipsbe", result.PostUpgradeArchitecture);
        Assert.Equal(2, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Equals("routeros-7.23.8-mipsbe.npk", StringComparison.Ordinal));
        Assert.Equal(
            "routeros",
            (await ops.ListInstalledPackagesAsync(
                FakeMikrotikConnections.CreateFullyPopulated(),
                CancellationToken.None))[0].Name);
    }

    /// <summary>
    /// A router on 7.22.0 upgrades straight to 7.23.8: <c>UpgradeSucceeded</c> at RouterboardFirmware after two
    /// backups and one reboot. No 6.49.23 file is fetched and a 7.23.8 file is.
    /// </summary>
    [Fact]
    public async Task RouterOs7Older_UpgradesDirectlyWithoutV6Packages()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["7.23.8"],
            InitialVersion = "7.22.0",
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.22.0"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.22.0"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal(2, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: An AP router already on the 7.23.8 target runs the executor.
    /// Expected: The outcome is SkippedAlreadyAtTarget with the RouterBOARD firmware stage completed. One backup is
    /// taken, no reboot occurs and no package is fetched.
    /// Invariant: A router at the RouterOS 7 target gets the OSPF reconcile, backup and RouterBOARD steps but no
    /// package fetch or reboot.
    /// </summary>
    [Fact]
    public async Task RouterOs7AtTarget_ReconcilesOspfWithoutPackageStage()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access") { InitialVersion = "7.23.8" };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.AlreadyAtTarget, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        // Already on ROS7 target: OSPF reconcile + backup + RouterBOARD; no package fetch/reboot.
        Assert.Equal(UpgradeOutcome.SkippedAlreadyAtTarget, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(0, ops.RebootCount);
        Assert.Empty(ops.FetchedFiles);
    }

    /// <summary>
    /// A router already on 7.24.0, newer than the 7.23.8 target, gives <c>SkippedNewerThanTarget</c> with no backup
    /// and no fetched files.
    /// </summary>
    [Fact]
    public async Task RouterOs7NewerThanTarget_NeverDowngrades()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access") { InitialVersion = "7.24.0" };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.24.0"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.24.0"),
            CreateAssessment(DeviceAssessmentStatus.AlreadyAtTarget, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.SkippedNewerThanTarget, result.Outcome);
        Assert.Equal(0, backup.CallCount);
        Assert.Empty(ops.FetchedFiles);
    }

    /// <summary>
    /// With no RouterOS 7 target available, the 6.49.23 stepping stage still completes (one backup, one reboot).
    /// The result is then <c>TargetUnavailable</c>, and no 7.23.8 file is fetched.
    /// </summary>
    [Fact]
    public async Task MissingV7Target_BlocksEligibleDeviceAfterStepping()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23"],
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            reconnect,
            v7TargetProvider: new FixedRouterOsV7TargetProvider(null));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.TargetUnavailable, result.Outcome);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// The target file changes to 7.24.0 just before the first fetch, but the job keeps the 7.23.8 snapshot it
    /// captured at the start. It succeeds at RouterboardFirmware with two backups, fetches 7.23.8 and never 7.24.0,
    /// while the provider itself now reports 7.24.0.
    /// </summary>
    [Fact]
    public async Task TargetChangeDuringJob_DoesNotAlterCapturedSnapshot()
    {
        MutableRouterOsV7TargetProvider v7 = new("7.23.8");
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["7.23.8"],
            InitialVersion = "6.49.23",
            OnBeforeFetch = () => v7.Set("7.24.0"),
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        File.WriteAllBytes(Path.Combine(root, "routeros-7.24.0-mipsbe.npk"), [9, 9, 9, 9]);
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect, v7TargetProvider: v7);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.23"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal(2, backup.CallCount);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.24.0", StringComparison.Ordinal));
        Assert.Equal("7.24.0", v7.TryGetCurrent()!.VersionText);
    }

    /// <summary>
    /// When the RouterBOARD upgrade firmware is newer than the current one, the job issues exactly one RouterBOARD
    /// upgrade command and performs a second reboot (two in total with the v7 reboot). It succeeds at
    /// RouterboardFirmware after two backups, and the legacy <c>FleetUpgradeInstall.rsc</c> is never fetched.
    /// </summary>
    [Fact]
    public async Task RouterboardUpgradeRequired_IssuesCommandAndExtraReboot()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["7.23.8", "7.23.8"],
            InitialVersion = "6.49.23",
            RouterboardCurrentFirmware = "6.49.23",
            RouterboardUpgradeFirmware = "7.23.8",
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.23"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal(1, ops.RouterboardUpgradeCount);
        Assert.Equal(2, ops.RebootCount); // v7 reboot + RouterBOARD reboot
        Assert.Equal(2, backup.CallCount);
        Assert.DoesNotContain(
            ops.FetchedFiles,
            f => f.Equals("FleetUpgradeInstall.rsc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// When the legacy FleetUpgrade cleanup fails on a 6.49.23 router, the result is
    /// <c>LegacyFirmwareAutomationCleanupFailed</c> with <c>CompletedStage</c> None. Schedulers were queried, but
    /// there are no reboots, RouterBOARD commands, backups or fetches.
    /// </summary>
    [Fact]
    public async Task LegacyCleanupFailure_SkipsFirmwareMutation()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["7.23.8"],
            InitialVersion = "6.49.23",
            PresentSchedulers = [LegacyFleetUpgradeObjectNames.SchedulerName],
            ThrowOnSchedulerRemove = new InvalidOperationException("cleanup boom"),
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.23"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.LegacyFirmwareAutomationCleanupFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Equal(0, ops.RebootCount);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
        Assert.Equal(0, backup.CallCount);
        Assert.Empty(ops.FetchedFiles);
        Assert.True(ops.SchedulerCountCalls > 0);
    }

    /// <summary>
    /// An empty current-firmware against an upgrade-firmware of 7.23.8 is inconclusive. The result is
    /// <c>RouterboardUpgradeFailed</c> at OspfMigration with no RouterBOARD upgrade command and only the one v7
    /// reboot. <c>FleetUpgradeInstall.rsc</c> is not fetched.
    /// </summary>
    [Fact]
    public async Task RouterboardInconclusive_DoesNotUpgrade()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["7.23.8"],
            InitialVersion = "6.49.23",
            RouterboardCurrentFirmware = "",
            RouterboardUpgradeFirmware = "7.23.8",
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.23"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.RouterboardUpgradeFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.OspfMigration, result.CompletedStage);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(
            ops.FetchedFiles,
            f => f.Equals("FleetUpgradeInstall.rsc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A BR router steps through 6.49.23 and 7.23.8 and succeeds at RouterboardFirmware. Current and upgrade
    /// firmware match in the fixture, so no RouterBOARD upgrade command is issued. The legacy scheduler and script
    /// cleanup did run, 7.23.8 was fetched and <c>FleetUpgradeInstall.rsc</c> was not.
    /// </summary>
    [Fact]
    public async Task Br_RunsRouterboardPhaseAfterSuccessfulOspf()
    {
        StageTrackingOps ops = new("BR12.POP123.456.edge")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "BR12.POP123.456.edge", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "BR12.POP123.456.edge", version: "6.49.10"),
            CreateJob(),
            CreateDevice("BR12.POP123.456.edge", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal(0, ops.RouterboardUpgradeCount); // current == upgrade firmware in fixture
        Assert.True(ops.SchedulerCountCalls > 0);
        Assert.True(ops.ScriptCountCalls > 0);
        Assert.DoesNotContain(
            ops.FetchedFiles,
            f => f.Equals("FleetUpgradeInstall.rsc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// Creates a temporary firmware directory containing stub 4-byte packages for the RouterOS 6.49.23 stepping
    /// package and the 7.23.8 target package (mipsbe).
    /// </summary>
    private static string CreateFirmwareRootWithV6AndV7()
    {
        string root = Directory.CreateTempSubdirectory("twostage-fw-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [5, 6, 7, 8]);
        return root;
    }

    /// <summary>
    /// A router already on 7.23.8 whose OSPF static-summary redistribution fails gives
    /// <c>OspfStaticSummaryRedistributionFailed</c> with <c>CompletedStage</c> OspfMigration. There is no
    /// RouterBOARD upgrade, the OSPF delete plan is not run, and only the single pre-OSPF backup was taken.
    /// </summary>
    [Fact]
    public async Task StaticSummaryFailure_AfterOspf_BlocksRouterboard()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = [],
            InitialVersion = "7.23.8",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.23.8",
        };
        CountingBackup backup = new();
        TrackingOspfMigrationService ospf = new();
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            summary: new FailingStaticSummaryService(),
            ospf: ospf);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.OspfStaticSummaryRedistributionFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.OspfMigration, result.CompletedStage);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
        Assert.Equal(0, ospf.DeletePlanCount);
        Assert.Equal(1, backup.CallCount); // Path A pre-OSPF backup only
    }

    /// <summary>
    /// On the 6-to-7 path, a static-summary failure after OSPF apply gives
    /// <c>OspfStaticSummaryRedistributionFailed</c> with <c>CompletedStage</c> OspfMigration. The summary ran once
    /// and OSPF applied once, after three backups (v6, v7 and post-ROS7). No RouterBOARD upgrade ran and the OSPF
    /// delete plan was never executed, so the plan is preserved.
    /// </summary>
    [Fact]
    public async Task PathB_StaticSummaryFailure_AfterOspf_BlocksRouterboard_PreservesPlan()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        TrackingOspfMigrationService ospf = new();
        RecordingStaticSummaryService summary = new(OspfStaticSummaryRedistributionResult.Failed("path-b summary failure"));
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            reconnect,
            summary: summary,
            ospf: ospf);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.OspfStaticSummaryRedistributionFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.OspfMigration, result.CompletedStage);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
        Assert.Equal(0, ospf.DeletePlanCount);
        Assert.Equal(1, summary.CallCount);
        Assert.Equal(3, backup.CallCount); // v6 + v7 + post-ROS7 pre-OSPF
        Assert.True(backup.CallCount >= summary.CallCount); // backup before summary
        Assert.Equal(1, ospf.ApplyCount);
    }

    /// <summary>
    /// On the 6-to-7 path a successful static-summary step runs exactly once, after three backups, and the summary
    /// service sees all three backups before it runs. The job ends <c>UpgradeSucceeded</c> at RouterboardFirmware.
    /// </summary>
    [Fact]
    public async Task PathB_StaticSummarySuccess_RunsBeforeRouterboard()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        RecordingStaticSummaryService summary = new(OspfStaticSummaryRedistributionResult.Ok())
        {
            Backup = backup,
        };
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            reconnect,
            summary: summary);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal(1, summary.CallCount);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(3, summary.LastCallBackupCount);
    }

    /// <summary>
    /// If the backup taken after the RouterOS 7 install fails, the result is <c>BackupFailed</c> with
    /// <c>CompletedStage</c> RouterOs7. OSPF apply, static summary, RouterBOARD upgrade and the OSPF delete plan
    /// are all skipped.
    /// </summary>
    [Fact]
    public async Task PathB_BackupFailureBeforeOspf_BlocksOspfSummaryAndRouterboard()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new() { FailOnCallNumber = 3 };
        RecordingStaticSummaryService summary = new(OspfStaticSummaryRedistributionResult.Ok());
        TrackingOspfMigrationService ospf = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            reconnect,
            summary: summary,
            ospf: ospf);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.BackupFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterOs7, result.CompletedStage);
        Assert.Equal(0, ospf.ApplyCount);
        Assert.Equal(0, summary.CallCount);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
        Assert.Equal(0, ospf.DeletePlanCount);
    }

    /// <summary>
    /// The recorded timeline must show the last backup before OSPF apply, and OSPF apply before the static summary.
    /// The job succeeds with three backups and one OSPF apply.
    /// </summary>
    [Fact]
    public async Task PathB_PostRos7BackupThenOspfOrdering()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        List<string> timeline = [];
        CountingBackup backup = new() { Timeline = timeline };
        TrackingOspfMigrationService ospf = new() { Timeline = timeline };
        RecordingStaticSummaryService summary = new(OspfStaticSummaryRedistributionResult.Ok())
        {
            Backup = backup,
            Timeline = timeline,
        };
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            reconnect,
            summary: summary,
            ospf: ospf);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(1, ospf.ApplyCount);
        int lastBackupBeforeOspf = timeline.FindLastIndex(static e => e == "backup");
        int ospfIdx = timeline.IndexOf("ospf-apply");
        int summaryIdx = timeline.IndexOf("summary");
        Assert.True(lastBackupBeforeOspf >= 0 && ospfIdx >= 0);
        Assert.True(lastBackupBeforeOspf < ospfIdx, $"Expected post-ROS7 backup before OSPF. Timeline: {string.Join(',', timeline)}");
        Assert.True(ospfIdx < summaryIdx, $"Expected OSPF before summary. Timeline: {string.Join(',', timeline)}");
    }

    /// <summary>
    /// A third backup that reports <c>RemoteCleanupFailed</c> but still has centrally verified export, binary and
    /// manifest files does not stop the job. It ends <c>UpgradeSucceeded</c> with one OSPF apply, and
    /// <c>IsCentralBackupVerified</c> is true for that result.
    /// </summary>
    [Fact]
    public async Task PathB_RemoteCleanupFailedStillAllowsOspfWhenCentralVerified()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new()
        {
            ResultOnCallNumber = 3,
            ResultOverride = new DeviceBackupResult
            {
                Succeeded = false,
                RemoteCleanupFailed = true,
                FailureReason = "remote cleanup failed after central verification",
                ExportLocalPath = "export.rsc",
                BinaryLocalPath = "binary.backup",
                ManifestLocalPath = "manifest.json",
            },
        };
        TrackingOspfMigrationService ospf = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect, ospf: ospf);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(1, ospf.ApplyCount);
        Assert.True(RouterOsV6UpgradeExecutor.IsCentralBackupVerified(backup.ResultOverride!));
    }

    /// <summary>
    /// Cancelling at the third backup, which returns a verified backup with a failed remote cleanup, makes
    /// <c>ExecuteAsync</c> throw <c>OperationCanceledException</c>. OSPF apply, static summary and RouterBOARD
    /// upgrade all stay at zero.
    /// </summary>
    [Fact]
    public async Task PathB_CancellationAfterVerifiedBackup_PreventsOspfEvenIfCleanupFailed()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        using CancellationTokenSource cts = new();
        CountingBackup backup = new()
        {
            ResultOnCallNumber = 3,
            ResultOverride = new DeviceBackupResult
            {
                Succeeded = false,
                RemoteCleanupFailed = true,
                FailureReason = "remote cleanup failed after central verification",
                ExportLocalPath = "export.rsc",
                BinaryLocalPath = "binary.backup",
                ManifestLocalPath = "manifest.json",
            },
            OnCall = n =>
            {
                if (n == 3)
                {
                    cts.Cancel();
                }
            },
        };
        TrackingOspfMigrationService ospf = new();
        RecordingStaticSummaryService summary = new(OspfStaticSummaryRedistributionResult.Ok());
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            reconnect,
            summary: summary,
            ospf: ospf);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(
                FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
                CreateJob(),
                CreateDevice("AP01.POP001.001.access", "6.49.10"),
                CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
                "admin",
                "secret",
                static () => true,
                cts.Token));

        Assert.Equal(0, ospf.ApplyCount);
        Assert.Equal(0, summary.CallCount);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
    }

    /// <summary>
    /// For a router already on 7.23.8, cancelling during the first backup (verified but with a failed remote
    /// cleanup) makes <c>ExecuteAsync</c> throw <c>OperationCanceledException</c>. OSPF apply, static summary and
    /// RouterBOARD upgrade all stay at zero.
    /// </summary>
    [Fact]
    public async Task PathA_CancellationAfterVerifiedBackup_PreventsOspfEvenIfCleanupFailed()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = [],
            InitialVersion = "7.23.8",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.23.8",
        };
        using CancellationTokenSource cts = new();
        CountingBackup backup = new()
        {
            ResultOverride = new DeviceBackupResult
            {
                Succeeded = false,
                RemoteCleanupFailed = true,
                FailureReason = "remote cleanup failed after central verification",
                ExportLocalPath = "export.rsc",
                BinaryLocalPath = "binary.backup",
                ManifestLocalPath = "manifest.json",
            },
            ResultOnCallNumber = 1,
            OnCall = _ => cts.Cancel(),
        };
        TrackingOspfMigrationService ospf = new();
        RecordingStaticSummaryService summary = new(OspfStaticSummaryRedistributionResult.Ok());
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            summary: summary,
            ospf: ospf);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(
                FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
                CreateJob(),
                CreateDevice("AP01.POP001.001.access", "7.23.8"),
                CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
                "admin",
                "secret",
                static () => true,
                cts.Token));

        Assert.Equal(0, ospf.ApplyCount);
        Assert.Equal(0, summary.CallCount);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
    }

    /// <summary>
    /// Cancelling inside the third backup, the post-ROS7 one, makes <c>ExecuteAsync</c> throw
    /// <c>OperationCanceledException</c>. OSPF apply and RouterBOARD upgrade stay at zero.
    /// </summary>
    [Fact]
    public async Task PathB_CancellationDuringPostRos7Backup_PreventsOspf()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        using CancellationTokenSource cts = new();
        CountingBackup backup = new()
        {
            OnCall = n =>
            {
                if (n == 3)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
            },
        };
        TrackingOspfMigrationService ospf = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, reconnect, ospf: ospf);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(
                FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
                CreateJob(),
                CreateDevice("AP01.POP001.001.access", "6.49.10"),
                CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
                "admin",
                "secret",
                static () => true,
                cts.Token));

        Assert.Equal(0, ospf.ApplyCount);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
    }

    /// <summary>
    /// When OSPF apply fails after the three backups, the result is <c>OspfMigrationApplyFailed</c> with
    /// <c>CompletedStage</c> RouterOs7. OSPF apply was attempted once, and the static summary, RouterBOARD upgrade
    /// and OSPF delete plan never run.
    /// </summary>
    [Fact]
    public async Task PathB_OspfFailureAfterVerifiedBackup_BlocksSummaryAndRouterboard()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        TrackingOspfMigrationService ospf = new()
        {
            ApplyResult = OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfMigrationApplyFailed,
                "forced ospf apply failure"),
        };
        RecordingStaticSummaryService summary = new(OspfStaticSummaryRedistributionResult.Ok());
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            reconnect,
            summary: summary,
            ospf: ospf);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "6.49.23"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.OspfMigrationApplyFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterOs7, result.CompletedStage);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(1, ospf.ApplyCount);
        Assert.Equal(0, summary.CallCount);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
        Assert.Equal(0, ospf.DeletePlanCount);
    }

    /// <summary>
    /// A router already on 7.23.8 takes one backup and applies OSPF once, in the order backup then ospf-apply. The
    /// result is <c>SkippedAlreadyAtTarget</c> at RouterboardFirmware.
    /// </summary>
    [Fact]
    public async Task PathA_AlreadyRos7_RetainsBackupBeforeOspfOrdering()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = [],
            InitialVersion = "7.23.8",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.23.8",
        };
        List<string> timeline = [];
        CountingBackup backup = new() { Timeline = timeline };
        TrackingOspfMigrationService ospf = new() { Timeline = timeline };
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, ospf: ospf);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.SkippedAlreadyAtTarget, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ospf.ApplyCount);
        Assert.Equal(["backup", "ospf-apply"], timeline.Where(static e => e is "backup" or "ospf-apply").ToList());
    }

    /// <summary>
    /// For a router already on 7.23.8, an OSPF apply failure gives <c>OspfMigrationApplyFailed</c> with
    /// <c>CompletedStage</c> RouterOs7. One backup and one OSPF apply happened, and the static summary, RouterBOARD
    /// upgrade and delete plan did not.
    /// </summary>
    [Fact]
    public async Task PathA_AlreadyRos7_OspfFailureAfterBackup_ReportsRouterOs7CompletedStage()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = [],
            InitialVersion = "7.23.8",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.23.8",
        };
        CountingBackup backup = new();
        TrackingOspfMigrationService ospf = new()
        {
            ApplyResult = OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfMigrationApplyFailed,
                "forced already-v7 ospf failure"),
        };
        RecordingStaticSummaryService summary = new(OspfStaticSummaryRedistributionResult.Ok());
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            summary: summary,
            ospf: ospf);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.OspfMigrationApplyFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterOs7, result.CompletedStage);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ospf.ApplyCount);
        Assert.Equal(0, summary.CallCount);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
        Assert.Equal(0, ospf.DeletePlanCount);
    }

    /// <summary>
    /// For a router already on 7.23.8, a failed backup gives <c>BackupFailed</c> with <c>CompletedStage</c> None,
    /// and neither OSPF apply nor a RouterBOARD upgrade is attempted.
    /// </summary>
    [Fact]
    public async Task PathA_AlreadyRos7_BackupFailure_ReportsNoneCompletedStage()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = [],
            InitialVersion = "7.23.8",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.23.8",
        };
        CountingBackup backup = new() { FailOnCallNumber = 1 };
        TrackingOspfMigrationService ospf = new();
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, ospf: ospf);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.BackupFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Equal(0, ospf.ApplyCount);
        Assert.Equal(0, ops.RouterboardUpgradeCount);
    }

    /// <summary>
    /// Cancelling while the backup runs makes <c>ExecuteAsync</c> throw <c>OperationCanceledException</c> and OSPF
    /// apply is never called.
    /// </summary>
    [Fact]
    public async Task PathA_CancellationDuringBackup_DoesNotMarkStageComplete()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = [],
            InitialVersion = "7.23.8",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.23.8",
        };
        using CancellationTokenSource cts = new();
        CountingBackup backup = new()
        {
            OnCall = _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };
        TrackingOspfMigrationService ospf = new();
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, ospf: ospf);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(
                FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
                CreateJob(),
                CreateDevice("AP01.POP001.001.access", "7.23.8"),
                CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
                "admin",
                "secret",
                static () => true,
                cts.Token));

        Assert.Equal(0, ospf.ApplyCount);
    }

    /// <summary>
    /// Cancelling from inside OSPF apply makes <c>ExecuteAsync</c> throw <c>OperationCanceledException</c>. The
    /// test checks that exactly one backup and one OSPF apply attempt were made.
    /// </summary>
    [Fact]
    public async Task PathA_CancellationDuringOspf_DoesNotMarkOspfComplete()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = [],
            InitialVersion = "7.23.8",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.23.8",
        };
        using CancellationTokenSource cts = new();
        CountingBackup backup = new();
        TrackingOspfMigrationService ospf = new()
        {
            OnApply = () =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root, ospf: ospf);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(
                FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
                CreateJob(),
                CreateDevice("AP01.POP001.001.access", "7.23.8"),
                CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
                "admin",
                "secret",
                static () => true,
                cts.Token));

        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ospf.ApplyCount);
    }

    /// <summary>
    /// A router already on 7.23.8 whose loader is behind (current 7.23.8, upgrade 7.24.0) has its RouterBOARD upgrade
    /// command refused. With the static summary recorded as successful beforehand, the result is
    /// <c>RouterboardUpgradeFailed</c> with <c>CompletedStage</c> OspfSummaryRedistribution.
    /// </summary>
    [Fact]
    public async Task StaticSummarySuccess_ThenRouterboardCommandFailure_ReportsSummaryCompletedStage()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = [],
            InitialVersion = "7.23.8",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.24.0",
            ThrowOnRouterboardUpgrade = new IOException("rb upgrade refused"),
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            summary: new RecordingStaticSummaryService(OspfStaticSummaryRedistributionResult.Ok()));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.RouterboardUpgradeFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.OspfSummaryRedistribution, result.CompletedStage);
    }

    /// <summary>
    /// Same refused RouterBOARD upgrade (current 7.23.8, upgrade 7.24.0), but with the pass-through static-summary
    /// stub that records no summary work. The result is <c>RouterboardUpgradeFailed</c> with <c>CompletedStage</c>
    /// OspfMigration rather than OspfSummaryRedistribution.
    /// </summary>
    [Fact]
    public async Task StaticSummaryDisabled_ThenRouterboardCommandFailure_ReportsOspfMigrationStage()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = [],
            InitialVersion = "7.23.8",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.24.0",
            ThrowOnRouterboardUpgrade = new IOException("rb upgrade refused"),
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(
            ops,
            backup,
            root,
            summary: new PassingOspfStaticSummaryRedistributionService());

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.RouterboardUpgradeFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.OspfMigration, result.CompletedStage);
    }

    /// <summary>
    /// Builds a <see cref="RouterOsV6UpgradeExecutor"/> wired to the supplied fakes with short test timeouts. Unless
    /// overridden, reconnects throw, the RouterOS 7 target is 7.23.8, and the OSPF migration and static-summary
    /// services always pass; the verify timeout is capped at the reboot timeout.
    /// </summary>
    private static RouterOsV6UpgradeExecutor CreateTwoStageExecutor(
        StageTrackingOps ops,
        IDeviceBackupService backup,
        string firmwareRoot,
        IMikrotikConnectionFactory? connectionFactory = null,
        TimeSpan? rebootTimeout = null,
        TimeSpan? postUpgradeVerifyTimeout = null,
        IRouterOsV7TargetProvider? v7TargetProvider = null,
        IOspfStaticSummaryRedistributionService? summary = null,
        IOspfMigrationService? ospf = null)
    {
        TimeSpan reboot = rebootTimeout ?? TimeSpan.FromSeconds(5);
        TimeSpan verify = postUpgradeVerifyTimeout ?? reboot;
        if (verify > reboot)
        {
            verify = reboot;
        }

        IMikrotikConnectionFactory factory =
            connectionFactory ?? new ThrowingMikrotikConnectionFactory(new IOException("no reconnect"));
        IOptions<FirmwareUpgradeOptions> upgradeOptions = Options.Create(new FirmwareUpgradeOptions
        {
            Enabled = true,
            AdvertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
            FetchTimeout = TimeSpan.FromSeconds(5),
            ReconnectInterval = TimeSpan.FromMilliseconds(10),
            RebootTimeout = reboot,
            PostUpgradeVerifyTimeout = verify,
            BackupFileReadyTimeout = TimeSpan.FromMilliseconds(100),
            BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(20),
        });
        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot });
        IOptions<MikroTikOptions> mikrotikOptions = Options.Create(new MikroTikOptions { Port = 8728 });

        return new RouterOsV6UpgradeExecutor(
            ops,
            factory,
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(firmwareRoot),
                firmwareFilesOptions),
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
                TimeProvider.System,
                NullLogger<RouterboardFirmwareUpgradeService>.Instance),
            ospf ?? new PassingOspfMigrationService(),
            summary ?? new PassingOspfStaticSummaryRedistributionService(),
            upgradeOptions,
            mikrotikOptions,
            TimeProvider.System,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
    }

    /// <summary>
    /// Static-summary redistribution fake that always fails, used to prove failures after the OSPF migration step are
    /// surfaced.
    /// </summary>
    private sealed class FailingStaticSummaryService : IOspfStaticSummaryRedistributionService
    {
        /// <summary>Always returns a failed result with the reason <c>forced summary failure</c>.</summary>
        public Task<OspfStaticSummaryRedistributionResult> ApplyAndVerifyAsync(
            ITikConnection connection,
            OspfMigrationPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfStaticSummaryRedistributionResult.Failed("forced summary failure"));
    }

    /// <summary>
    /// Static-summary redistribution fake that records invocations and returns a preconfigured result, so tests can
    /// assert it ran (and in which order).
    /// </summary>
    private sealed class RecordingStaticSummaryService(OspfStaticSummaryRedistributionResult result)
        : IOspfStaticSummaryRedistributionService
    {
        /// <summary>Number of times static-summary redistribution was applied.</summary>
        public int CallCount { get; private set; }
        /// <summary>
        /// Call count of <see cref="Backup"/> sampled at the latest summary call, proving the second-stage backup ran
        /// before redistribution.
        /// </summary>
        public int? LastCallBackupCount { get; private set; }
        /// <summary>Optional backup fake whose call count is sampled each time the summary runs.</summary>
        public CountingBackup? Backup { get; init; }
        /// <summary>Optional shared ordered log that receives a <c>summary</c> entry per call.</summary>
        public List<string>? Timeline { get; init; }

        /// <summary>Records the call (count, backup count, timeline) and returns the preconfigured result.</summary>
        public Task<OspfStaticSummaryRedistributionResult> ApplyAndVerifyAsync(
            ITikConnection connection,
            OspfMigrationPlan? plan,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastCallBackupCount = Backup?.CallCount;
            Timeline?.Add("summary");
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// OSPF migration fake that delegates to <see cref="PassingOspfMigrationService"/> while counting plan deletions
    /// and applies, optionally overriding the apply result.
    /// </summary>
    private sealed class TrackingOspfMigrationService : IOspfMigrationService
    {
        /// <summary>Always-passing implementation that supplies default behaviour.</summary>
        private readonly PassingOspfMigrationService _inner = new();
        /// <summary>Number of times the persisted OSPF plan was deleted.</summary>
        public int DeletePlanCount { get; private set; }
        /// <summary>Number of times the OSPF plan was applied and verified.</summary>
        public int ApplyCount { get; private set; }
        /// <summary>Optional shared ordered log that receives an <c>ospf-apply</c> entry per apply.</summary>
        public List<string>? Timeline { get; init; }
        /// <summary>
        /// When set, returned from <see cref="ApplyAndVerifyAsync"/> instead of delegating, simulating an apply-gate
        /// failure.
        /// </summary>
        public OspfApplyGateResult? ApplyResult { get; init; }
        /// <summary>Optional callback invoked on each apply before the result is produced.</summary>
        public Action? OnApply { get; init; }

        /// <summary>Delegates to the passing inner service without recording.</summary>
        public Task<OspfGateResult> CaptureAndValidateAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            string identity,
            string routerOsVersion,
            string? architecture,
            bool persistPlan,
            CancellationToken cancellationToken) =>
            _inner.CaptureAndValidateAsync(
                connection,
                job,
                identity,
                routerOsVersion,
                architecture,
                persistPlan,
                cancellationToken);

        /// <summary>
        /// Records the apply (count, timeline, callback) and returns <see cref="ApplyResult"/> when set, otherwise
        /// the inner result.
        /// </summary>
        public Task<OspfApplyGateResult> ApplyAndVerifyAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            string identity,
            OspfMigrationPlan? inMemoryPlan,
            CancellationToken cancellationToken,
            string? managementHost = null)
        {
            ApplyCount++;
            Timeline?.Add("ospf-apply");
            OnApply?.Invoke();
            if (ApplyResult is not null)
            {
                return Task.FromResult(ApplyResult);
            }

            return _inner.ApplyAndVerifyAsync(
                connection,
                job,
                identity,
                inMemoryPlan,
                cancellationToken,
                managementHost);
        }

        /// <summary>Counts the deletion and forwards it to the inner service.</summary>
        public void DeletePlan(MikrotikScheduledJob job)
        {
            DeletePlanCount++;
            _inner.DeletePlan(job);
        }
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
    /// Builds RouterOS-6-era <see cref="MikrotikDeviceInfo"/> (mipsbe, hEX PoE) with the given identity and version.
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
    /// Builds a compatible, path-eligible assessment with the given status and RouterOS target; the version
    /// classification is <c>AtTargetV6</c> for <c>AlreadyAtTarget</c> and <c>OlderThanTargetV6</c> otherwise.
    /// </summary>
    private static DeviceCompatibilityAssessment CreateAssessment(
        DeviceAssessmentStatus status,
        string target) =>
        new()
        {
            RouterIp = "10.0.0.50",
            OverallStatus = status,
            RouterOsVersionClassification = status == DeviceAssessmentStatus.AlreadyAtTarget
                ? RouterOsVersionClassification.AtTargetV6
                : RouterOsVersionClassification.OlderThanTargetV6,
            TargetRouterOsVersion = target,
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Backup fake that counts calls and can fail or return a custom result on a chosen call, used to verify each
    /// upgrade stage takes its own backup.
    /// </summary>
    private sealed class CountingBackup : IDeviceBackupService
    {
        /// <summary>Number of pre-upgrade backups requested.</summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// 1-based call number that fails with <c>second-stage backup failed</c>; null never fails.
        /// </summary>
        public int? FailOnCallNumber { get; init; }

        /// <summary>1-based call number on which <see cref="ResultOverride"/> is returned.</summary>
        public int? ResultOnCallNumber { get; init; }

        /// <summary>Backup result returned on the call selected by <see cref="ResultOnCallNumber"/>.</summary>
        public DeviceBackupResult? ResultOverride { get; init; }

        /// <summary>Optional callback invoked with the 1-based call number on every backup.</summary>
        public Action<int>? OnCall { get; init; }

        /// <summary>Optional shared ordered log that receives a <c>backup</c> entry per call.</summary>
        public List<string>? Timeline { get; init; }

        /// <summary>
        /// Records the call and returns a configured failure, the override result, or success with remote cleanup
        /// completed.
        /// </summary>
        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Timeline?.Add("backup");
            OnCall?.Invoke(CallCount);
            if (FailOnCallNumber is int n && CallCount == n)
            {
                return Task.FromResult(new DeviceBackupResult
                {
                    Succeeded = false,
                    FailureReason = "second-stage backup failed",
                });
            }

            if (ResultOnCallNumber is int rn && CallCount == rn && ResultOverride is not null)
            {
                return Task.FromResult(ResultOverride);
            }

            return Task.FromResult(new DeviceBackupResult
            {
                Succeeded = true,
                RemoteCleanupSucceeded = true,
            });
        }
    }

    /// <summary>
    /// Stateful <see cref="IMikrotikRouterOperations"/> fake that models a router across the v6 stepping and RouterOS
    /// 7 stages: versions change per reboot, RouterBOARD firmware is promoted on reboot, and cleanup, fetch and
    /// reboot activity is recorded.
    /// </summary>
    private sealed class StageTrackingOps(string identity) : IMikrotikRouterOperations
    {
        /// <summary>
        /// 1-based index of the latest reboot into <see cref="VersionsAfterReboot"/>; zero before any reboot.
        /// </summary>
        private int _rebootIndex;
        /// <summary>Set once a RouterBOARD upgrade was issued so the next reboot applies the new firmware.</summary>
        private bool _routerboardUpgradeIssued;

        /// <summary>System identity reported by the fake router.</summary>
        public string Identity { get; } = identity;

        /// <summary>RouterOS version reported before the first reboot.</summary>
        public string InitialVersion { get; init; } = "6.49.10";

        /// <summary>
        /// RouterOS versions reported after each successive reboot; the last entry repeats once the list is
        /// exhausted.
        /// </summary>
        public List<string> VersionsAfterReboot { get; init; } = [];

        /// <summary>
        /// 1-based number of the post-reboot device-info read that throws; null disables the failure.
        /// </summary>
        public int? FailOnPostRebootDeviceInfoReadNumber { get; init; }

        /// <summary>Defaults equal so a successful upgrade needs no RouterBOARD upgrade/reboot.</summary>
        public string RouterboardCurrentFirmware { get; set; } = "7.23.8";

        /// <summary>
        /// RouterBOARD firmware version staged for upgrade; the default equals the current firmware so a successful
        /// upgrade needs no RouterBOARD upgrade or reboot.
        /// </summary>
        public string RouterboardUpgradeFirmware { get; set; } = "7.23.8";

        /// <summary>Names of legacy fleet-upgrade scripts currently present on the fake router.</summary>
        public List<string> PresentScripts { get; init; } = [];

        /// <summary>Names of legacy fleet-upgrade schedulers currently present on the fake router.</summary>
        public List<string> PresentSchedulers { get; init; } = [];

        /// <summary>
        /// When set, <see cref="UpgradeRouterboardAsync"/> throws this exception, simulating a refused RouterBOARD
        /// upgrade.
        /// </summary>
        public Exception? ThrowOnRouterboardUpgrade { get; init; }

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

        /// <summary>Number of device-info reads performed after the first reboot.</summary>
        public int PostRebootDeviceInfoReads { get; private set; }

        /// <summary>Number of RouterBOARD firmware upgrades issued.</summary>
        public int RouterboardUpgradeCount { get; private set; }

        /// <summary>
        /// Number of scheduler inspections performed; zero proves cleanup never looked for schedulers.
        /// </summary>
        public int SchedulerCountCalls { get; private set; }

        /// <summary>Number of script inspections performed; zero proves cleanup never looked for scripts.</summary>
        public int ScriptCountCalls { get; private set; }

        /// <summary>Total scheduler and script inspections; zero proves legacy cleanup was never attempted.</summary>
        public int CleanupInspectionCount => SchedulerCountCalls + ScriptCountCalls;

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
        /// Optional callback invoked at the start of each fetch, e.g. to record ordering or assert state.
        /// </summary>
        public Action? OnBeforeFetch { get; init; }

        /// <summary>
        /// RouterOS version reported right now: <see cref="InitialVersion"/> before any reboot, then the entry of
        /// <see cref="VersionsAfterReboot"/> selected by the latest reboot.
        /// </summary>
        private string CurrentVersion =>
            RebootCount == 0
                ? InitialVersion
                : VersionsAfterReboot[Math.Min(_rebootIndex - 1, VersionsAfterReboot.Count - 1)];

        /// <summary>
        /// Returns device info for <see cref="Identity"/> at the current stage version. After the first reboot each
        /// read is counted, and the read numbered <see cref="FailOnPostRebootDeviceInfoReadNumber"/> throws to
        /// simulate identity being unavailable.
        /// </summary>
        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default)
        {
            if (RebootCount >= 1)
            {
                PostRebootDeviceInfoReads++;
                if (FailOnPostRebootDeviceInfoReadNumber is int n
                    && PostRebootDeviceInfoReads == n)
                {
                    throw new InvalidOperationException("identity unavailable");
                }
            }

            return Task.FromResult(new MikrotikDeviceInfo
            {
                Identity = Identity,
                Version = CurrentVersion,
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
                RouterboardCurrentFirmware = RouterboardCurrentFirmware,
                RouterboardUpgradeFirmware = RouterboardUpgradeFirmware,
            });
        }

        /// <summary>
        /// Returns a single main package (<c>routeros-mipsbe</c> on RouterOS 6, <c>routeros</c> on 7+) at the numeric
        /// version of the current stage.
        /// </summary>
        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            string version = CurrentVersion;
            string numeric = RouterOsVersion.TryParse(version, out RouterOsVersion parsed)
                ? parsed.ToString()
                : version;
            string mainName = RouterOsVersion.TryParse(version, out RouterOsVersion ver) && ver.IsV7OrLater
                ? "routeros"
                : "routeros-mipsbe";
            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([
                new(mainName, numeric, false),
            ]);
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

        /// <summary>
        /// Invokes <see cref="OnBeforeFetch"/>, then records the URL and destination file name without transferring
        /// anything.
        /// </summary>
        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            OnBeforeFetch?.Invoke();
            FetchedUrls.Add(url);
            FetchedFiles.Add(destinationFileName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Counts the reboot and selects the next post-reboot version. If a RouterBOARD upgrade was issued, the
        /// current firmware is promoted to the upgrade firmware, as on real hardware.
        /// </summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCount++;
            _rebootIndex = RebootCount;
            if (_routerboardUpgradeIssued)
            {
                RouterboardCurrentFirmware = RouterboardUpgradeFirmware;
            }

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
        /// Throws <see cref="ThrowOnRouterboardUpgrade"/> when configured; otherwise counts the upgrade and arms
        /// firmware promotion on the next reboot.
        /// </summary>
        public Task UpgradeRouterboardAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnRouterboardUpgrade is not null)
            {
                throw ThrowOnRouterboardUpgrade;
            }

            RouterboardUpgradeCount++;
            _routerboardUpgradeIssued = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// RouterOS 7 target provider whose version can be changed or cleared at runtime, to test target changes between
    /// stages.
    /// </summary>
    private sealed class MutableRouterOsV7TargetProvider : IRouterOsV7TargetProvider
    {
        /// <summary>Guards <see cref="_current"/> against concurrent <see cref="Set"/> and read calls.</summary>
        private readonly object _gate = new();
        /// <summary>Current target snapshot; null when no target version is configured.</summary>
        private RouterOsV7TargetSnapshot? _current;

        /// <summary>Creates the provider with an initial target version; null or blank means no target.</summary>
        public MutableRouterOsV7TargetProvider(string? versionText) => Set(versionText);

        /// <summary>Placeholder source path reported in snapshots; no file is read.</summary>
        public string VersionFilePath => "mutable-test-version";

        /// <summary>
        /// Replaces the target with the parsed version, or clears it when the text is null or whitespace.
        /// </summary>
        public void Set(string? versionText)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(versionText))
                {
                    _current = null;
                    return;
                }

                RouterOsVersion version = RouterOsVersion.Parse(versionText);
                _current = new RouterOsV7TargetSnapshot
                {
                    Version = version,
                    VersionText = version.ToString(),
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    SourcePath = VersionFilePath,
                };
            }
        }

        /// <summary>Returns the current target snapshot, or null when none is set.</summary>
        public RouterOsV7TargetSnapshot? TryGetCurrent()
        {
            lock (_gate)
            {
                return _current;
            }
        }

        /// <summary>Returns the current snapshot, same as <see cref="TryGetCurrent"/>.</summary>
        public RouterOsV7TargetSnapshot? CaptureSnapshot() => TryGetCurrent();
    }
}
