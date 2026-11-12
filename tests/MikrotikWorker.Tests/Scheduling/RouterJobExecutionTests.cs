using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Scheduling;
public sealed class RouterJobExecutionTests
{
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

    [Fact]
    public async Task ConnectionFailure_MarksFailed_DoesNotStopOtherJobs()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        ThrowingMikrotikConnectionFactory failing = new(new IOException("connect failed"));
        await using ExecutionHarness harness = await ExecutionHarness.CreateAsync(
            time,
            mikroTikEnabled: true,
            connectionFactory: failing);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1), "A"),
            CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 3, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1), "B"),
            CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => !harness.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));
        await WaitForAsync(
            () => Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any(),
            TimeSpan.FromSeconds(5));
        Assert.False(harness.Scheduler.IsTracked(importId));
    }

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

        await WaitForAsync(() => gate.Active >= 12, TimeSpan.FromSeconds(5));

        Guid victimImport = Guid.NewGuid();
        harness.Scheduler.RegisterImport(victimImport, 1);
        DateTime victimStart = time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        DateTime victimStop = time.GetUtcNow().UtcDateTime.AddMinutes(5);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(victimImport, 2, victimStart, victimStop, "VICTIM", "10.0.0.99"),
            CancellationToken.None);

        await WaitForAsync(() => harness.Scheduler.DispatchedCount >= 13, TimeSpan.FromSeconds(5));

        time.Advance(TimeSpan.FromMinutes(10));
        harness.Scheduler.NudgeWake();
        gate.ReleaseAll();

        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(gate.Hosts, static h => h == "10.0.0.99");
    }

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

        await WaitForAsync(() => gate.MaxActive >= 12, TimeSpan.FromSeconds(5));
        Assert.True(gate.MaxActive <= 12, $"MaxActive={gate.MaxActive}");
        gate.ReleaseAll();
        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(10));
    }

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

        await WaitForAsync(() => gate.Active >= 1, TimeSpan.FromSeconds(5));
        string completedDir = harness.CompletedDir;
        await harness.DisposeAsync();

        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.False(Directory.Exists(completedDir) && Directory.EnumerateFiles(completedDir, "*.csv").Any());
    }

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

    private sealed class ExecutionHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _schedulerCts = new();
        private readonly CancellationTokenSource _executionCts;
        private readonly Task _schedulerTask;
        private readonly Task _executionTask;
        private readonly string _root;

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

        public InMemoryJobScheduler Scheduler { get; }

        public RecordingConnectionFactory ConnectionFactory { get; }

        public string AcceptedCsv { get; }

        public string CompletedDir { get; }

        public static Task<ExecutionHarness> CreateAsync(
            FakeTimeProvider time,
            bool mikroTikEnabled = true,
            IMikrotikConnectionFactory? connectionFactory = null,
            int maxConcurrentRouterJobs = 12)
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
                    executionWorkers: maxConcurrentRouterJobs);

            return Task.FromResult(new ExecutionHarness(
                scheduler,
                tracking,
                acceptedCsv,
                paths.Completed,
                root,
                executionTask,
                executionCts));
        }

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

    private class RecordingConnectionFactory : IMikrotikConnectionFactory
    {
        private readonly IMikrotikConnectionFactory? _inner;

        public RecordingConnectionFactory(IMikrotikConnectionFactory? inner = null) => _inner = inner;

        public ConcurrentBag<MikrotikConnectionOptions> Connections { get; } = [];

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

    private sealed class GateConnectionFactory : RecordingConnectionFactory
    {
        private readonly SemaphoreSlim _gate = new(0, int.MaxValue);
        private int _active;
        private int _maxActive;

        public int Active => Volatile.Read(ref _active);

        public int MaxActive => Volatile.Read(ref _maxActive);

        public ConcurrentBag<string> Hosts { get; } = [];

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
