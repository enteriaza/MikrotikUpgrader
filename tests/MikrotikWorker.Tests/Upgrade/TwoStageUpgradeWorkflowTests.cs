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
public sealed class TwoStageUpgradeWorkflowTests
{
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

    private static string CreateFirmwareRootWithV6AndV7()
    {
        string root = Directory.CreateTempSubdirectory("twostage-fw-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [5, 6, 7, 8]);
        return root;
    }

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

    private sealed class FailingStaticSummaryService : IOspfStaticSummaryRedistributionService
    {
        public Task<OspfStaticSummaryRedistributionResult> ApplyAndVerifyAsync(
            ITikConnection connection,
            OspfMigrationPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfStaticSummaryRedistributionResult.Failed("forced summary failure"));
    }

    private sealed class RecordingStaticSummaryService(OspfStaticSummaryRedistributionResult result)
        : IOspfStaticSummaryRedistributionService
    {
        public int CallCount { get; private set; }
        public int? LastCallBackupCount { get; private set; }
        public CountingBackup? Backup { get; init; }
        public List<string>? Timeline { get; init; }

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

    private sealed class TrackingOspfMigrationService : IOspfMigrationService
    {
        private readonly PassingOspfMigrationService _inner = new();
        public int DeletePlanCount { get; private set; }
        public int ApplyCount { get; private set; }
        public List<string>? Timeline { get; init; }
        public OspfApplyGateResult? ApplyResult { get; init; }
        public Action? OnApply { get; init; }

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

        public void DeletePlan(MikrotikScheduledJob job)
        {
            DeletePlanCount++;
            _inner.DeletePlan(job);
        }
    }

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

    private static MikrotikDeviceInfo CreateDevice(string identity, string version) =>
        new()
        {
            Identity = identity,
            Version = version,
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

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

    private sealed class CountingBackup : IDeviceBackupService
    {
        public int CallCount { get; private set; }

        public int? FailOnCallNumber { get; init; }

        public int? ResultOnCallNumber { get; init; }

        public DeviceBackupResult? ResultOverride { get; init; }

        public Action<int>? OnCall { get; init; }

        public List<string>? Timeline { get; init; }

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

    private sealed class StageTrackingOps(string identity) : IMikrotikRouterOperations
    {
        private int _rebootIndex;
        private bool _routerboardUpgradeIssued;

        public string Identity { get; } = identity;

        public string InitialVersion { get; init; } = "6.49.10";

        public List<string> VersionsAfterReboot { get; init; } = [];

        public int? FailOnPostRebootDeviceInfoReadNumber { get; init; }

        /// <summary>Defaults equal so a successful upgrade needs no RouterBOARD upgrade/reboot.</summary>
        public string RouterboardCurrentFirmware { get; set; } = "7.23.8";

        public string RouterboardUpgradeFirmware { get; set; } = "7.23.8";

        public List<string> PresentScripts { get; init; } = [];

        public List<string> PresentSchedulers { get; init; } = [];

        public Exception? ThrowOnRouterboardUpgrade { get; init; }

        public List<string> RemovalOrder { get; } = [];

        public Exception? ThrowOnSchedulerRemove { get; init; }

        public Exception? ThrowOnScriptRemove { get; init; }

        public int RebootCount { get; private set; }

        public int PostRebootDeviceInfoReads { get; private set; }

        public int RouterboardUpgradeCount { get; private set; }

        public int SchedulerCountCalls { get; private set; }

        public int ScriptCountCalls { get; private set; }

        public int CleanupInspectionCount => SchedulerCountCalls + ScriptCountCalls;

        public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> FetchedFiles { get; } = [];

        public List<string> FetchedUrls { get; } = [];

        public Action? OnBeforeFetch { get; init; }

        private string CurrentVersion =>
            RebootCount == 0
                ? InitialVersion
                : VersionsAfterReboot[Math.Min(_rebootIndex - 1, VersionsAfterReboot.Count - 1)];

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
            CancellationToken cancellationToken = default)
        {
            if (FileSizes.TryGetValue(fileName, out long known))
            {
                return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, known));
            }

            return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, 4));
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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default)
        {
            ScriptCountCalls++;
            return Task.FromResult(PresentScripts.Count(n => n.Equals(scriptName, StringComparison.Ordinal)));
        }

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

        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default)
        {
            SchedulerCountCalls++;
            return Task.FromResult(PresentSchedulers.Count(n => n.Equals(schedulerName, StringComparison.Ordinal)));
        }

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

    private sealed class MutableRouterOsV7TargetProvider : IRouterOsV7TargetProvider
    {
        private readonly object _gate = new();
        private RouterOsV7TargetSnapshot? _current;

        public MutableRouterOsV7TargetProvider(string? versionText) => Set(versionText);

        public string VersionFilePath => "mutable-test-version";

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

        public RouterOsV7TargetSnapshot? TryGetCurrent()
        {
            lock (_gate)
            {
                return _current;
            }
        }

        public RouterOsV7TargetSnapshot? CaptureSnapshot() => TryGetCurrent();
    }
}
