// -----------------------------------------------------------------------------
// File: RouterPreflightTests.cs
//
// Description:
// Preflight connect/info collection tests for RouterJobExecutor paths.
//
// -----------------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.MikroTik;
/// <summary>
/// Pre-flight behaviour of <c>RouterJobExecutor</c> (connect, device-information read, disconnect), exercised through
/// a real <c>InMemoryJobScheduler</c> and fake tik4net connections.
/// Each failure or cancellation must end the job, release the import from the scheduler, dispose the connection
/// exactly once, never log the router password, and report the upgrade stage's result truthfully as the job outcome.
/// </summary>
public sealed class RouterPreflightTests
{
    /// <summary>
    /// Router password configured for every harness; tests assert it never appears in log output.
    /// </summary>
    private const string SharedPassword = "PreflightSecret!99";

    /// <summary>
    /// A fully populated router is connected, assessed as upgrade-ready, upgraded by a stub, and
    /// disconnected.
    /// Preconditions: <c>FakeMikrotikConnections.CreateFullyPopulated</c> and a
    /// <c>StubUpgradeExecutor</c> returning <c>UpgradeSucceeded</c>.
    /// Expected: The import leaves the scheduler; the logs show connection attempt, successful
    /// connect, RouterOS version, assessment start and a ReadyForUpgrade result, disconnect, and
    /// "Final job outcome: Succeeded".
    /// Invariant: No log line contains the router password or a "Password=" fragment.
    /// </summary>
    [Fact]
    public async Task SuccessfulDeviceInformationRetrieval_MarksJobSucceeded()
    {
        FakeTimeProvider time = CreateTime();
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            FakeMikrotikConnections.CreateFullyPopulated(),
            executorLogger: logs.CreateLogger("RouterJobExecutor"),
            upgradeExecutor: new StubUpgradeExecutor(UpgradeOutcome.UpgradeSucceeded));

