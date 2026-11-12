// -----------------------------------------------------------------------------
// File: RouterOsV6UpgradeExecutorTests.cs
//
// Description:
// Unit/fake-based tests for RouterOsV6UpgradeExecutor workflow gates, stages,
// and outcomes.
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
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade;
/// <summary>
/// Fake-based tests for RouterOsV6UpgradeExecutor gates, stages, and outcomes.
/// </summary>
public sealed class RouterOsV6UpgradeExecutorTests
{
    /// <summary>
    /// A failing backup service gives <c>UpgradeOutcome.BackupFailed</c> with no fetch, no reboot and
    /// <c>RebootInitiated</c> false.
    /// </summary>
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

    /// <summary>
    /// A backup whose remote cleanup fails also gives <c>BackupFailed</c>, with a detail containing 'Remote cleanup
    /// failed'. Nothing is fetched or rebooted and <c>RebootInitiated</c> is false.
    /// </summary>
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

    /// <summary>
    /// With a successful backup and the firmware file present, the executor does attempt the fetch
    /// (<c>FetchCalled</c>). When the router never reports the file, the result is <c>UpgradeFailed</c> and no
    /// reboot is requested.
    /// </summary>
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

    /// <summary>
    /// A device already on RouterOS 7.23.8 gives <c>SkippedAlreadyAtTarget</c> with <c>CompletedStage</c>
    /// RouterboardFirmware and <c>PostUpgradeVersion</c> 7.23.8. Nothing is fetched and no reboot is requested.
    /// </summary>
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

    /// <summary>
    /// With upgrades disabled in options, the result is <c>SkippedUpgradeDisabled</c> and the router is never asked
    /// to fetch.
    /// </summary>
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

    /// <summary>
    /// A router with <c>routeros-mipsbe</c> and an installed <c>tr069-client</c> but only the main package in the
    /// firmware folder gives <c>PackageResolutionFailed</c>. Nothing is fetched or rebooted.
    /// </summary>
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

    /// <summary>
    /// Cancelling 50 ms into a fetch that would take 5 seconds gives <c>UpgradeInterrupted</c>. No reboot is
    /// requested and <c>RebootInitiated</c> is false.
    /// </summary>
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

    /// <summary>
    /// When the fetched file never appears on the router (<c>OmitFetchedFile</c>), the fetch is attempted but the
    /// result is <c>UpgradeFailed</c> and no reboot is requested.
    /// </summary>
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

    /// <summary>
    /// When the schedule-window check returns false, the result is <c>PolicyBlocked</c> with no fetch and no
    /// reboot.
    /// </summary>
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

    /// <summary>
    /// A fake clock advanced in 15 s steps past the 5 minute reboot timeout, with every reconnect failing ('still
    /// down'), gives <c>UpgradeTimedOut</c> and never <c>UpgradeSucceeded</c>. The reboot was requested and
    /// <c>RebootInitiated</c> is true.
    /// </summary>
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

    /// <summary>
    /// Scenario: Fake time starts at 12:00 and the reconnect factory only succeeds two minutes after the reboot. The
    /// executor retries every 15 seconds within a five-minute reboot timeout, and the test advances the clock in
    /// 15-second steps so the retry delays can complete. No RouterOS 7 target is configured.
    /// Expected: A reconnect succeeds before the deadline and the v6 stepping stage completes with 6.49.23 reported
    /// afterwards. The outcome is TargetUnavailable because no RouterOS 7 target follows, and at least one connection
    /// succeeded.
    /// Invariant: A reconnect inside the five-minute window counts as success, not as a timeout.
    /// </summary>
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

    /// <summary>
    /// A reconnect that first succeeds at 4:50 of the 5:00 reboot window, with 10 s retries, is accepted. The
    /// executor then stops at the v6 stepping stage with <c>TargetUnavailable</c> (no RouterOS 7 target is
    /// configured) instead of timing out, and <c>CompletedStage</c> is V6Stepping.
    /// </summary>
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

    /// <summary>
    /// Scenario: The reconnect factory never succeeds. The executor retries every 30 seconds within a five-minute
    /// reboot timeout, and the test advances the fake clock 30 seconds at a time.
    /// Expected: The outcome is UpgradeTimedOut, the fake clock has reached at least 12:05 (five minutes after the
    /// reboot), and at least two attempts were made.
    /// Invariant: Retries never extend the absolute five-minute deadline measured from the reboot.
    /// </summary>
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

    /// <summary>
    /// A reconnect attempt that hangs is cancelled when the fake clock reaches the 5 minute deadline, which the
    /// test confirms through the factory's cancellation signal. The result is <c>UpgradeTimedOut</c>, never
    /// <c>UpgradeSucceeded</c>.
    /// </summary>
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

    /// <summary>
    /// Allow the reconnect loop to enter its delay before cancelling.
    /// Cancelling while the reconnect loop waits out its 1 minute delay, then advancing the fake clock to release
    /// it, gives <c>UpgradeInterrupted</c> rather than a timeout or success. <c>RebootInitiated</c> is true.
    /// </summary>
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

