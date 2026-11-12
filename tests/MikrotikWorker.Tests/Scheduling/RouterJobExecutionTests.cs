// -----------------------------------------------------------------------------
// File: RouterJobExecutionTests.cs
//
// Description:
// Router execution pool and executor integration: concurrency limits, future
// jobs not blocking eligible ones, success/failure paths, expiry while queued,
// and shutdown not marking unfinished jobs successful.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Scheduling;
/// <summary>
/// Integration tests for router job execution queue, workers, and executor outcomes.
/// </summary>
public sealed class RouterJobExecutionTests
{
    /// <summary>
    /// Default options and validator with MaxConcurrentRouterJobs=11.
    /// Expects default 12 and validation failure below the floor.
    /// </summary>
    [Fact]
    public void MaxConcurrentRouterJobs_DefaultsTo12_AndRejectsBelow()
    {
        ScheduledJobsOptions defaults = new();
        Assert.Equal(12, defaults.MaxConcurrentRouterJobs);

        ScheduledJobsOptionsValidator validator = new();
        Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(
            null,
            new ScheduledJobsOptions { Path = "ScheduledJobs", MaxConcurrentRouterJobs = 11 });
        Assert.True(result.Failed);
    }

    /// <summary>
    /// Future + immediate jobs; recording factory.
    /// Expects one connect for the due job while the future remains queued.
    /// </summary>
    [Fact]
    public async Task FutureJob_RemainsPending_AndDoesNotBlockEligible()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        await using ExecutionHarness harness = await ExecutionHarness.CreateAsync(time);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddHours(2), time.GetUtcNow().UtcDateTime.AddHours(3), "FUTURE"),
            CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 3, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1), "NOW"),
            CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => harness.ConnectionFactory.Connections.Count >= 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, harness.Scheduler.QueuedCount);
        Assert.Contains(harness.ConnectionFactory.Connections, static c => c.Host == "192.168.1.1");
    }

    /// <summary>
    /// Enabled MikroTik path with populated fake connection.
    /// Expects pending cleared, Completed archive present, import untracked.
    /// </summary>
    [Fact]
    public async Task SuccessfulConnectDisconnect_MarksSucceeded_AndCompletesImport()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await using ExecutionHarness harness = await ExecutionHarness.CreateAsync(time, mikroTikEnabled: true);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(5));
        await WaitForAsync(
            () => Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any(),
            TimeSpan.FromSeconds(5));
        Assert.True(harness.ConnectionFactory.Connections.Count >= 1);
        Assert.False(harness.Scheduler.IsTracked(importId));
    }

    /// <summary>
    /// Selective factory fails one host, succeeds another.
    /// Expects connection retry scheduled for the failing job while the peer succeeds
    /// and the import stays tracked.
    /// </summary>
    [Fact]
    public async Task ConnectionFailure_RequeuesFailingJob_DoesNotStopOtherJobs()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        HostSelectiveConnectionFactory factory = new(failHost: "192.168.1.1");
        await using ExecutionHarness harness = await ExecutionHarness.CreateAsync(
            time,
            mikroTikEnabled: true,
            connectionFactory: factory);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        MikrotikScheduledJob failingJob = CreateJob(
            importId,
            2,
            time.GetUtcNow().UtcDateTime.AddMinutes(-1),
            time.GetUtcNow().UtcDateTime.AddHours(1),
            "A",
            "192.168.1.1");
        MikrotikScheduledJob okJob = CreateJob(
            importId,
            3,
            time.GetUtcNow().UtcDateTime.AddMinutes(-1),
            time.GetUtcNow().UtcDateTime.AddHours(1),
            "B",
            "192.168.1.2");
        await harness.Scheduler.ScheduleAsync(failingJob, CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(okJob, CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => factory.SuccessCount >= 1, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => failingJob.ConnectionFailureCount >= 1, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => failingJob.NextRetryUtc is not null, TimeSpan.FromSeconds(5));

        Assert.Equal(1, failingJob.ConnectionFailureCount);
        Assert.Equal(0, failingJob.AuthenticationFailureCount);
        Assert.True(failingJob.NextRetryUtc >= time.GetUtcNow().AddMinutes(10).AddSeconds(-1));
        Assert.Equal(1, harness.Scheduler.QueuedCount);
        Assert.True(harness.Scheduler.IsTracked(importId));
    }

    /// <summary>
    /// Saturates 12 workers with gated connects; victim window expires before gate opens.
    /// Expects victim host never appears in connect attempts after release.
    /// </summary>
    [Fact]
    public async Task ExpiredWhileQueuedForExecution_DoesNotConnect()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        GateConnectionFactory gate = new();
        await using ExecutionHarness harness = await ExecutionHarness.CreateAsync(
            time,
            mikroTikEnabled: true,
            connectionFactory: gate,
            maxConcurrentRouterJobs: 12);

        Guid blockerImport = Guid.NewGuid();
        harness.Scheduler.RegisterImport(blockerImport, 12);
        DateTime blockStart = time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        DateTime blockStop = time.GetUtcNow().UtcDateTime.AddHours(2);
        for (int i = 0; i < 12; i++)
        {
            await harness.Scheduler.ScheduleAsync(
                CreateJob(blockerImport, i + 2, blockStart, blockStop, $"B{i}"),
                CancellationToken.None);
        }

        harness.Scheduler.SetAcceptedPath(blockerImport, harness.AcceptedCsv);
        await WaitForAsync(() => gate.Active >= 12, TimeSpan.FromSeconds(5));

        Guid victimImport = Guid.NewGuid();
        harness.Scheduler.RegisterImport(victimImport, 1);
        DateTime victimStart = time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        DateTime victimStop = time.GetUtcNow().UtcDateTime.AddMinutes(5);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(victimImport, 2, victimStart, victimStop, "VICTIM", "10.0.0.99"),
            CancellationToken.None);
        // Distinct Accepted path so the victim import is dispatchable while blockers hold workers.
        string victimCsv = Path.Combine(Path.GetDirectoryName(harness.AcceptedCsv)!, "victim.csv");
        await File.WriteAllTextAsync(victimCsv, "placeholder");
        harness.Scheduler.SetAcceptedPath(victimImport, victimCsv);

        await WaitForAsync(() => harness.Scheduler.DispatchedCount >= 13, TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromMinutes(10));
        harness.Scheduler.NudgeWake();
        gate.ReleaseAll();

        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(gate.Hosts, static h => h == "10.0.0.99");
    }

    /// <summary>
    /// 24 due jobs with MaxConcurrentRouterJobs=12 and gated factory.
    /// Expects MaxActive never exceeds 12 before release drains the queue.
    /// </summary>
    [Fact]
    public async Task Concurrency_NeverExceedsConfiguredLimit()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        GateConnectionFactory gate = new();
        await using ExecutionHarness harness = await ExecutionHarness.CreateAsync(
            time,
            mikroTikEnabled: true,
            connectionFactory: gate,
            maxConcurrentRouterJobs: 12);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 24);
        DateTime start = time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        DateTime stop = time.GetUtcNow().UtcDateTime.AddHours(1);
        for (int i = 0; i < 24; i++)
        {
            await harness.Scheduler.ScheduleAsync(
                CreateJob(importId, i + 2, start, stop, $"R{i}"),
                CancellationToken.None);
        }

        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);
        await WaitForAsync(() => gate.MaxActive >= 12, TimeSpan.FromSeconds(5));
        Assert.True(gate.MaxActive <= 12, $"MaxActive={gate.MaxActive}");
        gate.ReleaseAll();
        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Job blocked mid-connect; dispose harness.
    /// Expects pending 0 and no Completed CSV claiming success.
    /// </summary>
    [Fact]
    public async Task Shutdown_DoesNotMarkUnfinishedJobsSuccessful()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        GateConnectionFactory gate = new();
        ExecutionHarness harness = await ExecutionHarness.CreateAsync(
            time,
            mikroTikEnabled: true,
            connectionFactory: gate);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => gate.Active >= 1, TimeSpan.FromSeconds(5));
        string completedDir = harness.CompletedDir;
        await harness.DisposeAsync();

        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.False(Directory.Exists(completedDir) && Directory.EnumerateFiles(completedDir, "*.csv").Any());
    }

    /// <summary>
    /// Builds a scheduled job with configurable name/IP and UTC window.
    /// </summary>
    private static MikrotikScheduledJob CreateJob(
        Guid importId,
        int row,
        DateTime start,
        DateTime stop,
        string routerName = "CORE-01",
        string ip = "192.168.1.1") =>
        new()
        {
            ImportId = importId,
            SourceFileName = "batch.csv",
            RowNumber = row,
            RouterName = routerName,
            RouterIp = IPAddress.Parse(ip),
            DateStartUtc = DateTime.SpecifyKind(start, DateTimeKind.Utc),
            DateStopUtc = DateTime.SpecifyKind(stop, DateTimeKind.Utc),
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
    /// Scheduler + execution workers via SchedulerExecutionTestHost with a recording
    /// connection factory and temp Accepted/Completed layout.
    /// </summary>
    private sealed class ExecutionHarness : IAsyncDisposable
    {
        /// <summary>
        /// Cancels the scheduler run loop during disposal.
        /// </summary>
        private readonly CancellationTokenSource _schedulerCts = new();

        /// <summary>
        /// Cancels the router execution workers during disposal.
        /// </summary>
        private readonly CancellationTokenSource _executionCts;

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
        /// Initializes the harness state and starts the scheduler run loop.
        /// </summary>
        /// <param name="scheduler">Scheduler under test.</param>
        /// <param name="connectionFactory">Recording factory used by the executor.</param>
        /// <param name="acceptedCsv">Path of the placeholder Accepted CSV.</param>
        /// <param name="completedDir">Completed folder path.</param>
        /// <param name="root">Temporary ScheduledJobs root to delete on disposal.</param>
        /// <param name="executionTask">Task running the execution workers.</param>
        /// <param name="executionCts">Cancellation source that stops the execution workers.</param>
        private ExecutionHarness(
            InMemoryJobScheduler scheduler,
            RecordingConnectionFactory connectionFactory,
            string acceptedCsv,
            string completedDir,
            string root,
            Task executionTask,
            CancellationTokenSource executionCts)
        {
            Scheduler = scheduler;
            ConnectionFactory = connectionFactory;
            AcceptedCsv = acceptedCsv;
            CompletedDir = completedDir;
            _root = root;
            _executionTask = executionTask;
            _executionCts = executionCts;
            _schedulerTask = scheduler.RunAsync(_schedulerCts.Token);
        }

        /// <summary>
        /// Scheduler under test; jobs are enqueued through it and executed by the loop and workers this harness
        /// starts.
        /// </summary>
        public InMemoryJobScheduler Scheduler { get; }

        /// <summary>
        /// Connection factory that records every connect attempt the executor makes; tests inspect its recorded
        /// options and hosts.
        /// </summary>
        public RecordingConnectionFactory ConnectionFactory { get; }

        /// <summary>
        /// Path of the placeholder Accepted CSV that scheduled jobs are registered against.
        /// </summary>
        public string AcceptedCsv { get; }

        /// <summary>
        /// Completed folder inside the temporary ScheduledJobs tree; finished CSVs are archived here, so tests can
        /// tell whether an import finished.
        /// </summary>
        public string CompletedDir { get; }

        /// <summary>
        /// Creates the temp ScheduledJobs tree and starts the scheduler loop plus execution workers.
        /// </summary>
        /// <param name="time">Fake clock driving scheduling.</param>
        /// <param name="mikroTikEnabled">Whether MikroTik integration is enabled for the executor.</param>
        /// <param name="connectionFactory">Optional inner connection factory; wrapped by a recorder when not already one.</param>
        /// <param name="maxConcurrentRouterJobs">Maximum concurrent router jobs and worker count.</param>
        /// <param name="jobRetryOptions">Optional retry delays and limits.</param>
        /// <returns>A started harness; dispose it to stop loops and clean up.</returns>
        public static Task<ExecutionHarness> CreateAsync(
            FakeTimeProvider time,
            bool mikroTikEnabled = true,
            IMikrotikConnectionFactory? connectionFactory = null,
            int maxConcurrentRouterJobs = 12,
            JobRetryOptions? jobRetryOptions = null)
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

            RecordingConnectionFactory tracking = connectionFactory as RecordingConnectionFactory
                ?? new RecordingConnectionFactory(connectionFactory);

            (InMemoryJobScheduler scheduler, _, Task executionTask, CancellationTokenSource executionCts) =
                SchedulerExecutionTestHost.Create(
                    options,
                    paths,
                    new FileSystemScheduledJobFileMover(),
                    time,
                    connectionFactory: tracking,
                    mikroTikEnabled: mikroTikEnabled,
                    executionWorkers: maxConcurrentRouterJobs,
                    jobRetryOptions: jobRetryOptions);

            return Task.FromResult(new ExecutionHarness(
                scheduler,
                tracking,
                acceptedCsv,
                paths.Completed,
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
    }

    /// <summary>
    /// Records MikrotikConnectionOptions; delegates to an inner factory or returns a
    /// fully populated fake connection.
    /// </summary>
    private class RecordingConnectionFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Optional factory that connection requests are delegated to after recording.
        /// </summary>
        private readonly IMikrotikConnectionFactory? _inner;

        /// <summary>
        /// Initializes the recorder.
        /// </summary>
        /// <param name="inner">Optional factory to delegate to; a populated fake connection is returned when <see langword="null"/>.</param>
        public RecordingConnectionFactory(IMikrotikConnectionFactory? inner = null) => _inner = inner;

        /// <summary>
        /// Connection options of every attempt made against this factory. It is a thread-safe bag because workers
        /// connect concurrently, so enumeration order is not guaranteed.
        /// </summary>
        public ConcurrentBag<MikrotikConnectionOptions> Connections { get; } = [];

        /// <summary>
        /// Records the connection options, then delegates to the inner factory or returns a populated fake connection.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router.</param>
        /// <param name="cancellationToken">Token forwarded to the inner factory.</param>
        /// <returns>The connection created by the inner factory or a fully populated fake.</returns>
        public virtual async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Connections.Add(connection);
            if (_inner is not null)
            {
                return await _inner.CreateAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            return FakeMikrotikConnections.CreateFullyPopulated();
        }
    }

    /// <summary>
    /// Fails CreateAsync for a designated host with IOException; succeeds others and
    /// counts successes for peer-progress assertions.
    /// </summary>
    private sealed class HostSelectiveConnectionFactory : RecordingConnectionFactory
    {
        /// <summary>
        /// Host address whose connection attempts always fail.
        /// </summary>
        private readonly string _failHost;

        /// <summary>
        /// Number of successful connection creations.
        /// </summary>
        private int _successCount;

        /// <summary>
        /// Initializes the factory.
        /// </summary>
        /// <param name="failHost">Host address whose connection attempts always fail.</param>
        public HostSelectiveConnectionFactory(string failHost) => _failHost = failHost;

        /// <summary>
        /// Number of connections created successfully for hosts other than the failing one, which shows the remaining
        /// routers were still processed. Read with <c>Volatile.Read</c>.
        /// </summary>
        public int SuccessCount => Volatile.Read(ref _successCount);

        /// <summary>
        /// Records the attempt, throws <see cref="IOException"/> for the failing host, and otherwise returns a populated fake connection.
        /// </summary>
        /// <param name="connection">Connection options identifying the target host.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <returns>A populated fake connection for hosts other than the failing one.</returns>
        public override Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Connections.Add(connection);
            if (string.Equals(connection.Host, _failHost, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("connect failed");
            }

            Interlocked.Increment(ref _successCount);
            return Task.FromResult<ITikConnection>(FakeMikrotikConnections.CreateFullyPopulated());
        }
    }

    /// <summary>
    /// Holds CreateAsync on a semaphore to measure Active/MaxActive concurrency and
    /// control when connects complete via ReleaseAll.
    /// </summary>
    private sealed class GateConnectionFactory : RecordingConnectionFactory
    {
        /// <summary>
        /// Semaphore that blocks connect attempts until released.
        /// </summary>
        private readonly SemaphoreSlim _gate = new(0, int.MaxValue);

        /// <summary>
        /// Number of connect attempts currently blocked at the gate.
        /// </summary>
        private int _active;

        /// <summary>
        /// High-water mark of <see cref="_active"/>.
        /// </summary>
        private int _maxActive;

        /// <summary>
        /// Number of connect attempts currently in flight: started at the gate and not yet finished. Read with
        /// <c>Volatile.Read</c>.
        /// </summary>
        public int Active => Volatile.Read(ref _active);

        /// <summary>
        /// Highest number of connect attempts in flight at the same time, which tests use to prove how much
        /// parallelism the executor actually achieved.
        /// </summary>
        public int MaxActive => Volatile.Read(ref _maxActive);

        /// <summary>
        /// Gets every host a connect attempt was made against, including attempts still blocked.
        /// </summary>
        public ConcurrentBag<string> Hosts { get; } = [];

        /// <summary>
        /// Releases all gated connect waiters.
        /// </summary>
        public void ReleaseAll()
        {
            try
            {
                _gate.Release(10_000);
            }
            catch (SemaphoreFullException)
            {
            }
        }

        /// <summary>
        /// Records the host and concurrency, waits at the gate, then delegates to the base recording factory.
        /// </summary>
        /// <param name="connection">Connection options identifying the target host.</param>
        /// <param name="cancellationToken">Token that aborts the wait at the gate.</param>
        /// <returns>A populated fake connection once the gate is released.</returns>
        public override async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Hosts.Add(connection.Host);
            int active = Interlocked.Increment(ref _active);
            int max;
            do
            {
                max = _maxActive;
            }
            while (active > max && Interlocked.CompareExchange(ref _maxActive, active, max) != max);

            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await base.CreateAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
