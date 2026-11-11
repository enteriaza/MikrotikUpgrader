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

namespace MikrotikWorker.Tests;

public sealed class TwoStageUpgradeWorkflowTests
{
    [Theory]
    [InlineData("ACE01.POP001.001.core", "ACE")]
    [InlineData("BR12.POP123.456.edge", "BR")]
    public async Task AceOrBr_CompletesV6Only_WithoutSecondStageOps(string identity, string typeToken)
    {
        StageTrackingOps ops = new(identity)
        {
            VersionsAfterReboot = ["6.49.23"],
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        ScriptedMikrotikConnectionFactory reconnect = new(_ => FakeMikrotikConnections.CreateFullyPopulated(identity: identity));
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

        Assert.Equal(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
        Assert.Contains(typeToken, result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.All(ops.FetchedFiles, f => Assert.Contains("6.49.23", f, StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedUrls, u => u.Contains("7.23.8", StringComparison.Ordinal));
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
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(2, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
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
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(2, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
    }

    [Fact]
    public async Task IdentityRereadFailure_KeepsVerifiedAceAndSkipsV7()
    {
        StageTrackingOps ops = new("ACE01.POP001.001.core")
        {
            VersionsAfterReboot = ["6.49.23"],
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

        Assert.Equal(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
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
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(1, backup.CallCount); // only second-stage backup
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
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(2, backup.CallCount);
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
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AceOnNewerThanStepping_SkipsSteppingAndSkipsV7()
    {
        StageTrackingOps ops = new("ACE01.POP001.001.core")
        {
            InitialVersion = "6.49.24",
        };
        CountingBackup backup = new();
        string root = CreateFirmwareRootWithV6AndV7();
        RouterOsV6UpgradeExecutor executor = CreateTwoStageExecutor(ops, backup, root);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "ACE01.POP001.001.core", version: "6.49.24"),
            CreateJob(),
            CreateDevice("ACE01.POP001.001.core", "6.49.24"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade, "7.23.8"),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
        Assert.Equal(0, backup.CallCount);
        Assert.Equal(0, ops.RebootCount);
        Assert.Empty(ops.FetchedFiles);
    }

    [Fact]
    public async Task AceOnOlderV6_StepsTo64923_ThenSkipsV7()
    {
        StageTrackingOps ops = new("ACE01.POP001.001.core")
        {
            VersionsAfterReboot = ["6.49.23"],
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

        Assert.Equal(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.19", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
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
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RouterOs7AtTarget_SkipsUpgrade()
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

        Assert.Equal(UpgradeOutcome.SkippedAlreadyAtTarget, result.Outcome);
        Assert.Equal(0, backup.CallCount);
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
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-7.24.0.npk"), [9, 9, 9, 9]);
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
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.24.0", StringComparison.Ordinal));
        Assert.Equal("7.24.0", v7.TryGetCurrent()!.VersionText);
    }

    private static string CreateFirmwareRootWithV6AndV7()
    {
        string root = Directory.CreateTempSubdirectory("twostage-fw-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-7.23.8.npk"), [5, 6, 7, 8]);
        return root;
    }

    private static RouterOsV6UpgradeExecutor CreateTwoStageExecutor(
        StageTrackingOps ops,
        IDeviceBackupService backup,
        string firmwareRoot,
        IMikrotikConnectionFactory? connectionFactory = null,
        TimeSpan? rebootTimeout = null,
        TimeSpan? postUpgradeVerifyTimeout = null,
        IRouterOsV7TargetProvider? v7TargetProvider = null)
    {
        TimeSpan reboot = rebootTimeout ?? TimeSpan.FromSeconds(5);
        TimeSpan verify = postUpgradeVerifyTimeout ?? reboot;
        if (verify > reboot)
        {
            verify = reboot;
        }

        return new RouterOsV6UpgradeExecutor(
            ops,
            connectionFactory ?? new ThrowingMikrotikConnectionFactory(new IOException("no reconnect")),
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(firmwareRoot),
                Options.Create(new FirmwareFilesOptions { Path = firmwareRoot })),
            backup,
            v7TargetProvider ?? new FixedRouterOsV7TargetProvider("7.23.8"),
            Options.Create(new FirmwareUpgradeOptions
            {
                Enabled = true,
                AdvertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
                FetchTimeout = TimeSpan.FromSeconds(5),
                ReconnectInterval = TimeSpan.FromMilliseconds(10),
                RebootTimeout = reboot,
                PostUpgradeVerifyTimeout = verify,
                BackupFileReadyTimeout = TimeSpan.FromMilliseconds(100),
                BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(20),
            }),
            Options.Create(new MikroTikOptions { Port = 8728 }),
            TimeProvider.System,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
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

        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (FailOnCallNumber is int n && CallCount == n)
            {
                return Task.FromResult(new DeviceBackupResult
                {
                    Succeeded = false,
                    FailureReason = "second-stage backup failed",
                });
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

        public string Identity { get; } = identity;

        public string InitialVersion { get; init; } = "6.49.10";

        public List<string> VersionsAfterReboot { get; init; } = [];

        public int? FailOnPostRebootDeviceInfoReadNumber { get; init; }

        public int RebootCount { get; private set; }

        public int PostRebootDeviceInfoReads { get; private set; }

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
            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([
                new("routeros-mipsbe", numeric, false),
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
            long size = fileName.Contains("7.23.8", StringComparison.Ordinal) ? 4 : 4;
            return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, size));
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