    /// <summary>
    /// With a factory whose every attempt throws and the clock advanced in 20 s steps, the result is
    /// <c>UpgradeTimedOut</c> and never <c>UpgradeSucceeded</c>.
    /// </summary>
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

    /// <summary>
    /// After a successful reconnect to a router now reporting 6.49.23, the executor verifies the version.
    /// <c>PostUpgradeVersion</c> is 6.49.23 and <c>CompletedStage</c> is V6Stepping, and with no RouterOS 7 target
    /// the outcome is <c>TargetUnavailable</c>.
    /// </summary>
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

    /// <summary>
    /// Expire the (capped) verify poll window well inside the absolute reboot deadline.
    /// A router that reconnects reporting 6.48.1 instead of 6.49.23 gives <c>UpgradeVersionMismatch</c> once the
    /// verify poll window expires (clock advanced 2 s), not <c>UpgradeTimedOut</c>. <c>RebootInitiated</c> is true.
    /// </summary>
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

    /// <summary>
    /// Scenario: The reconnect factory succeeds only 4 minutes 50 seconds after the reboot, leaving 10 seconds of the
    /// five-minute window. Once the post-reboot read starts, the test advances the fake clock 15 seconds, past the
    /// absolute deadline.
    /// Expected: The outcome is UpgradeTimedOut (not success), the reboot is recorded as initiated, and the fake
    /// clock has reached at least 12:05.
    /// Invariant: Post-reboot verification cannot continue past the absolute deadline, even after a late reconnect.
    /// </summary>
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

    /// <summary>
    /// When the router reconnects within the deadline reporting 6.49.23, the post-reboot package list and device
    /// info are each read at least once. The run ends at V6Stepping with <c>TargetUnavailable</c>.
    /// </summary>
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

    /// <summary>
    /// These stage-oriented unit tests exercise the v6 stepping machinery. No RouterOS 7 target is configured,
    /// </summary>
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