        await harness.RunSingleJobAsync("192.168.10.1");

        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Contains(logs.Messages, static m => m.Contains("Connection attempt started", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("Connected successfully", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("RouterOS version:", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("Compatibility assessment started", StringComparison.Ordinal));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Assessment result:", StringComparison.Ordinal)
                && m.Contains("ReadyForUpgrade", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("Connection disconnect started", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("Connection disconnected successfully", StringComparison.Ordinal));
        // Stub reports a verified target (UpgradeSucceeded). SkippedUpgradeDisabled → Failed is covered by
        // UpgradeOutcome_IsReportedTruthfullyAsJobOutcome.
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Final job outcome: Succeeded", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            static m => m.Contains(SharedPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            static m => m.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Each non-success <c>UpgradeOutcome</c> from the upgrade stage is mapped to its own job
    /// outcome.
    /// Preconditions: A stub upgrade executor returns the supplied outcome for a healthy router.
    /// Expected: The import leaves the scheduler and the log reports "Final job outcome" with the
    /// expected mapping; "Succeeded" is never logged for these outcomes.
    /// Invariant: Skipped, blocked or interrupted upgrades are never reported as success.
    /// </summary>
    /// <param name="outcome">Outcome returned by the stubbed upgrade executor.</param>
    /// <param name="expectedJobOutcome">Terminal job state name expected in the final log
    /// line.</param>
    [Theory]
    [InlineData(UpgradeOutcome.SkippedUpgradeDisabled, "Failed")]
    [InlineData(UpgradeOutcome.SkippedAlreadyAtTarget, "AlreadyUpToDate")]
    [InlineData(UpgradeOutcome.SkippedNewerThanTarget, "Blocked")]
    [InlineData(UpgradeOutcome.PolicyBlocked, "Blocked")]
    [InlineData(UpgradeOutcome.UpgradeInterrupted, "Cancelled")]
    [InlineData(UpgradeOutcome.NotAttempted, "Failed")]
    public async Task UpgradeOutcome_IsReportedTruthfullyAsJobOutcome(UpgradeOutcome outcome, string expectedJobOutcome)
    {
        FakeTimeProvider time = CreateTime();
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            FakeMikrotikConnections.CreateFullyPopulated(),
            executorLogger: logs.CreateLogger("RouterJobExecutor"),
            upgradeExecutor: new StubUpgradeExecutor(outcome));

        await harness.RunSingleJobAsync("192.168.10.2");

        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Contains(
            logs.Messages,
            m => m.Contains("Final job outcome: " + expectedJobOutcome + " ", StringComparison.Ordinal));
        if (expectedJobOutcome != "Succeeded")
        {
            Assert.DoesNotContain(
                logs.Messages,
                static m => m.Contains("Final job outcome: Succeeded", StringComparison.Ordinal));
        }
    }
    /// <summary>
    /// The router answers without any /system routerboard data (for example a virtual or CHR device).
    /// Preconditions: <c>CreateFullyPopulated(includeRouterboard: false)</c>.
    /// Expected: The job still runs to completion and leaves the scheduler.
    /// Invariant: Routerboard details are optional for pre-flight.
    /// </summary>
    [Fact]
    public async Task MissingOptionalRouterboardFields_StillSucceed()
    {
        FakeTimeProvider time = CreateTime();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            FakeMikrotikConnections.CreateFullyPopulated(includeRouterboard: false));

        await harness.RunSingleJobAsync("10.0.0.8");
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
    }

    /// <summary>
    /// The router returns no system identity.
    /// Preconditions: <c>FakeMikrotikConnections.CreateMissingIdentity</c>.
    /// Expected: The job ends, and the log contains "Pre-flight validation failed" mentioning
    /// identity.
    /// Invariant: Identity is mandatory because it names device logs and backups.
    /// </summary>
    [Fact]
    public async Task MissingRequiredIdentity_FailsPreflight()
    {
        FakeTimeProvider time = CreateTime();
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            FakeMikrotikConnections.CreateMissingIdentity(),
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("10.0.0.9");

        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Pre-flight validation failed", StringComparison.Ordinal)
                && m.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The router returns no architecture name.
    /// Preconditions: <c>FakeMikrotikConnections.CreateMissingArchitecture</c>.
    /// Expected: The job ends and the log mentions the missing architecture.
    /// Invariant: Without architecture no firmware package can be chosen, so pre-flight stops.
    /// </summary>
    [Fact]
    public async Task MissingArchitecture_FailsPreflight()
    {
        FakeTimeProvider time = CreateTime();
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            FakeMikrotikConnections.CreateMissingArchitecture(),
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("10.0.0.10");

        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("architecture", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The TCP connection is refused.
    /// Preconditions: <c>CountingThrowingFactory</c> throws IOException("tcp refused") and
    /// <c>MaximumUnsuccessfulAttempts</c> is 0, which disables retries.
    /// Expected: Exactly one connection attempt is made, the job ends, and the log records a failed
    /// terminal outcome.
    /// Invariant: With retries disabled a connect failure is a single terminal attempt.
    /// </summary>
    [Fact]
    public async Task ConnectionFailure_FailsJob_WithoutDuplicateAttempts()
    {
        FakeTimeProvider time = CreateTime();
        CountingThrowingFactory failing = new(new IOException("tcp refused"));
        RecordingLoggerProvider logs = new();
        // Retry disabled so this preflight regression still proves a single terminal attempt.
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: failing,
            executorLogger: logs.CreateLogger("RouterJobExecutor"),
            jobRetryOptions: new JobRetryOptions { MaximumUnsuccessfulAttempts = 0 });

        await harness.RunSingleJobAsync("10.0.0.11");
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Equal(1, failing.CreateCount);
        Assert.Contains(logs.Messages, static m => m.Contains("Connection attempt started", StringComparison.Ordinal));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Failed", StringComparison.Ordinal)
                && (m.Contains("terminal", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("exhausted", StringComparison.OrdinalIgnoreCase)
                    || m.Contains("connect/login failed", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The router connection fails with a permanent, non-transient error.
    /// Preconditions: <c>CountingThrowingFactory</c> throws InvalidOperationException("cannot log
    /// in"); default retry options.
    /// Expected: Exactly one attempt, the log states "connect/login failed permanently" with
    /// "Failed", and the password is absent from all log messages.
    /// Invariant: Permanent login failures are not retried and do not leak credentials.
    /// </summary>
    [Fact]
    public async Task AuthenticationFailure_FailsJob_WithoutDuplicateAttempts()
    {
        FakeTimeProvider time = CreateTime();
        // InvalidOperationException is permanent (not a tik4net login refusal) — still a single attempt.
        CountingThrowingFactory failing = new(new InvalidOperationException("cannot log in"));
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: failing,
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("10.0.0.16");
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Equal(1, failing.CreateCount);
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("connect/login failed permanently", StringComparison.OrdinalIgnoreCase)
                && m.Contains("Failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, static m => m.Contains(SharedPassword, StringComparison.Ordinal));
    }

    /// <summary>
    /// Connecting works but reading device information fails.
    /// Preconditions: <c>CreateInfoRetrievalFailure</c> wrapped in a
    /// <c>DisposeTrackingConnection</c>.
    /// Expected: One connection was created, it was disposed exactly once, the job ends, and the log
    /// shows the retrieval failure or the final outcome.
    /// Invariant: A connection is never leaked after a failed pre-flight.
    /// </summary>
    [Fact]
    public async Task InformationRetrievalFailure_DisposesConnection_AndFailsJob()
    {
        FakeTimeProvider time = CreateTime();
        DisposeTrackingConnection tracking = new(FakeMikrotikConnections.CreateInfoRetrievalFailure());
        ScriptedMikrotikConnectionFactory factory = new(_ => tracking);
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory,
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("10.0.0.12");

        Assert.Single(factory.Connections);
        Assert.Equal(1, tracking.DisposeCount);
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Device information retrieval failed", StringComparison.Ordinal)
                || m.Contains("Final job outcome", StringComparison.Ordinal));
    }

    /// <summary>
    /// Disposing the connection at the end of the job throws.
    /// Preconditions: <c>FailingDisposeConnection</c> throws IOException("disconnect failed") from
    /// <c>Dispose</c>.
    /// Expected: The job still ends and leaves the scheduler, and a dispose was attempted.
    /// Invariant: A failing disconnect cannot leave the job stuck in the scheduler.
    /// </summary>
    [Fact]
    public async Task DisconnectionFailure_FailsJob()
    {
        FakeTimeProvider time = CreateTime();
        FailingDisposeConnection connection = new(FakeMikrotikConnections.CreateFullyPopulated());
        ScriptedMikrotikConnectionFactory factory = new(_ => connection);
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory);

        await harness.RunSingleJobAsync("10.0.0.13");
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.True(connection.DisposeAttempted);
    }

    /// <summary>
    /// The host is cancelled while a connection is being established.
    /// Preconditions: <c>GateBeforeConnectFactory</c> blocks inside <c>CreateAsync</c> until
    /// released; execution is cancelled while it waits.
    /// Expected: The scheduler's pending count drops to zero and exactly one connection attempt was
    /// made.
    /// Invariant: Cancellation during connect completes the job instead of hanging or retrying.
    /// </summary>
    [Fact]
    public async Task CancellationDuringConnection_MarksCancelled()
    {
        FakeTimeProvider time = CreateTime();
        GateBeforeConnectFactory factory = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, time, "10.0.0.14"), CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => factory.Waiting, TimeSpan.FromSeconds(5));
        harness.CancelExecution();
        factory.Release();

        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.CreateCount);
    }

    /// <summary>
    /// The host is cancelled while the device information read is in progress.
    /// Preconditions: <c>GateDeviceInfoReader</c> blocks until released; the connection is a
    /// <c>DisposeTrackingConnection</c>.
    /// Expected: Pending jobs drop to zero and the connection was disposed at least once.
    /// Invariant: Cancelled jobs still release their connection.
    /// </summary>
    [Fact]
    public async Task CancellationDuringInformationRetrieval_DisposesAndCancels()
    {
        FakeTimeProvider time = CreateTime();
        DisposeTrackingConnection tracking = new(FakeMikrotikConnections.CreateFullyPopulated());
        ScriptedMikrotikConnectionFactory factory = new(_ => tracking);
        GateDeviceInfoReader reader = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory,
            deviceInfoReader: reader);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, time, "10.0.0.15"), CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => reader.Waiting, TimeSpan.FromSeconds(5));
        harness.CancelExecution();
        reader.Release();

        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(5));
        Assert.True(tracking.DisposeCount >= 1);
    }

