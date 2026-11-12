using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Tests.Scheduling;
public sealed class InMemoryJobSchedulerTests
{
    [Fact]
    public async Task ImmediateJob_BecomesObservedEligible()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await using SchedulerHarness harness = CreateHarness(time, maximumPendingJobs: 10);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, row: 2, start: time.GetUtcNow().UtcDateTime.AddMinutes(-1), stop: time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await harness.WaitForPendingAsync(0);

        Assert.Equal(0, harness.Scheduler.PendingCount);
        await harness.WaitForCompletedAsync();
    }

    [Fact]
    public async Task FutureJob_BecomesEligible_AfterTimeAdvance()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        await using SchedulerHarness harness = CreateHarness(time);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        DateTime start = time.GetUtcNow().UtcDateTime.AddHours(1);
        DateTime stop = start.AddHours(1);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, 2, start, stop), CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        Assert.Equal(1, harness.Scheduler.PendingCount);

        time.Advance(TimeSpan.FromHours(1));
        await harness.WaitForPendingAsync(0);
        await harness.WaitForCompletedAsync();
    }

    [Fact]
    public async Task FutureJob_DoesNotBlock_ImmediateJob()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        RecordingLoggerProvider logs = new();
        await using SchedulerHarness harness = CreateHarness(time, loggerProvider: logs);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);

        MikrotikScheduledJob future = CreateJob(
            importId,
            row: 2,
            start: time.GetUtcNow().UtcDateTime.AddHours(2),
            stop: time.GetUtcNow().UtcDateTime.AddHours(3),
            routerName: "FUTURE");
        MikrotikScheduledJob immediate = CreateJob(
            importId,
            row: 3,
            start: time.GetUtcNow().UtcDateTime.AddMinutes(-1),
            stop: time.GetUtcNow().UtcDateTime.AddHours(1),
            routerName: "NOW");

        await harness.Scheduler.ScheduleAsync(future, CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(immediate, CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(
            () => logs.Messages.Any(static m => m.Contains("dispatched to execution queue", StringComparison.Ordinal) && m.Contains("NOW", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        Assert.Contains(logs.Messages, static m => m.Contains("NOW", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            static m => m.Contains("dispatched to execution queue", StringComparison.Ordinal) && m.Contains("FUTURE", StringComparison.Ordinal));
        Assert.Equal(1, harness.Scheduler.QueuedCount);
    }

    [Fact]
    public async Task MultipleJobs_BecomeEligible_Simultaneously()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        RecordingLoggerProvider logs = new();
        await using SchedulerHarness harness = CreateHarness(time, loggerProvider: logs);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 3);
        DateTime start = time.GetUtcNow().UtcDateTime.AddMinutes(5);
        DateTime stop = start.AddHours(1);
        for (int i = 0; i < 3; i++)
        {
            await harness.Scheduler.ScheduleAsync(
                CreateJob(importId, row: i + 2, start, stop, routerName: $"R{i}"),
                CancellationToken.None);
        }

        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);
        time.Advance(TimeSpan.FromMinutes(5));
        await harness.WaitForPendingAsync(0);
        await harness.WaitForCompletedAsync();

        Assert.Equal(3, logs.Messages.Count(static m => m.Contains("dispatched to execution queue", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DateStartUtc_IsInclusive_DateStopUtc_IsExclusive()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        RecordingLoggerProvider logs = new();
        await using SchedulerHarness harness = CreateHarness(time, loggerProvider: logs);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);

        DateTime start = time.GetUtcNow().UtcDateTime;
        DateTime stop = start.AddHours(1);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, 2, start, stop, "INCLUSIVE"), CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, 3, start.AddHours(-2), start, "EXCLUSIVE_STOP"), CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await harness.WaitForPendingAsync(0);
        await harness.WaitForCompletedAsync();

        Assert.Contains(
            logs.Messages,
            static m => m.Contains("INCLUSIVE", StringComparison.Ordinal) && m.Contains("dispatched to execution queue", StringComparison.Ordinal));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("EXCLUSIVE_STOP", StringComparison.Ordinal) && m.Contains("Job expired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JobExpiringBeforeStart_IsExpired()
    {
        // Start in the past relative to "now" after advance past stop without hitting start wait usefully:
        // Schedule with start=T+1h, stop=T+30m is invalid at validation; simulate clock jump:
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        RecordingLoggerProvider logs = new();
        await using SchedulerHarness harness = CreateHarness(time, loggerProvider: logs);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        DateTime start = time.GetUtcNow().UtcDateTime.AddMinutes(10);
        DateTime stop = time.GetUtcNow().UtcDateTime.AddMinutes(20);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, 2, start, stop), CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);
        Assert.Equal(1, harness.Scheduler.PendingCount);

        // Jump past both start and stop. Wait for the expired log (not merely PendingCount==0):
        // ProcessDueJobs removes queue entries before CompleteJob logs, so Pending can hit 0 first.
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.True(time.GetUtcNow().UtcDateTime >= stop);
        harness.Scheduler.NudgeWake();
        await WaitForAsync(
            () => logs.Messages.Any(static m => m.Contains("Job expired", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));

        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.DoesNotContain(
            logs.Messages,
            static m => m.Contains("dispatched to execution queue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CapacityBackpressure_WaitsUntilTerminalReleasesSlot()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        await using SchedulerHarness harness = CreateHarness(time, maximumPendingJobs: 1);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        DateTime start = time.GetUtcNow().UtcDateTime.AddHours(1);
        DateTime stop = start.AddHours(1);

        await harness.Scheduler.ScheduleAsync(CreateJob(importId, 2, start, stop, "A"), CancellationToken.None);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await harness.Scheduler.ScheduleAsync(CreateJob(importId, 3, start, stop, "B"), cts.Token));

        Assert.Equal(1, harness.Scheduler.PendingCount);

        time.Advance(TimeSpan.FromHours(1));
        await harness.WaitForPendingAsync(0);
    }

    [Fact]
    public async Task AbortImport_ReleasesCapacity_AndDoesNotCompleteBatch()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        await using SchedulerHarness harness = CreateHarness(time, maximumPendingJobs: 2);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        DateTime start = time.GetUtcNow().UtcDateTime.AddHours(1);
        DateTime stop = start.AddHours(1);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, 2, start, stop), CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, 3, start, stop), CancellationToken.None);

        Assert.Equal(2, harness.Scheduler.PendingCount);
        await harness.Scheduler.AbortImportAsync(importId, CancellationToken.None);
        Assert.Equal(0, harness.Scheduler.PendingCount);

        // Capacity freed: can schedule another import.
        Guid other = Guid.NewGuid();
        harness.Scheduler.RegisterImport(other, 2);
        await harness.Scheduler.ScheduleAsync(CreateJob(other, 2, start, stop), CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(CreateJob(other, 3, start, stop), CancellationToken.None);
        Assert.Equal(2, harness.Scheduler.PendingCount);

        Assert.False(Directory.EnumerateFiles(harness.CompletedDir).Any());
    }

    [Fact]
    public async Task Shutdown_DiscardsPendingJobs()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        SchedulerHarness harness = CreateHarness(time);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddHours(1), time.GetUtcNow().UtcDateTime.AddHours(2)),
            CancellationToken.None);

        Assert.Equal(1, harness.Scheduler.PendingCount);
        await harness.DisposeAsync();
        Assert.Equal(0, harness.Scheduler.PendingCount);
    }

    private static SchedulerHarness CreateHarness(
        FakeTimeProvider time,
        int maximumPendingJobs = 100,
        RecordingLoggerProvider? loggerProvider = null)
    {
        string root = TestHostFactory.CreateScheduledJobsRoot();
        ScheduledJobsOptions options = new()
        {
            Path = root,
            MaximumPendingJobs = maximumPendingJobs,
            MaxConcurrentRouterJobs = 12,
        };
        ScheduledJobsPaths paths = new(Options.Create(options));
        paths.EnsureCreated();

        string acceptedCsv = Path.Combine(paths.Accepted, "batch.csv");
        File.WriteAllText(acceptedCsv, "placeholder");

        ILoggerFactory loggerFactory = loggerProvider is null
            ? NullLoggerFactory.Instance
            : LoggerFactory.Create(builder =>
            {
                builder.ClearProviders();
                builder.AddProvider(loggerProvider);
                builder.SetMinimumLevel(LogLevel.Trace);
            });

        (InMemoryJobScheduler scheduler, _, Task executionTask, CancellationTokenSource executionCts) =
            SchedulerExecutionTestHost.Create(
                options,
                paths,
                new FileSystemScheduledJobFileMover(),
                time,
                loggerFactory.CreateLogger<InMemoryJobScheduler>());

        return new SchedulerHarness(scheduler, time, paths, acceptedCsv, root, executionTask, executionCts);
    }

    private static MikrotikScheduledJob CreateJob(
        Guid importId,
        int row,
        DateTime start,
        DateTime stop,
        string routerName = "CORE-01") =>
        new()
        {
            ImportId = importId,
            SourceFileName = "batch.csv",
            RowNumber = row,
            RouterName = routerName,
            RouterIp = IPAddress.Parse("192.168.1.1"),
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

    private sealed class SchedulerHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationTokenSource _executionCts;
        private readonly Task _runTask;
        private readonly Task _executionTask;

        public SchedulerHarness(
            InMemoryJobScheduler scheduler,
            FakeTimeProvider time,
            ScheduledJobsPaths paths,
            string acceptedCsv,
            string root,
            Task executionTask,
            CancellationTokenSource executionCts)
        {
            Scheduler = scheduler;
            Time = time;
            Paths = paths;
            AcceptedCsv = acceptedCsv;
            Root = root;
            CompletedDir = paths.Completed;
            _executionTask = executionTask;
            _executionCts = executionCts;
            _runTask = scheduler.RunAsync(_cts.Token);
        }

        public InMemoryJobScheduler Scheduler { get; }

        public FakeTimeProvider Time { get; }

        public ScheduledJobsPaths Paths { get; }

        public string AcceptedCsv { get; }

        public string CompletedDir { get; }

        public string Root { get; }

        public async Task WaitForPendingAsync(int expected)
        {
            await WaitForAsync(() => Scheduler.PendingCount == expected, TimeSpan.FromSeconds(5));
        }

        public async Task WaitForCompletedAsync()
        {
            await WaitForAsync(
                () => Directory.EnumerateFiles(CompletedDir, "*.csv").Any(),
                TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _executionCts.Cancel();
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }

            try
            {
                await _executionTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }

            await Scheduler.DisposeAsync();
            _cts.Dispose();
            _executionCts.Dispose();
            try
            {
                Directory.Delete(Root, true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
