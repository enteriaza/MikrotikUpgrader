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

public sealed class JobTransientRetryTests
{
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

    private static MikrotikScheduledJob CreateJob(
        Guid importId,
        int row,
        FakeTimeProvider time,
        string ip) =>
        CreateJobAt(importId, row, time, ip, TimeSpan.FromMinutes(-1), "R" + row);

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

    private sealed class CountingFailFactory(Exception exception) : IMikrotikConnectionFactory
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            throw exception;
        }
    }

    private sealed class FailThenSucceedFactory(int failuresBeforeSuccess, Exception exception)
        : IMikrotikConnectionFactory
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

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

    private sealed class SelectiveHostFactory(string failHost) : IMikrotikConnectionFactory
    {
        public ConcurrentBag<string> SuccessHosts { get; } = [];

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

    private sealed class OrderedGateFactory : IMikrotikConnectionFactory
    {
        private readonly object _gate = new();
        private readonly ConcurrentQueue<string> _order = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _releases = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> DispatchOrder => _order.ToArray();

        public ICollection<string> ActiveHosts => _active.Keys;

        public int ActiveCount => _active.Count;

        public void ReleaseHost(string host)
        {
            if (_releases.TryGetValue(host, out TaskCompletionSource? tcs))
            {
                tcs.TrySetResult();
            }
        }

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

    private sealed class CancelOnConnectFactory : IMikrotikConnectionFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Entered => _entered.Task.IsCompleted;

        public bool Completed { get; private set; }

        public void Release() => _release.TrySetResult();

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

    private sealed class RetryHarness : IAsyncDisposable
    {
        private readonly ExecutionHarnessParts _parts;

        private RetryHarness(ExecutionHarnessParts parts) => _parts = parts;

        public InMemoryJobScheduler Scheduler => _parts.Scheduler;

        public string AcceptedCsv => _parts.AcceptedCsv;

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

        public async Task<MikrotikScheduledJob> ScheduleSingleAsync(string ip = "10.0.0.50")
        {
            Guid importId = Guid.NewGuid();
            Scheduler.RegisterImport(importId, 1);
            MikrotikScheduledJob job = CreateJob(importId, 2, (FakeTimeProvider)_parts.Time, ip);
            await Scheduler.ScheduleAsync(job, CancellationToken.None);
            Scheduler.SetAcceptedPath(importId, AcceptedCsv);
            return job;
        }

        public ValueTask DisposeAsync() => _parts.DisposeAsync();
    }

    private sealed class ExecutionHarnessParts : IAsyncDisposable
    {
        private readonly CancellationTokenSource _schedulerCts = new();
        private readonly Task _schedulerTask;
        private readonly Task _executionTask;
        private readonly string _root;

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

        public InMemoryJobScheduler Scheduler { get; }

        public TimeProvider Time { get; }

        public string AcceptedCsv { get; }

        public CancellationTokenSource ExecutionCts { get; }

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
