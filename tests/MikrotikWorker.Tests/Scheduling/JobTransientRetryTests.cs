// -----------------------------------------------------------------------------
// File: JobTransientRetryTests.cs
//
// Description:
// JobRetry behaviour for connection and authentication failures: independent
// counters, delay eligibility, worker slot release, reset on success,
// permanent failures, cancellation not consuming attempts, and ordered
// dispatch after delayed retries.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Scheduling;

/// <summary>
/// Covers transient connect/login retry counters, delays, and terminal exhaustion.
/// </summary>
public sealed class JobTransientRetryTests
{
    /// <summary>
    /// TimeoutException on connect.
    /// Expects ConnectionFailureCount=1, NextRetryUtc ≈ now+10m, queued not dispatched.
    /// </summary>
    [Fact]
    public async Task ConnectionTimeout_FirstFailure_RequeuesAboutTenMinutesLater()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new TimeoutException("connect timed out"));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => job.ConnectionFailureCount == 1, TimeSpan.FromSeconds(5));

        Assert.Equal(0, job.AuthenticationFailureCount);
        Assert.Equal(JobRetryFailureCategory.Connection, job.LastRetryFailureCategory);
        Assert.NotNull(job.NextRetryUtc);
        Assert.Equal(time.GetUtcNow().AddMinutes(10), job.NextRetryUtc);
        Assert.Equal(1, harness.Scheduler.QueuedCount);
        Assert.Equal(0, harness.Scheduler.DispatchedCount);
        Assert.Equal(1, factory.Attempts);
    }

    /// <summary>
    /// IOException with MaximumUnsuccessfulAttempts=10.
    /// Expects ten requeues then eleventh failure untracks the import with no NextRetryUtc.
    /// </summary>
    [Fact]
    public async Task ConnectionFailures_AttemptsOneThroughTen_AreRetryable_EleventhIsTerminal()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new IOException("unreachable"));
        await using RetryHarness harness = await RetryHarness.CreateAsync(
            time,
            factory,
            new JobRetryOptions
            {
                ConnectionRetryDelay = TimeSpan.FromMinutes(10),
                MaximumUnsuccessfulAttempts = 10,
            });

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();

        for (int attempt = 1; attempt <= 10; attempt++)
        {
            await WaitForAsync(() => job.ConnectionFailureCount == attempt, TimeSpan.FromSeconds(5));
            Assert.Equal(1, harness.Scheduler.QueuedCount);
            Assert.True(harness.Scheduler.IsTracked(job.ImportId));

            time.Advance(TimeSpan.FromMinutes(10));
            harness.Scheduler.NudgeWake();
        }

        await WaitForAsync(() => job.ConnectionFailureCount == 11, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => !harness.Scheduler.IsTracked(job.ImportId), TimeSpan.FromSeconds(5));
        Assert.Null(job.NextRetryUtc);
        Assert.Equal(11, factory.Attempts);
    }

    /// <summary>
    /// After first failure, advance 9m (no retry) then +1m.
    /// Expects attempt count unchanged until full delay elapses.
    /// </summary>
    [Fact]
    public async Task DelayedJob_IsNotDispatchedBeforeRetryTime_AndBecomesEligibleAfter()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new IOException("down"));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => job.ConnectionFailureCount == 1, TimeSpan.FromSeconds(5));
        int attemptsAfterFirst = factory.Attempts;

        time.Advance(TimeSpan.FromMinutes(9));
        harness.Scheduler.NudgeWake();
        harness.Scheduler.ProcessDueJobsForTests();
        await Task.Delay(50);
        Assert.Equal(attemptsAfterFirst, factory.Attempts);
        Assert.Equal(1, harness.Scheduler.QueuedCount);

        time.Advance(TimeSpan.FromMinutes(1));
        harness.Scheduler.NudgeWake();
        // Wait for the retry handler to finish recording the failure (CreateAsync may
        // increment Attempts before ConnectionFailureCount is updated).
        await WaitForAsync(
            () => factory.Attempts > attemptsAfterFirst && job.ConnectionFailureCount == 2,
            TimeSpan.FromSeconds(5));
        Assert.Equal(2, job.ConnectionFailureCount);
    }

    /// <summary>
    /// One host always fails connect; peer succeeds.
    /// Expects failing job requeued and peer host recorded successful while import tracked.
    /// </summary>
    [Fact]
    public async Task OtherDevicesContinue_WhileOneWaitsForRetry_AndWorkerSlotIsReleased()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        SelectiveHostFactory factory = new(failHost: "10.0.0.1");
        await using RetryHarness harness = await RetryHarness.CreateAsync(
            time,
            factory,
            maxConcurrentRouterJobs: 12);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        MikrotikScheduledJob delayed = CreateJob(importId, 2, time, "10.0.0.1");
        MikrotikScheduledJob other = CreateJob(importId, 3, time, "10.0.0.2");
        await harness.Scheduler.ScheduleAsync(delayed, CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(other, CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => delayed.ConnectionFailureCount == 1, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => factory.SuccessHosts.Contains("10.0.0.2"), TimeSpan.FromSeconds(5));
        Assert.Equal(1, harness.Scheduler.QueuedCount);
        Assert.True(harness.Scheduler.IsTracked(importId));
    }

    /// <summary>
    /// Fail twice then succeed (tik4net atomic connect+login).
    /// Expects both counters cleared and pending drained after the successful attempt.
    /// </summary>
    [Fact]
    public async Task SuccessfulAuthenticatedConnect_ResetsBothRetryCounters()
    {
        // tik4net CreateAsync performs TCP connect and login atomically; success clears both counters.
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        FailThenSucceedFactory factory = new(failuresBeforeSuccess: 2, new IOException("flaky"));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => job.ConnectionFailureCount == 1, TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromMinutes(10));
        harness.Scheduler.NudgeWake();
        await WaitForAsync(() => job.ConnectionFailureCount == 2, TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromMinutes(10));
        harness.Scheduler.NudgeWake();
        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(5));

        Assert.Equal(0, job.ConnectionFailureCount);
        Assert.Equal(0, job.AuthenticationFailureCount);
        Assert.Null(job.NextRetryUtc);
    }

    /// <summary>
    /// TikConnectionLoginRefusedException.
    /// Expects auth counter incremented, connection counter untouched, retry scheduled.
    /// </summary>
    [Fact]
    public async Task AuthenticationRejection_Requeues_AndDoesNotConsumeConnectionAttempts()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new TikConnectionLoginRefusedException("invalid user name or password", "api"));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => job.AuthenticationFailureCount == 1, TimeSpan.FromSeconds(5));

        Assert.Equal(0, job.ConnectionFailureCount);
        Assert.Equal(JobRetryFailureCategory.Authentication, job.LastRetryFailureCategory);
        Assert.Equal(time.GetUtcNow().AddMinutes(10), job.NextRetryUtc);
    }

    /// <summary>
    /// Ten auth retries then eleventh.
    /// Expects import untracked after exhausting MaximumUnsuccessfulAttempts.
    /// </summary>
    [Fact]
    public async Task AuthenticationFailures_EleventhAttempt_IsTerminal()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new TikConnectionLoginRefusedException("denied", "api"));
        await using RetryHarness harness = await RetryHarness.CreateAsync(
            time,
            factory,
            new JobRetryOptions
            {
                AuthenticationRetryDelay = TimeSpan.FromMinutes(10),
                MaximumUnsuccessfulAttempts = 10,
            });

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            await WaitForAsync(() => job.AuthenticationFailureCount == attempt, TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromMinutes(10));
            harness.Scheduler.NudgeWake();
        }

        await WaitForAsync(() => job.AuthenticationFailureCount == 11, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => !harness.Scheduler.IsTracked(job.ImportId), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Pre-seeded auth count 4 then SocketException connect failure.
    /// Expects auth count unchanged when connection fails.
    /// </summary>
    [Fact]
    public async Task ConnectionFailure_DoesNotConsumeAuthenticationAttempts()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new SocketException(10060));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        job.AuthenticationFailureCount = 4;
        await WaitForAsync(() => job.ConnectionFailureCount == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(4, job.AuthenticationFailureCount);
    }

    /// <summary>
    /// InvalidOperationException from factory.
    /// Expects single attempt, counters 0, import untracked immediately.
    /// </summary>
    [Fact]
    public async Task PermanentInvalidOperation_IsNotRetried()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new InvalidOperationException("programming defect"));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => !harness.Scheduler.IsTracked(job.ImportId), TimeSpan.FromSeconds(5));
        Assert.Equal(0, job.ConnectionFailureCount);
        Assert.Equal(0, job.AuthenticationFailureCount);
        Assert.Equal(1, factory.Attempts);
    }

    /// <summary>
    /// AggregateException of Timeout + OperationCanceledException.
    /// Expects no retry counters/NextRetryUtc; treated as cancellation/terminal without retry budget use.
    /// </summary>
    [Fact]
    public async Task AggregateTimeoutPlusCancellation_DoesNotIncrementCounters_OrScheduleRetry()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        using CancellationTokenSource cts = new();
        cts.Cancel();
        CountingFailFactory factory = new(new AggregateException(
            new TimeoutException("connect timed out"),
            new OperationCanceledException(cts.Token)));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => !harness.Scheduler.IsTracked(job.ImportId), TimeSpan.FromSeconds(5));

        Assert.Equal(0, job.ConnectionFailureCount);
        Assert.Equal(0, job.AuthenticationFailureCount);
        Assert.Null(job.NextRetryUtc);
        Assert.Equal(1, factory.Attempts);
    }

    /// <summary>
    /// AggregateException of Timeout + InvalidOperationException.
    /// Expects terminal without incrementing retry counters.
    /// </summary>
    [Fact]
    public async Task AggregateTimeoutPlusPermanent_DoesNotConsumeRetryAttempts()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new AggregateException(
            new TimeoutException("connect timed out"),
            new InvalidOperationException("programming defect")));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => !harness.Scheduler.IsTracked(job.ImportId), TimeSpan.FromSeconds(5));

        Assert.Equal(0, job.ConnectionFailureCount);
        Assert.Equal(0, job.AuthenticationFailureCount);
        Assert.Null(job.NextRetryUtc);
        Assert.Equal(1, factory.Attempts);
    }

    /// <summary>
    /// Enqueue A@+10m, B@+1m, C@+3m with gated factory.
    /// Expects dispatch order B, C, A as each becomes due — no HOL blocking by A.
    /// </summary>
    [Fact]
    public async Task DelayedEligibility_AbcOrdering_DispatchesInEligibleTimeOrder()
    {
        // At 10:00 schedule A@10:10, B@10:01, C@10:03. Prove B then C then A — no HOL blocking.
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        OrderedGateFactory factory = new();
        await using RetryHarness harness = await RetryHarness.CreateAsync(
            time,
            factory,
            maxConcurrentRouterJobs: 12);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 3);
        MikrotikScheduledJob jobA = CreateJobAt(importId, 2, time, "10.0.0.10", TimeSpan.FromMinutes(10), "A");
        MikrotikScheduledJob jobB = CreateJobAt(importId, 3, time, "10.0.0.11", TimeSpan.FromMinutes(1), "B");
        MikrotikScheduledJob jobC = CreateJobAt(importId, 4, time, "10.0.0.12", TimeSpan.FromMinutes(3), "C");

        // Enqueue A first so a naive FIFO would incorrectly prefer A if HOL blocking existed.
        await harness.Scheduler.ScheduleAsync(jobA, CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(jobB, CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(jobC, CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        harness.Scheduler.ProcessDueJobsForTests();
        await Task.Delay(30);
        Assert.Empty(factory.DispatchOrder);
        Assert.Equal(3, harness.Scheduler.QueuedCount);
        Assert.Equal(0, harness.Scheduler.DispatchedCount);

        time.Advance(TimeSpan.FromMinutes(1)); // 10:01
        harness.Scheduler.NudgeWake();
        await WaitForAsync(() => factory.ActiveHosts.Contains("10.0.0.11"), TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "10.0.0.11" }, factory.DispatchOrder.ToArray());
        Assert.DoesNotContain("10.0.0.10", factory.DispatchOrder);
        Assert.DoesNotContain("10.0.0.12", factory.DispatchOrder);
        Assert.Equal(2, harness.Scheduler.QueuedCount);
        Assert.Equal(1, factory.ActiveCount);

        factory.ReleaseHost("10.0.0.11");
        await WaitForAsync(() => factory.ActiveCount == 0, TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromMinutes(2)); // 10:03
        harness.Scheduler.NudgeWake();
        await WaitForAsync(() => factory.ActiveHosts.Contains("10.0.0.12"), TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "10.0.0.11", "10.0.0.12" }, factory.DispatchOrder.ToArray());
        Assert.DoesNotContain("10.0.0.10", factory.DispatchOrder);
        Assert.Equal(1, harness.Scheduler.QueuedCount);

        factory.ReleaseHost("10.0.0.12");
        await WaitForAsync(() => factory.ActiveCount == 0, TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromMinutes(7)); // 10:10
        harness.Scheduler.NudgeWake();
        await WaitForAsync(() => factory.ActiveHosts.Contains("10.0.0.10"), TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "10.0.0.11", "10.0.0.12", "10.0.0.10" }, factory.DispatchOrder.ToArray());
        Assert.Equal(0, harness.Scheduler.QueuedCount);

        factory.ReleaseHost("10.0.0.10");
        await WaitForAsync(() => !harness.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.DispatchOrder.Count(static h => h == "10.0.0.10"));
        Assert.Equal(1, factory.DispatchOrder.Count(static h => h == "10.0.0.11"));
        Assert.Equal(1, factory.DispatchOrder.Count(static h => h == "10.0.0.12"));
    }

    /// <summary>
    /// After first connection failure.
    /// Expects same JobId and a single queued/pending entry.
    /// </summary>
    [Fact]
    public async Task RetryPreservesJobIdentity_AndDoesNotDuplicateQueueEntries()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new IOException("down"));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        string jobId = job.JobId;
        await WaitForAsync(() => job.ConnectionFailureCount == 1, TimeSpan.FromSeconds(5));

        Assert.Equal(jobId, job.JobId);
        Assert.Equal(1, harness.Scheduler.QueuedCount);
        Assert.Equal(1, harness.Scheduler.PendingCount);
    }

    /// <summary>
    /// Cancel execution while CreateAsync is gated mid-connect.
    /// Expects connection/auth counters remain 0.
    /// </summary>
    [Fact]
    public async Task CancellationDuringConnect_DoesNotIncrementCounters()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CancelOnConnectFactory factory = new();
        ExecutionHarnessParts parts = await ExecutionHarnessParts.CreateAsync(
            time,
            factory,
            new JobRetryOptions { MaximumUnsuccessfulAttempts = 10 },
            maxConcurrentRouterJobs: 12);

        Guid importId = Guid.NewGuid();
        parts.Scheduler.RegisterImport(importId, 1);
        MikrotikScheduledJob job = CreateJob(importId, 2, time, "10.9.9.9");
        await parts.Scheduler.ScheduleAsync(job, CancellationToken.None);
        parts.Scheduler.SetAcceptedPath(importId, parts.AcceptedCsv);

        await WaitForAsync(() => factory.Entered, TimeSpan.FromSeconds(5));
        await parts.ExecutionCts.CancelAsync();
        factory.Release();

        await WaitForAsync(() => parts.Scheduler.PendingCount == 0 || factory.Completed, TimeSpan.FromSeconds(5));
        Assert.Equal(0, job.ConnectionFailureCount);
        Assert.Equal(0, job.AuthenticationFailureCount);

        await parts.DisposeAsync();
    }

    /// <summary>
    /// Dispose after first failure then advance past retry time.
    /// Expects no further factory attempts and pending 0.
    /// </summary>
    [Fact]
    public async Task Shutdown_DiscardsDelayedRetry_WithoutDispatch()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        CountingFailFactory factory = new(new IOException("down"));
        RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => job.ConnectionFailureCount == 1, TimeSpan.FromSeconds(5));
        int attempts = factory.Attempts;

        await harness.DisposeAsync();
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(attempts, factory.Attempts);
        Assert.Equal(0, harness.Scheduler.PendingCount);
    }

    /// <summary>
    /// One IOException then success.
    /// Expects two attempts, counters cleared, import completed/untracked.
    /// </summary>
    [Fact]
    public async Task SuccessfulRetry_ProceedsThroughUpgradeWorkflowOnce()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        FailThenSucceedFactory factory = new(failuresBeforeSuccess: 1, new IOException("once"));
        await using RetryHarness harness = await RetryHarness.CreateAsync(time, factory);

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => job.ConnectionFailureCount == 1, TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromMinutes(10));
        harness.Scheduler.NudgeWake();
        await WaitForAsync(() => !harness.Scheduler.IsTracked(job.ImportId), TimeSpan.FromSeconds(5));
        Assert.Equal(2, factory.Attempts);
        Assert.Equal(0, job.ConnectionFailureCount);
    }

    /// <summary>
    /// Due job (start −1m) for the given import/row/IP.
    /// </summary>
    private static MikrotikScheduledJob CreateJob(
        Guid importId,
        int row,
        FakeTimeProvider time,
        string ip) =>
        CreateJobAt(importId, row, time, ip, TimeSpan.FromMinutes(-1), "R" + row);

    /// <summary>
    /// Job with configurable start offset from FakeTimeProvider now.
    /// </summary>
    private static MikrotikScheduledJob CreateJobAt(
        Guid importId,
        int row,
        FakeTimeProvider time,
        string ip,
        TimeSpan startOffsetFromNow,
        string routerName) =>
        new()
        {
            ImportId = importId,
            SourceFileName = "batch.csv",
            RowNumber = row,
            RouterName = routerName,
            RouterIp = IPAddress.Parse(ip),
            DateStartUtc = time.GetUtcNow().UtcDateTime.Add(startOffsetFromNow),
            DateStopUtc = time.GetUtcNow().UtcDateTime.AddHours(6),
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

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    /// <summary>
    /// Always throws the supplied exception; counts CreateAsync attempts.
    /// </summary>
    /// <param name="exception">Exception thrown on every connection attempt.</param>
    private sealed class CountingFailFactory(Exception exception) : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Number of <see cref="CreateAsync"/> calls observed so far.
        /// </summary>
        private int _attempts;

        /// <summary>
        /// Number of connect attempts made against this factory. Every attempt throws, so the value equals the number
        /// of tries the executor made. Read with <c>Volatile.Read</c> because attempts arrive from worker threads.
        /// </summary>
        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>
        /// Counts the attempt and then throws the configured exception instead of connecting.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <returns>Never returns; always throws.</returns>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            throw exception;
        }
    }

    /// <summary>
    /// Throws N times then returns a populated fake connection.
    /// </summary>
    /// <param name="failuresBeforeSuccess">Number of initial attempts that fail before one succeeds.</param>
    /// <param name="exception">Exception thrown for each failing attempt.</param>
    private sealed class FailThenSucceedFactory(int failuresBeforeSuccess, Exception exception)
        : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Number of <see cref="CreateAsync"/> calls observed so far.
        /// </summary>
        private int _attempts;

        /// <summary>
        /// Number of connect attempts made against this factory, counting the initial failures and the final success.
        /// Read with <c>Volatile.Read</c> because attempts arrive from worker threads.
        /// </summary>
        public int Attempts => Volatile.Read(ref _attempts);

        /// <summary>
        /// Throws the configured exception for the first N attempts, then returns a fully populated fake connection.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <returns>A populated fake connection once the failure budget is exhausted.</returns>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= failuresBeforeSuccess)
            {
                throw exception;
            }

            return Task.FromResult<ITikConnection>(FakeMikrotikConnections.CreateFullyPopulated());
        }
    }

    /// <summary>
    /// Fails one host; records successful peer hosts.
    /// </summary>
    /// <param name="failHost">Host address whose connection attempts always fail.</param>
    private sealed class SelectiveHostFactory(string failHost) : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Hosts for which a connection was created successfully; the configured failing host never appears, so tests
        /// can show the other routers still ran.
        /// </summary>
        public ConcurrentBag<string> SuccessHosts { get; } = [];

        /// <summary>
        /// Throws <see cref="IOException"/> for the configured failing host; otherwise records the host and returns a fake connection.
        /// </summary>
        /// <param name="connection">Connection options identifying the target host.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <returns>A populated fake connection for hosts other than the failing one.</returns>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            if (string.Equals(connection.Host, failHost, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("host down");
            }

            SuccessHosts.Add(connection.Host);
            return Task.FromResult<ITikConnection>(FakeMikrotikConnections.CreateFullyPopulated());
        }
    }

    /// <summary>
    /// Records dispatch order and holds connects until ReleaseHost.
    /// </summary>
    private sealed class OrderedGateFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Serializes order recording and release/active registration for a connect attempt.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// Hosts in the order their connect attempts began.
        /// </summary>
        private readonly ConcurrentQueue<string> _order = new();

        /// <summary>
        /// Per-host gates that hold a connect attempt until <see cref="ReleaseHost"/> is called.
        /// </summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _releases = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Hosts whose connect attempt is currently blocked at the gate.
        /// </summary>
        private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets a snapshot of hosts in the order their connect attempts were dispatched.
        /// </summary>
        public IReadOnlyCollection<string> DispatchOrder => _order.ToArray();

        /// <summary>
        /// Hosts whose connect attempts are currently blocked at the gate, waiting for <see cref="ReleaseHost"/>.
        /// </summary>
        public ICollection<string> ActiveHosts => _active.Keys;

        /// <summary>
        /// Number of connect attempts currently blocked at the gate, so tests can tell when the expected attempts
        /// have arrived.
        /// </summary>
        public int ActiveCount => _active.Count;

        /// <summary>
        /// Opens the gate for a host so its pending connect attempt completes.
        /// </summary>
        /// <param name="host">Host whose connect attempt should be released.</param>
        public void ReleaseHost(string host)
        {
            if (_releases.TryGetValue(host, out TaskCompletionSource? tcs))
            {
                tcs.TrySetResult();
            }
        }

        /// <summary>
        /// Records the host, then blocks until <see cref="ReleaseHost"/> is called for it or the token is cancelled.
        /// </summary>
        /// <param name="connection">Connection options identifying the target host.</param>
        /// <param name="cancellationToken">Token that aborts the wait at the gate.</param>
        /// <returns>A populated fake connection after the gate opens.</returns>
        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _order.Enqueue(connection.Host);
                _releases[connection.Host] = release;
                _active[connection.Host] = 0;
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return (ITikConnection)FakeMikrotikConnections.CreateFullyPopulated();
            }
            finally
            {
                _active.TryRemove(connection.Host, out _);
            }
        }
    }

    /// <summary>
    /// Blocks in CreateAsync until Release; exposes Entered/Completed.
    /// </summary>
    private sealed class CancelOnConnectFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Completed once the first connect attempt has entered <see cref="CreateAsync"/>.
        /// </summary>
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gate that holds the connect attempt until <see cref="Release"/> is called.
        /// </summary>
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets a value indicating whether a connect attempt has reached the gate.
        /// </summary>
        public bool Entered => _entered.Task.IsCompleted;

        /// <summary>
        /// Gets a value indicating whether the connect attempt has finished (normally, cancelled, or faulted).
        /// </summary>
        public bool Completed { get; private set; }

        /// <summary>
        /// Opens the gate so the pending connect attempt can proceed.
        /// </summary>
        public void Release() => _release.TrySetResult();

        /// <summary>
        /// Signals entry, waits for <see cref="Release"/> or cancellation, and marks the attempt completed.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router (ignored).</param>
        /// <param name="cancellationToken">Token whose cancellation aborts the wait with <see cref="OperationCanceledException"/>.</param>
        /// <returns>A populated fake connection when released without cancellation.</returns>
        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return FakeMikrotikConnections.CreateFullyPopulated();
            }
            finally
            {
                Completed = true;
            }
        }
    }

    /// <summary>
    /// Full scheduler+workers host with configurable factory, retry options, and
    /// ScheduleSingleAsync helper over a temp Accepted CSV.
    /// </summary>
    private sealed class RetryHarness : IAsyncDisposable
    {
        /// <summary>
        /// Underlying scheduler, workers, and temp folders shared with the cancellation scenarios.
        /// </summary>
        private readonly ExecutionHarnessParts _parts;

        /// <summary>
        /// Wraps an already started set of harness parts.
        /// </summary>
        /// <param name="parts">Started scheduler/worker parts to expose.</param>
        private RetryHarness(ExecutionHarnessParts parts) => _parts = parts;

        /// <summary>
        /// Scheduler under test, owned by the shared execution parts of this harness.
        /// </summary>
        public InMemoryJobScheduler Scheduler => _parts.Scheduler;

        /// <summary>
        /// Path of the placeholder Accepted CSV that scheduled imports are bound to.
        /// </summary>
        public string AcceptedCsv => _parts.AcceptedCsv;

        /// <summary>
        /// Creates the temp jobs tree and starts the scheduler loop plus execution workers with the supplied factory.
        /// </summary>
        /// <param name="time">Fake clock driving scheduler and retry eligibility.</param>
        /// <param name="factory">Connection factory used by the router job executor.</param>
        /// <param name="retryOptions">Retry delays and limits; defaults are used when <see langword="null"/>.</param>
        /// <param name="maxConcurrentRouterJobs">Execution worker/concurrency limit.</param>
        /// <returns>A started harness; dispose it to stop loops and clean up.</returns>
        public static async Task<RetryHarness> CreateAsync(
            FakeTimeProvider time,
            IMikrotikConnectionFactory factory,
            JobRetryOptions? retryOptions = null,
            int maxConcurrentRouterJobs = 12)
        {
            ExecutionHarnessParts parts = await ExecutionHarnessParts.CreateAsync(
                time,
                factory,
                retryOptions ?? new JobRetryOptions(),
                maxConcurrentRouterJobs);
            return new RetryHarness(parts);
        }

        /// <summary>
        /// Registers a one-row import, schedules it, and binds Accepted path.
        /// </summary>
        public async Task<MikrotikScheduledJob> ScheduleSingleAsync(string ip = "10.0.0.50")
        {
            Guid importId = Guid.NewGuid();
            Scheduler.RegisterImport(importId, 1);
            MikrotikScheduledJob job = CreateJob(importId, 2, (FakeTimeProvider)_parts.Time, ip);
            await Scheduler.ScheduleAsync(job, CancellationToken.None);
            Scheduler.SetAcceptedPath(importId, AcceptedCsv);
            return job;
        }

        /// <summary>
        /// Stops the scheduler and workers and removes temporary directories.
        /// </summary>
        /// <returns>A task that completes when disposal has finished.</returns>
        public ValueTask DisposeAsync() => _parts.DisposeAsync();
    }

    /// <summary>
    /// Shared scheduler + execution workers used by retry scenarios.
    /// </summary>
    private sealed class ExecutionHarnessParts : IAsyncDisposable
    {
        /// <summary>
        /// Cancels the scheduler run loop during disposal.
        /// </summary>
        private readonly CancellationTokenSource _schedulerCts = new();

        /// <summary>
        /// Task running the scheduler loop.
        /// </summary>
        private readonly Task _schedulerTask;

        /// <summary>
        /// Task running the router execution workers.
        /// </summary>
        private readonly Task _executionTask;

        /// <summary>
        /// Temporary ScheduledJobs root deleted on disposal.
        /// </summary>
        private readonly string _root;

        /// <summary>
        /// Initializes the parts and starts the scheduler run loop.
        /// </summary>
        /// <param name="scheduler">Scheduler under test.</param>
        /// <param name="time">Clock shared with the scheduler and executor.</param>
        /// <param name="acceptedCsv">Placeholder Accepted CSV path.</param>
        /// <param name="root">Temporary ScheduledJobs root to delete on disposal.</param>
        /// <param name="executionTask">Task running the execution workers.</param>
        /// <param name="executionCts">Cancellation source that stops the execution workers.</param>
        private ExecutionHarnessParts(
            InMemoryJobScheduler scheduler,
            TimeProvider time,
            string acceptedCsv,
            string root,
            Task executionTask,
            CancellationTokenSource executionCts)
        {
            Scheduler = scheduler;
            Time = time;
            AcceptedCsv = acceptedCsv;
            _root = root;
            _executionTask = executionTask;
            ExecutionCts = executionCts;
            _schedulerTask = scheduler.RunAsync(_schedulerCts.Token);
        }

        /// <summary>
        /// Scheduler under test; it is run by the loop and execution workers these parts start.
        /// </summary>
        public InMemoryJobScheduler Scheduler { get; }

        /// <summary>
        /// Clock shared by the scheduler and executor, so retry eligibility and scheduling see the same time.
        /// </summary>
        public TimeProvider Time { get; }

        /// <summary>
        /// Path of the placeholder Accepted CSV that scheduled imports are bound to.
        /// </summary>
        public string AcceptedCsv { get; }

        /// <summary>
        /// Cancellation source that stops the execution workers; cancelling it simulates host shutdown while jobs are
        /// in flight.
        /// </summary>
        public CancellationTokenSource ExecutionCts { get; }

        /// <summary>
        /// Builds the temp ScheduledJobs tree and Accepted CSV, then wires the scheduler and execution workers.
        /// </summary>
        /// <param name="time">Fake clock driving scheduling and retries.</param>
        /// <param name="factory">Connection factory used by the router job executor.</param>
        /// <param name="retryOptions">Retry delays and limits for connect/auth failures.</param>
        /// <param name="maxConcurrentRouterJobs">Maximum concurrent router jobs and worker count.</param>
        /// <returns>The started harness parts.</returns>
        public static Task<ExecutionHarnessParts> CreateAsync(
            FakeTimeProvider time,
            IMikrotikConnectionFactory factory,
            JobRetryOptions retryOptions,
            int maxConcurrentRouterJobs)
        {
            string root = TestHostFactory.CreateScheduledJobsRoot();
            ScheduledJobsOptions options = new()
            {
                Path = root,
                MaximumPendingJobs = 100,
                MaxConcurrentRouterJobs = maxConcurrentRouterJobs,
            };
            ScheduledJobsPaths paths = new(Options.Create(options));
            paths.EnsureCreated();
            string acceptedCsv = Path.Combine(paths.Accepted, "batch.csv");
            File.WriteAllText(acceptedCsv, "placeholder");

            (InMemoryJobScheduler scheduler, _, Task executionTask, CancellationTokenSource executionCts) =
                SchedulerExecutionTestHost.Create(
                    options,
                    paths,
                    new FileSystemScheduledJobFileMover(),
                    time,
                    connectionFactory: factory,
                    mikroTikEnabled: true,
                    executionWorkers: maxConcurrentRouterJobs,
                    jobRetryOptions: retryOptions);

            return Task.FromResult(new ExecutionHarnessParts(
                scheduler,
                time,
                acceptedCsv,
                root,
                executionTask,
                executionCts));
        }

        /// <summary>
        /// Stops background loops and deletes temporary directories.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _schedulerCts.Cancel();
            ExecutionCts.Cancel();
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
            ExecutionCts.Dispose();
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }
    }
}