    /// <summary>
    /// Connection factory driven by a fake clock: attempts fail until the clock reaches a chosen instant, then return
    /// a fully populated fake connection.
    /// </summary>
    private sealed class TimedReconnectFactory(FakeTimeProvider time, DateTimeOffset succeedAfter)
        : IMikrotikConnectionFactory
    {
        /// <summary>Total reconnect attempts, successful or not.</summary>
        public int Attempts { get; private set; }

        /// <summary>Number of attempts that succeeded after the router "came back".</summary>
        public int SuccessCount { get; private set; }

        /// <summary>
        /// Throws <see cref="IOException"/> (<c>router still rebooting</c>) while the clock is before the success
        /// time; afterwards returns a fake connection.
        /// </summary>
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
    /// Connection factory that blocks until cancelled, used to verify reboot-wait cancellation reaches an in-flight
    /// reconnect.
    /// </summary>
    private sealed class HangingReconnectFactory : IMikrotikConnectionFactory
    {
        /// <summary>Completed when the first reconnect attempt begins.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completed when the pending attempt observes cancellation.</summary>
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Signals <see cref="Started"/>, waits indefinitely, and signals <see cref="Cancelled"/> before rethrowing
        /// when cancelled.
        /// </summary>
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

    /// <summary>Builds a minimal scheduled job (router <c>CORE-01</c>, 10.0.0.50) for executor calls.</summary>
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

    /// <summary>
    /// Builds RouterOS 6.49.10 <see cref="MikrotikDeviceInfo"/> (mipsbe, hEX PoE) for a core router.
    /// </summary>
    private static MikrotikDeviceInfo CreateDevice() =>
        new()
        {
            Identity = "ACE01.POP001.001.core",
            Version = "6.49.10",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

    /// <summary>
    /// Builds a compatible, path-eligible assessment targeting RouterOS 7.23.8 with the given status and version
    /// classification.
    /// </summary>
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

    /// <summary>
    /// Configurable <see cref="IMikrotikRouterOperations"/> fake that reports a fixed post-upgrade version and
    /// records fetch, reboot, cleanup and RouterBOARD activity for executor stage assertions.
    /// </summary>
    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        /// <summary>
        /// True once <see cref="FetchUrlToFileAsync"/> has been invoked, i.e. the firmware package fetch was
        /// attempted.
        /// </summary>
        public bool FetchCalled { get; private set; }

        /// <summary>
        /// True once <see cref="RebootAsync"/> has been invoked, i.e. the executor requested a router reboot.
        /// </summary>
        public bool RebootCalled { get; private set; }

        /// <summary>
        /// When true, <see cref="FindFileAsync"/> reports <c>.npk</c> files as missing, simulating a fetch that
        /// produced nothing.
        /// </summary>
        public bool OmitFetchedFile { get; init; }

        /// <summary>
        /// Set by tests that expect the fetched package size to match; the fake already reports 4-byte files, so this
        /// flag does not alter behaviour.
        /// </summary>
        public bool FetchReturnsMatchingSize { get; init; }

        /// <summary>
        /// Delay applied inside <see cref="FetchUrlToFileAsync"/>, used to exercise fetch timeouts and cancellation.
        /// </summary>
        public TimeSpan FetchDelay { get; init; }

        /// <summary>RouterOS version reported by device-info and package reads (default 6.49.23).</summary>
        public string PostUpgradeVersion { get; init; } = "6.49.23";

        /// <summary>
        /// Defaults to ACE so stage-unit tests skip the RouterOS 7 transition after v6 stepping.
        /// </summary>
        public string Identity { get; init; } = "ACE01.POP001.001.core";

        /// <summary>RouterBOARD firmware version currently running on the fake router.</summary>
        public string RouterboardCurrentFirmware { get; init; } = "7.23.8";

        /// <summary>
        /// RouterBOARD firmware version available for upgrade; equal to the current firmware means no RouterBOARD
        /// upgrade is needed.
        /// </summary>
        public string RouterboardUpgradeFirmware { get; init; } = "7.23.8";

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

        /// <summary>Number of device-info reads performed after a reboot was requested.</summary>
        public int PostRebootDeviceInfoCount { get; private set; }

        /// <summary>Number of package listings performed after a reboot was requested.</summary>
        public int PostRebootPackageListCount { get; private set; }

        /// <summary>Number of RouterBOARD firmware upgrades issued.</summary>
        public int RouterboardUpgradeCount { get; private set; }

        /// <summary>Names of legacy fleet-upgrade scripts currently present on the fake router.</summary>
        public List<string> PresentScripts { get; init; } = [];

        /// <summary>Names of legacy fleet-upgrade schedulers currently present on the fake router.</summary>
        public List<string> PresentSchedulers { get; init; } = [];

        /// <summary>
        /// Ordered log of removals (<c>script:name</c> / <c>scheduler:name</c>) used to assert cleanup order.
        /// </summary>
        public List<string> RemovalOrder { get; } = [];

        /// <summary>
        /// Number of scheduler inspections performed; zero proves cleanup never looked for schedulers.
        /// </summary>
        public int SchedulerCountCalls { get; private set; }

        /// <summary>Number of script inspections performed; zero proves cleanup never looked for scripts.</summary>
        public int ScriptCountCalls { get; private set; }

        /// <summary>
        /// Per-file sizes (case-insensitive) reported by <see cref="FindFileAsync"/>, letting tests simulate
        /// truncated or mismatched uploads.
        /// </summary>
        public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Explicit package inventory returned by <see cref="ListInstalledPackagesAsync"/>; when null a single main
        /// package is derived from the version.
        /// </summary>
        public IReadOnlyList<InstalledPackageInfo>? Installed { get; init; }

        /// <summary>
        /// Returns device info at <see cref="PostUpgradeVersion"/>. After a reboot, reads are counted and optionally
        /// block until cancelled (<see cref="HangPostRebootReadUntilCancelled"/>).
        /// </summary>
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

        /// <summary>
        /// Returns <see cref="Installed"/> when set; otherwise a single main package (<c>routeros-mipsbe</c> or
        /// <c>routeros</c>) at the post-upgrade version. Counts post-reboot listings.
        /// </summary>
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
        /// Returns null for <c>.npk</c> files when <see cref="OmitFetchedFile"/> is set; otherwise the size
        /// registered in <see cref="FileSizes"/>, or a 4-byte file.
        /// </summary>
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
        /// Marks the fetch as called and optionally waits <see cref="FetchDelay"/> (cancellable); no data is
        /// transferred.
        /// </summary>
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

        /// <summary>Marks the reboot as requested.</summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
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
        /// Records <c>script:name</c> in <see cref="RemovalOrder"/> and removes matching entries from <see
        /// cref="PresentScripts"/>.
        /// </summary>
        public Task RemoveSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default)
        {
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
        /// Records <c>scheduler:name</c> in <see cref="RemovalOrder"/> and removes matching entries from <see
        /// cref="PresentSchedulers"/>.
        /// </summary>
        public Task RemoveSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default)
        {
            RemovalOrder.Add("scheduler:" + schedulerName);
            PresentSchedulers.RemoveAll(n => n.Equals(schedulerName, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        /// <summary>Counts the RouterBOARD firmware upgrade request.</summary>
        public Task UpgradeRouterboardAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            RouterboardUpgradeCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Backup fake that always fails with a configurable reason; a reason containing "cleanup" also flags remote
    /// cleanup failure.
    /// </summary>
    private sealed class FailingBackup(string reason = "backup failed") : IDeviceBackupService
    {
        /// <summary>
        /// Returns a failed result carrying the configured reason, with <c>RemoteCleanupFailed</c> set when the
        /// reason mentions cleanup.
        /// </summary>
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
}
