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
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade;
public sealed class RouterOsV6UpgradeExecutorTests
{
    [Fact]
    public async Task BackupFailure_PreventsFetchAndReboot()
    {
        TrackingOps ops = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup: new FailingBackup(),
            enabled: true);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.BackupFailed, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
        Assert.False(result.RebootInitiated);
    }

    [Fact]
    public async Task RemoteCleanupFailure_PreventsFetchAndReboot()
    {
        TrackingOps ops = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup: new FailingBackup(
                "Remote cleanup failed after central verification: remove failed. Remaining remote files: a.rsc, a.backup"),
            enabled: true);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.BackupFailed, result.Outcome);
        Assert.Contains("Remote cleanup failed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
        Assert.False(result.RebootInitiated);
    }

    [Fact]
    public async Task SuccessfulBackup_AllowsPackageStagingFetch_WithoutRebootWhenFetchFailsMissingFile()
    {
        TrackingOps ops = new() { OmitFetchedFile = true };
        string root = Directory.CreateTempSubdirectory("upg-fetch-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);

        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup: new SucceedingBackup(),
            enabled: true,
            firmwareRoot: root,
            advertisedBaseUrl: "http://198.18.0.1:8080/firmware_files");

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.True(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
        Assert.Equal(UpgradeOutcome.UpgradeFailed, result.Outcome);
    }

    [Fact]
    public async Task V7Device_AtTarget_IsNeverModified()
    {
        TrackingOps ops = new()
        {
            Identity = "AP01.POP001.001.access",
            PostUpgradeVersion = "7.23.8",
        };
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: true, v7Target: "7.23.8");

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "7.23.8"),
            CreateJob(),
            new MikrotikDeviceInfo
            {
                Identity = "AP01.POP001.001.access",
                Version = "7.23.8",
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
                RouterboardCurrentFirmware = "7.23.8",
                RouterboardUpgradeFirmware = "7.23.8",
            },
            CreateAssessment(DeviceAssessmentStatus.AlreadyAtTarget, RouterOsVersionClassification.RouterOsV7OrLater),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.SkippedAlreadyAtTarget, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task UpgradeDisabled_SkipsWithoutFetch()
    {
        TrackingOps ops = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: false);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.SkippedUpgradeDisabled, result.Outcome);
        Assert.False(ops.FetchCalled);
    }

    [Fact]
    public async Task MissingOptionalPackage_PreventsFetchAndReboot()
    {
        TrackingOps ops = new()
        {
            Installed =
            [
                new("routeros-mipsbe", "6.49.23", false),
                new("tr069-client", "6.49.23", false),
            ],
        };
        string root = Directory.CreateTempSubdirectory("upg-missing-opt-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);

        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            firmwareRoot: root);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.PackageResolutionFailed, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task CancellationDuringFetch_PreventsReboot()
    {
        TrackingOps ops = new() { FetchDelay = TimeSpan.FromSeconds(5) };
        using CancellationTokenSource cts = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: true);

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            cts.Token);

        await Task.Delay(50);
        cts.Cancel();
        UpgradeExecutionResult result = await task;

        Assert.Equal(UpgradeOutcome.UpgradeInterrupted, result.Outcome);
        Assert.False(ops.RebootCalled);
        Assert.False(result.RebootInitiated);
    }

    [Fact]
    public async Task IncompleteStaging_PreventsReboot()
    {
        TrackingOps ops = new() { OmitFetchedFile = true };
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: true);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeFailed, result.Outcome);
        Assert.True(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task OutsideScheduleWindow_PreventsFetchAndReboot()
    {
        TrackingOps ops = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: true);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => false,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.PolicyBlocked, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task ReconnectionTimeout_AfterReboot_ReportsTimedOut()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: new ThrowingMikrotikConnectionFactory(new IOException("still down")),
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(15));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => !task.IsCompleted, TimeSpan.FromSeconds(1));
        for (int i = 0; i < 25; i++)
        {
            time.Advance(TimeSpan.FromSeconds(15));
            await Task.Yield();
            if (task.IsCompleted)
            {
                break;
            }
        }

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.True(ops.RebootCalled);
        Assert.True(result.RebootInitiated);
    }

    [Fact]
    public async Task Reconnection_SucceedsWithinFiveMinutes()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        TimedReconnectFactory factory = new(time, succeedAfter: time.GetUtcNow() + TimeSpan.FromMinutes(2));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: factory,
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(15));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => factory.Attempts >= 1, TimeSpan.FromSeconds(5));
        // Failed attempts then delay; advance past succeedAfter in steps so delays can complete.
        for (int i = 0; i < 20 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(15));
            await Task.Yield();
        }

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeOutcome.TargetUnavailable, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.V6Stepping, result.CompletedStage);
        Assert.Equal("6.49.23", result.PostUpgradeVersion);
        Assert.True(factory.SuccessCount >= 1);
    }

    [Fact]
    public async Task Reconnection_AtFourMinutesFiftySeconds_SucceedsBeforeDeadline()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        TimedReconnectFactory factory = new(
            time,
            succeedAfter: time.GetUtcNow() + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(50));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: factory,
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(10));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => factory.Attempts >= 1, TimeSpan.FromSeconds(5));
        for (int i = 0; i < 40 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeOutcome.TargetUnavailable, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.V6Stepping, result.CompletedStage);
    }

    [Fact]
    public async Task Reconnection_RetryDoesNotExtendAbsoluteDeadline()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        CountingThrowFactory factory = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: factory,
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(30));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => factory.Attempts >= 1, TimeSpan.FromSeconds(5));
        // Many retries — wall-clock of fake time must still stop at 5 minutes from reboot.
        for (int i = 0; i < 40; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            await Task.Yield();
            if (task.IsCompleted)
            {
                break;
            }
        }

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.True(time.GetUtcNow() >= new DateTimeOffset(2026, 11, 10, 12, 5, 0, TimeSpan.Zero));
        Assert.True(factory.Attempts >= 2);
    }

    [Fact]
    public async Task Reconnection_ConnectionAttemptCannotOverrunRemainingDeadline()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        HangingReconnectFactory factory = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: factory,
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(15));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(5));
        await factory.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
    }

    [Fact]
    public async Task Reconnection_CancellationStopsImmediately()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        using CancellationTokenSource cts = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: new ThrowingMikrotikConnectionFactory(new IOException("still down")),
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromMinutes(1));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            cts.Token);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        // Allow the reconnect loop to enter its delay before cancelling.
        await Task.Yield();
        await cts.CancelAsync();
        // Unblock Delay waiting on fake time.
        time.Advance(TimeSpan.FromMinutes(1));
        await Task.Yield();
        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(UpgradeOutcome.UpgradeInterrupted, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.True(result.RebootInitiated);
    }

    [Fact]
    public async Task Reconnection_TimeoutNeverProducesUpgradeSucceeded()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        CountingThrowFactory factory = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: factory,
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(20));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => factory.Attempts >= 1, TimeSpan.FromSeconds(5));
        for (int i = 0; i < 20 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(20));
            await Task.Yield();
        }

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
    }

    [Fact]
    public async Task PostUpgradeVerification_RunsAfterSuccessfulReconnect()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true, PostUpgradeVersion = "6.49.23" };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        ScriptedMikrotikConnectionFactory reconnect = new(_ => FakeMikrotikConnections.CreateFullyPopulated());
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: reconnect,
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(1));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.TargetUnavailable, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.V6Stepping, result.CompletedStage);
        Assert.Equal("6.49.23", result.PostUpgradeVersion);
    }

    [Fact]
    public async Task PostUpgradeVersionMismatch_IsReported()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true, PostUpgradeVersion = "6.48.1" };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        ScriptedMikrotikConnectionFactory reconnect = new(_ => FakeMikrotikConnections.CreateFullyPopulated());
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: reconnect,
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(1),
            postUpgradeVerifyTimeout: TimeSpan.FromSeconds(1));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        // Expire the (capped) verify poll window well inside the absolute reboot deadline.
        time.Advance(TimeSpan.FromSeconds(2));
        await Task.Yield();

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpgradeOutcome.UpgradeVersionMismatch, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.True(result.RebootInitiated);
    }

    [Fact]
    public async Task PostRebootVerification_CannotContinuePastAbsoluteDeadline_AfterLateReconnect()
    {
        TrackingOps ops = new()
        {
            FetchReturnsMatchingSize = true,
            HangPostRebootReadUntilCancelled = true,
        };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        // Reconnect with 10s remaining so verification is forced onto the absolute deadline.
        TimedReconnectFactory factory = new(
            time,
            succeedAfter: time.GetUtcNow() + TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(50));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: factory,
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(10));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => factory.Attempts >= 1, TimeSpan.FromSeconds(5));
        for (int i = 0; i < 40 && !ops.PostRebootReadStarted.Task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Yield();
        }

        await ops.PostRebootReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Absolute deadline is T+5:00; late reconnect must not allow verification to overrun it.
        time.Advance(TimeSpan.FromSeconds(15));
        await Task.Yield();

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.True(result.RebootInitiated);
        Assert.True(time.GetUtcNow() >= new DateTimeOffset(2026, 11, 10, 12, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task PostRebootVerification_CompletingInsideDeadline_StillRunsPackageChecks()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true, PostUpgradeVersion = "6.49.23" };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        ScriptedMikrotikConnectionFactory reconnect = new(_ => FakeMikrotikConnections.CreateFullyPopulated());
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: reconnect,
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(1));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.TargetUnavailable, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.V6Stepping, result.CompletedStage);
        Assert.True(ops.PostRebootPackageListCount >= 1);
        Assert.True(ops.PostRebootDeviceInfoCount >= 1);
    }

    private static RouterOsV6UpgradeExecutor CreateExecutor(
        TrackingOps ops,
        IDeviceBackupService backup,
        bool enabled,
        string? firmwareRoot = null,
        string advertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
        IMikrotikConnectionFactory? connectionFactory = null,
        TimeProvider? timeProvider = null,
        TimeSpan? rebootTimeout = null,
        TimeSpan? reconnectInterval = null,
        TimeSpan? postUpgradeVerifyTimeout = null,
        string? v7Target = null)
    {
        string root = firmwareRoot ?? Directory.CreateTempSubdirectory("upg-fw-").FullName;
        if (firmwareRoot is null)
        {
            File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        }

        TimeSpan effectiveRebootTimeout = rebootTimeout ?? TimeSpan.FromMilliseconds(50);
        TimeSpan effectiveVerifyTimeout = postUpgradeVerifyTimeout ?? effectiveRebootTimeout;
        if (effectiveVerifyTimeout > effectiveRebootTimeout)
        {
            effectiveVerifyTimeout = effectiveRebootTimeout;
        }

        // These stage-oriented unit tests exercise the v6 stepping machinery. No RouterOS 7 target is configured,
        // so after a verified v6 step the workflow stops with TargetUnavailable (a Blocked job, never a success).
        IMikrotikConnectionFactory factory = connectionFactory
            ?? new ThrowingMikrotikConnectionFactory(new IOException("no reconnect in this test"));
        IOptions<FirmwareUpgradeOptions> upgradeOptions = Options.Create(new FirmwareUpgradeOptions
        {
            Enabled = enabled,
            AdvertisedBaseUrl = advertisedBaseUrl,
            FetchTimeout = TimeSpan.FromSeconds(5),
            ReconnectInterval = reconnectInterval ?? TimeSpan.FromMilliseconds(10),
            RebootTimeout = effectiveRebootTimeout,
            PostUpgradeVerifyTimeout = effectiveVerifyTimeout,
            BackupFileReadyTimeout = TimeSpan.FromMilliseconds(100),
            BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(20),
        });
        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = root });
        IOptions<MikroTikOptions> mikrotikOptions = Options.Create(new MikroTikOptions { Port = 8728 });

        return new RouterOsV6UpgradeExecutor(
            ops,
            factory,
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(root),
                firmwareFilesOptions),
            backup,
            new FixedRouterOsV7TargetProvider(v7Target),
            new LegacyFleetUpgradeAutomationCleanup(
                ops,
                NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance),
            new RouterboardFirmwareUpgradeService(
                ops,
                factory,
                upgradeOptions,
                mikrotikOptions,
                timeProvider ?? TimeProvider.System,
                NullLogger<RouterboardFirmwareUpgradeService>.Instance),
            new PassingOspfMigrationService(),
            new PassingOspfStaticSummaryRedistributionService(),
            upgradeOptions,
            mikrotikOptions,
            timeProvider ?? TimeProvider.System,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
    }

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

    private sealed class TimedReconnectFactory(FakeTimeProvider time, DateTimeOffset succeedAfter)
        : IMikrotikConnectionFactory
    {
        public int Attempts { get; private set; }

        public int SuccessCount { get; private set; }

        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (time.GetUtcNow() < succeedAfter)
            {
                throw new IOException("router still rebooting");
            }

            SuccessCount++;
            return Task.FromResult<ITikConnection>(FakeMikrotikConnections.CreateFullyPopulated());
        }
    }

    private sealed class CountingThrowFactory : IMikrotikConnectionFactory
    {
        public int Attempts { get; private set; }

        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new IOException("still down");
        }
    }

    private sealed class HangingReconnectFactory : IMikrotikConnectionFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }

    private static MikrotikScheduledJob CreateJob() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "CORE-01",
            RouterIp = IPAddress.Parse("10.0.0.50"),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    private static MikrotikDeviceInfo CreateDevice() =>
        new()
        {
            Identity = "ACE01.POP001.001.core",
            Version = "6.49.10",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

    private static DeviceCompatibilityAssessment CreateAssessment(
        DeviceAssessmentStatus status,
        RouterOsVersionClassification classification = RouterOsVersionClassification.OlderThanTargetV6) =>
        new()
        {
            RouterIp = "10.0.0.50",
            OverallStatus = status,
            RouterOsVersionClassification = classification,
            TargetRouterOsVersion = "7.23.8",
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        public bool FetchCalled { get; private set; }

        public bool RebootCalled { get; private set; }

        public bool OmitFetchedFile { get; init; }

        public bool FetchReturnsMatchingSize { get; init; }

        public TimeSpan FetchDelay { get; init; }

        public string PostUpgradeVersion { get; init; } = "6.49.23";

        /// <summary>
        /// Defaults to ACE so stage-unit tests skip the RouterOS 7 transition after v6 stepping.
        /// </summary>
        public string Identity { get; init; } = "ACE01.POP001.001.core";

        public string RouterboardCurrentFirmware { get; init; } = "7.23.8";

        public string RouterboardUpgradeFirmware { get; init; } = "7.23.8";

        public bool HangPostRebootReadUntilCancelled { get; init; }

        public TaskCompletionSource PostRebootReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PostRebootDeviceInfoCount { get; private set; }

        public int PostRebootPackageListCount { get; private set; }

        public int RouterboardUpgradeCount { get; private set; }

        public List<string> PresentScripts { get; init; } = [];

        public List<string> PresentSchedulers { get; init; } = [];

        public List<string> RemovalOrder { get; } = [];

        public int SchedulerCountCalls { get; private set; }

        public int ScriptCountCalls { get; private set; }

        public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<InstalledPackageInfo>? Installed { get; init; }

        public async Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default)
        {
            if (RebootCalled)
            {
                PostRebootDeviceInfoCount++;
                if (HangPostRebootReadUntilCancelled)
                {
                    PostRebootReadStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
            }

            return new MikrotikDeviceInfo
            {
                Identity = Identity,
                Version = PostUpgradeVersion,
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
                RouterboardCurrentFirmware = RouterboardCurrentFirmware,
                RouterboardUpgradeFirmware = RouterboardUpgradeFirmware,
            };
        }

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            if (RebootCalled)
            {
                PostRebootPackageListCount++;
            }

            string versionText = PostUpgradeVersion;
            string numeric = RouterOsVersion.TryParse(versionText, out RouterOsVersion parsed)
                ? parsed.ToString()
                : "6.49.23";
            string main = RouterOsVersion.TryParse(versionText, out RouterOsVersion ver) && ver.IsV7OrLater
                ? "routeros"
                : "routeros-mipsbe";
            return Task.FromResult(Installed ??
            [
                new(main, numeric, false),
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
            if (OmitFetchedFile && fileName.EndsWith(".npk", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<RouterFileInfo?>(null);
            }

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

        public async Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            if (FetchDelay > TimeSpan.Zero)
            {
                await Task.Delay(FetchDelay, cancellationToken);
            }
        }

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
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
            RemovalOrder.Add("scheduler:" + schedulerName);
            PresentSchedulers.RemoveAll(n => n.Equals(schedulerName, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        public Task UpgradeRouterboardAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            RouterboardUpgradeCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingBackup(string reason = "backup failed") : IDeviceBackupService
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
                FailureReason = reason,
                RemoteCleanupFailed = reason.Contains("cleanup", StringComparison.OrdinalIgnoreCase),
            });
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
}