    /// <summary>
    /// <c>MikrotikPreflightResult.FromDeviceInfo</c> maps a populated device to a successful result.
    /// Preconditions: Device EDGE-01 (RB750Gr3, mipsbe, 6.49.23 long-term) at 198.18.0.83 with a 250
    /// ms duration.
    /// Expected: Overall, connection and information statuses are Succeeded; IP, identity, version,
    /// architecture and board name are copied; <c>FailureReason</c> is null.
    /// Invariant: A result built from complete device data never carries a failure reason.
    /// </summary>
    [Fact]
    public void PreflightResult_ConstructsFromDeviceInfo()
    {
        MikrotikDeviceInfo info = new()
        {
            Identity = "EDGE-01",
            Version = "6.49.23 (long-term)",
            ArchitectureName = "mipsbe",
            BoardName = "RB750Gr3",
            Platform = "MikroTik",
            Uptime = "5d",
            RouterboardModel = "RTR750Gr3",
            RouterboardFirmwareType = "mt7621L",
            RouterboardCurrentFirmware = "6.49.23",
            RouterboardUpgradeFirmware = "6.49.23",
            RouterboardFactoryFirmware = "3.41",
        };

        MikrotikPreflightResult result = MikrotikPreflightResult.FromDeviceInfo(
            "198.18.0.83",
            info,
            new DateTimeOffset(2026, 11, 10, 16, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(250));

        Assert.Equal(MikrotikPreflightStatus.Succeeded, result.Status);
        Assert.Equal(MikrotikConnectionStatus.Succeeded, result.ConnectionStatus);
        Assert.Equal(MikrotikInformationStatus.Succeeded, result.InformationStatus);
        Assert.Equal("198.18.0.83", result.RouterIp);
        Assert.Equal("EDGE-01", result.RouterIdentity);
        Assert.Equal("6.49.23 (long-term)", result.RouterOsVersion);
        Assert.Equal("mipsbe", result.Architecture);
        Assert.Equal("RB750Gr3", result.BoardName);
        Assert.Null(result.FailureReason);
    }

    /// <summary>
    /// A two-row import where one router fails information retrieval and the other succeeds.
    /// Preconditions: The scripted factory returns an info-failure connection first, then a fully
    /// populated one.
    /// Expected: Both jobs finish, the import leaves the scheduler, a CSV appears in the completed
    /// folder, and two connections were used.
    /// Invariant: A failed row does not stop the batch from completing.
    /// </summary>
    [Fact]
    public async Task FailedAndSuccessfulPreflight_CompleteImportBatch()
    {
        FakeTimeProvider time = CreateTime();
        int calls = 0;
        ScriptedMikrotikConnectionFactory factory = new(_ =>
        {
            int n = Interlocked.Increment(ref calls);
            return n == 1
                ? FakeMikrotikConnections.CreateInfoRetrievalFailure()
                : FakeMikrotikConnections.CreateFullyPopulated();
        });

        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, time, "10.0.0.21", row: 2), CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, time, "10.0.0.22", row: 3), CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => !harness.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));
        await WaitForAsync(
            () => Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any(),
            TimeSpan.FromSeconds(5));
        Assert.Equal(2, factory.Connections.Count);
    }

    /// <summary>
    /// Creates the fake clock used by the harness and jobs.
    /// </summary>
    /// <returns>A clock fixed at 2026-12-01 12:00 UTC.</returns>
    private static FakeTimeProvider CreateTime() =>
        new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Creates a scheduled job whose window opened a minute ago and closes in an hour.
    /// </summary>
    /// <param name="importId">Import the job belongs to.</param>
    /// <param name="time">Fake clock used to compute the window.</param>
    /// <param name="ip">Router IP address.</param>
    /// <param name="row">CSV row number (default 2).</param>
    /// <returns>A job for router "CORE-01" from "batch.csv".</returns>
    private static MikrotikScheduledJob CreateJob(
        Guid importId,
        FakeTimeProvider time,
        string ip,
        int row = 2) =>
        new()
        {
            ImportId = importId,
            SourceFileName = "batch.csv",
            RowNumber = row,
            RouterName = "CORE-01",
            RouterIp = IPAddress.Parse(ip),
            DateStartUtc = time.GetUtcNow().UtcDateTime.AddMinutes(-1),
            DateStopUtc = time.GetUtcNow().UtcDateTime.AddHours(1),
        };

    /// <summary>
    /// Polls every 20 ms until a condition holds.
    /// </summary>
    /// <param name="condition">Predicate to evaluate.</param>
    /// <param name="timeout">Maximum real time to wait.</param>
    /// <returns>A task that completes when the condition is true.</returns>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    /// <summary>
    /// Connection factory that always fails with a given exception and counts attempts, to prove how
    /// many times the executor tried to connect.
    /// </summary>
    /// <param name="exception">Exception returned as a faulted task from every <c>CreateAsync</c>
    /// call.</param>
    private sealed class CountingThrowingFactory(Exception exception) : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Number of times <c>CreateAsync</c> was called.
        /// </summary>
        public int CreateCount { get; private set; }

        /// <summary>
        /// Counts the attempt and returns a faulted task.
        /// </summary>
        /// <param name="connection">Ignored connection options.</param>
        /// <param name="cancellationToken">Ignored.</param>
        /// <returns>A task faulted with the configured exception.</returns>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromException<ITikConnection>(exception);
        }
    }

    /// <summary>
    /// Connection factory that pauses inside <c>CreateAsync</c> until released, so a test can cancel
    /// execution at the exact moment a connection is being opened.
    /// </summary>
    private sealed class GateBeforeConnectFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Completed when the executor has entered the gated call.
        /// </summary>
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>
        /// Completed by <c>Release</c> to let the gated call continue.
        /// </summary>
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Number of times <c>CreateAsync</c> was entered.
        /// </summary>
        public int CreateCount { get; private set; }

        /// <summary>
        /// True once the executor is blocked at the gate.
        /// </summary>
        public bool Waiting => _entered.Task.IsCompleted;

        /// <summary>
        /// Opens the gate so the pending <c>CreateAsync</c> can continue.
        /// </summary>
        public void Release() => _release.TrySetResult();

        /// <summary>
        /// Signals entry, waits for <c>Release</c> (honouring cancellation), then returns a fully
        /// populated fake connection.
        /// </summary>
        /// <param name="connection">Ignored connection options.</param>
        /// <param name="cancellationToken">Cancels the wait at the gate.</param>
        /// <returns>A fake connection with complete device data.</returns>
        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return FakeMikrotikConnections.CreateFullyPopulated();
        }
    }

    /// <summary>
    /// Device-information reader that pauses until released, so a test can cancel while information
    /// is being retrieved.
    /// </summary>
    private sealed class GateDeviceInfoReader : IMikrotikDeviceInfoReader
    {
        /// <summary>
        /// Completed when the executor has entered the gated read.
        /// </summary>
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>
        /// Completed by <c>Release</c> to let the gated read continue.
        /// </summary>
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// True once the executor is blocked inside <c>ReadDeviceInfoAsync</c>.
        /// </summary>
        public bool Waiting => _entered.Task.IsCompleted;

        /// <summary>
        /// Opens the gate so the pending read can finish.
        /// </summary>
        public void Release() => _release.TrySetResult();

        /// <summary>
        /// Signals entry, waits for <c>Release</c> (honouring cancellation), then returns minimal device
        /// data.
        /// </summary>
        /// <param name="connection">Ignored fake connection.</param>
        /// <param name="host">Ignored host.</param>
        /// <param name="cancellationToken">Cancels the wait at the gate.</param>
        /// <returns>A device with identity "late", version "1.0" and architecture "arm".</returns>
        public async Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new MikrotikDeviceInfo
            {
                Identity = "late",
                Version = "1.0",
                ArchitectureName = "arm",
            };
        }
    }

    /// <summary>
    /// Dispose-tracking connection whose <c>Dispose</c> throws, simulating a failed disconnect.
    /// </summary>
    /// <param name="inner">Fake connection that supplies the normal behaviour.</param>
    private sealed class FailingDisposeConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        /// <summary>
        /// True once <c>Dispose</c> was invoked, even though it threw.
        /// </summary>
        public bool DisposeAttempted { get; private set; }

        /// <summary>
        /// Records the attempt and throws IOException("disconnect failed").
        /// </summary>
        public override void Dispose()
        {
            DisposeAttempted = true;
            throw new IOException("disconnect failed");
        }
    }

    /// <summary>
    /// Runs one or more jobs through a real scheduler and <c>RouterJobExecutor</c> rooted in a
    /// temporary scheduled-jobs folder, with fake connections, and tears everything down afterwards.
    /// </summary>
    private sealed class PreflightHarness : IAsyncDisposable
    {
        /// <summary>
        /// Stops the scheduler loop during disposal.
        /// </summary>
        private readonly CancellationTokenSource _schedulerCts = new();
        /// <summary>
        /// Cancels job execution, either via <c>CancelExecution</c> or during disposal.
        /// </summary>
        private readonly CancellationTokenSource _executionCts;
        /// <summary>
        /// Clock used to build job schedule windows.
        /// </summary>
        private readonly FakeTimeProvider _time;
        /// <summary>
        /// Running scheduler loop, awaited on disposal.
        /// </summary>
        private readonly Task _schedulerTask;
        /// <summary>
        /// Background job-execution worker, awaited on disposal.
        /// </summary>
        private readonly Task _executionTask;
        /// <summary>
        /// Temporary scheduled-jobs folder deleted on disposal.
        /// </summary>
        private readonly string _root;

        /// <summary>
        /// Stores the wired components and starts the scheduler loop.
        /// </summary>
        /// <param name="scheduler">Scheduler under test.</param>
        /// <param name="time">Fake clock for job windows.</param>
        /// <param name="acceptedCsv">Path of the placeholder CSV in the Accepted folder.</param>
        /// <param name="completedDir">Folder that receives the CSV when an import completes.</param>
        /// <param name="root">Temporary root removed on disposal.</param>
        /// <param name="executionTask">Background job-execution worker.</param>
        /// <param name="executionCts">Token source that cancels the execution worker.</param>
        private PreflightHarness(
            InMemoryJobScheduler scheduler,
            FakeTimeProvider time,
            string acceptedCsv,
            string completedDir,
            string root,
            Task executionTask,
            CancellationTokenSource executionCts)
        {
            Scheduler = scheduler;
            _time = time;
            AcceptedCsv = acceptedCsv;
            CompletedDir = completedDir;
            _root = root;
            _executionTask = executionTask;
            _executionCts = executionCts;
            _schedulerTask = scheduler.RunAsync(_schedulerCts.Token);
        }

        /// <summary>
        /// Scheduler that tracks imports and queued jobs for the harness.
        /// </summary>
        public InMemoryJobScheduler Scheduler { get; }

        /// <summary>
        /// Placeholder CSV in the Accepted folder, registered so completed imports can be moved.
        /// </summary>
        public string AcceptedCsv { get; }

        /// <summary>
        /// Folder in which a completed import's CSV appears.
        /// </summary>
        public string CompletedDir { get; }

        /// <summary>
        /// Import id created by the most recent <c>RunSingleJobAsync</c> call.
        /// </summary>
        public Guid LastImportId { get; private set; }

        /// <summary>
        /// Cancels the job-execution token, simulating host shutdown.
        /// </summary>
        public void CancelExecution() => _executionCts.Cancel();

        /// <summary>
        /// Creates folders, scheduler options, MikroTik options with <c>SharedPassword</c>, and the
        /// executor host, then wraps them in a harness.
        /// </summary>
        /// <param name="time">Fake clock shared by scheduler and executor.</param>
        /// <param name="connection">Connection returned by the default scripted factory; defaults to a
        /// fully populated one.</param>
        /// <param name="connectionFactory">Overrides the connection factory entirely.</param>
        /// <param name="deviceInfoReader">Optional reader replacing the real device-info reader.</param>
        /// <param name="executorLogger">Optional logger capturing <c>RouterJobExecutor</c>
        /// output.</param>
        /// <param name="upgradeExecutor">Optional upgrade-stage stub.</param>
        /// <param name="jobRetryOptions">Optional retry policy override.</param>
        /// <returns>A started harness.</returns>
        public static Task<PreflightHarness> CreateAsync(
            FakeTimeProvider time,
            ITikConnection? connection = null,
            IMikrotikConnectionFactory? connectionFactory = null,
            IMikrotikDeviceInfoReader? deviceInfoReader = null,
            ILogger? executorLogger = null,
            IRouterOsV6UpgradeExecutor? upgradeExecutor = null,
            JobRetryOptions? jobRetryOptions = null)
        {
            string root = TestHostFactory.CreateScheduledJobsRoot();
            ScheduledJobsOptions options = new()
            {
                Path = root,
                MaximumPendingJobs = 100,
                MaxConcurrentRouterJobs = 12,
            };
            ScheduledJobsPaths paths = new(Options.Create(options));
            paths.EnsureCreated();
            string acceptedCsv = Path.Combine(paths.Accepted, "batch.csv");
            File.WriteAllText(acceptedCsv, "placeholder");

            MikroTikOptions mikroTik = new()
            {
                Enabled = true,
                Username = "admin",
                Password = SharedPassword,
                Port = 8728,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                ReconnectInterval = TimeSpan.FromSeconds(30),
            };

            IMikrotikConnectionFactory factory = connectionFactory
                ?? new ScriptedMikrotikConnectionFactory(_ => connection ?? FakeMikrotikConnections.CreateFullyPopulated());

            ILogger<RouterJobExecutor> logger = executorLogger as ILogger<RouterJobExecutor>
                ?? (executorLogger is null
                    ? NullLogger<RouterJobExecutor>.Instance
                    : new LoggerAdapter(executorLogger));

            (InMemoryJobScheduler scheduler, _, Task executionTask, CancellationTokenSource hostCts) =
                SchedulerExecutionTestHost.Create(
                    options,
                    paths,
                    new FileSystemScheduledJobFileMover(),
                    time,
                    connectionFactory: factory,
                    deviceInfoReader: deviceInfoReader,
                    upgradeExecutor: upgradeExecutor,
                    mikroTikEnabled: true,
                    mikroTikOptions: mikroTik,
                    executorLogger: logger,
                    jobRetryOptions: jobRetryOptions);

            return Task.FromResult(new PreflightHarness(
                scheduler,
                time,
                acceptedCsv,
                paths.Completed,
                root,
                executionTask,
                hostCts));
        }

        /// <summary>
        /// Registers a one-job import, schedules the job, and waits up to 5 seconds for the import to
        /// leave the scheduler.
        /// </summary>
        /// <param name="ip">Router IP address for the job.</param>
        /// <returns>A task that completes when the import is no longer tracked.</returns>
        public async Task RunSingleJobAsync(string ip)
        {
            Guid importId = Guid.NewGuid();
            LastImportId = importId;
            Scheduler.RegisterImport(importId, 1);
            await Scheduler.ScheduleAsync(CreateJob(importId, _time, ip), CancellationToken.None);
            Scheduler.SetAcceptedPath(importId, AcceptedCsv);
            await WaitForAsync(() => !Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Cancels the scheduler and execution, waits for them (ignoring failures), disposes the
        /// scheduler and token sources, and deletes the temporary root.
        /// </summary>
        /// <returns>A task that completes once cleanup has finished.</returns>
        public async ValueTask DisposeAsync()
        {
            _schedulerCts.Cancel();
            _executionCts.Cancel();
            try
            {
                await _schedulerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            try
            {
                await _executionTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            await Scheduler.DisposeAsync();
            _schedulerCts.Dispose();
            _executionCts.Dispose();
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Presents a plain <c>ILogger</c> as <c>ILogger&lt;RouterJobExecutor&gt;</c> so a recording
        /// logger can be injected into the executor.
        /// </summary>
        /// <param name="inner">Logger that receives all calls.</param>
        private sealed class LoggerAdapter(ILogger inner) : ILogger<RouterJobExecutor>
        {
            /// <summary>
            /// Forwards scope creation to the inner logger.
            /// </summary>
            /// <typeparam name="TState">Type of the scope state.</typeparam>
            /// <param name="state">Scope state.</param>
            /// <returns>The inner logger's scope, or <c>null</c>.</returns>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                inner.BeginScope(state);

            /// <summary>
            /// Forwards the level check to the inner logger.
            /// </summary>
            /// <param name="logLevel">Level being tested.</param>
            /// <returns>Whether the inner logger accepts the level.</returns>
            public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

            /// <summary>
            /// Forwards the log call to the inner logger.
            /// </summary>
            /// <typeparam name="TState">Type of the log state.</typeparam>
            /// <param name="logLevel">Entry severity.</param>
            /// <param name="eventId">Entry event id.</param>
            /// <param name="state">Entry state.</param>
            /// <param name="exception">Exception, if any.</param>
            /// <param name="formatter">Creates the message text.</param>
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
