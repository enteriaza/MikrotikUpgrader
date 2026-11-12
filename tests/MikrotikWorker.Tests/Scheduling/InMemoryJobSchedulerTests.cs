// -----------------------------------------------------------------------------
// File: InMemoryJobSchedulerTests.cs
//
// Description:
// Core InMemoryJobScheduler behaviours: eligibility vs time, future jobs not
// blocking immediate ones, inclusive start/exclusive stop, expiry, capacity
// backpressure, abort releasing capacity, and shutdown discarding pending work.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Tests.Scheduling;

/// <summary>
/// Unit tests for time-aware scheduling, capacity, abort, and shutdown of InMemoryJobScheduler.
/// </summary>
public sealed class InMemoryJobSchedulerTests
{
    /// <summary>
    /// Job whose window already contains now; Accepted path set.
    /// Expects pending drains to 0 and the import archives to Completed.
    /// </summary>
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

    /// <summary>
    /// Job starting one hour ahead remains pending until clock advances into the window.
    /// Expects dispatch/completion only after Advance, not before.
    /// </summary>
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

    /// <summary>
    /// Same import: future job enqueued before an already-due job.
    /// Expects NOW dispatched while FUTURE stays queued — no head-of-line blocking.
    /// </summary>
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

    /// <summary>
    /// Three jobs sharing a future start; clock advanced into window.
    /// Expects three dispatch log lines and pending cleared.
    /// </summary>
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

    /// <summary>
    /// One job with start==now (due) and one with stop==now (expired).
    /// Expects inclusive start dispatches; exclusive stop expires without connect/dispatch.
    /// </summary>
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

    /// <summary>
    /// Future window jumped entirely past by clock advance.
    /// Expects Job expired log, pending 0, and no dispatch.
    /// </summary>
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

    /// <summary>
    /// MaximumPendingJobs=1 with a future job holding the slot.
    /// Expects second ScheduleAsync cancels on short token until the first completes.
    /// </summary>
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
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await harness.Scheduler.ScheduleAsync(CreateJob(importId, 3, start, stop, "B"), cts.Token));

        Assert.Equal(1, harness.Scheduler.PendingCount);

        time.Advance(TimeSpan.FromHours(1));
        await harness.WaitForPendingAsync(0);
    }

    /// <summary>
    /// Abort of a two-job future import.
    /// Expects pending cleared, capacity reusable by another import, and no Completed files.
    /// </summary>
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

    /// <summary>
    /// Dispose while a future job is pending.
    /// Expects pending count drops to 0 without completing the batch.
    /// </summary>
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

    /// <summary>
    /// Temp ScheduledJobs tree, placeholder Accepted CSV, optional recording logger,
    /// and SchedulerExecutionTestHost run/execution loops.
    /// </summary>
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

    /// <summary>
    /// Builds a MikrotikScheduledJob with UTC start/stop and fixed IP.
    /// </summary>
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
    /// Owns scheduler RunAsync + execution worker CTS/tasks and temp root cleanup.
    /// WaitForPendingAsync / WaitForCompletedAsync poll PendingCount and Completed dir.
    /// </summary>
    private sealed class SchedulerHarness : IAsyncDisposable
    {
        /// <summary>
        /// Cancels the scheduler run loop during disposal.
        /// </summary>
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// Cancels the router execution workers during disposal.
        /// </summary>
        private readonly CancellationTokenSource _executionCts;

        /// <summary>
        /// Task running the scheduler loop.
        /// </summary>
        private readonly Task _runTask;

        /// <summary>
        /// Task running the router execution workers.
        /// </summary>
        private readonly Task _executionTask;

        /// <summary>
        /// Initializes the harness and starts the scheduler run loop.
        /// </summary>
        /// <param name="scheduler">Scheduler under test.</param>
        /// <param name="time">Fake clock driving the scheduler.</param>
        /// <param name="paths">Resolved ScheduledJobs folder paths.</param>
        /// <param name="acceptedCsv">Path of the placeholder Accepted CSV.</param>
        /// <param name="root">Temporary ScheduledJobs root to delete on disposal.</param>
        /// <param name="executionTask">Task running the execution workers.</param>
        /// <param name="executionCts">Cancellation source that stops the execution workers.</param>
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

        /// <summary>
        /// Scheduler under test, created over the temporary ScheduledJobs tree this harness builds.
        /// </summary>
        public InMemoryJobScheduler Scheduler { get; }

        /// <summary>
        /// Fake clock that drives the scheduler's job eligibility and wake-ups; tests advance it explicitly.
        /// </summary>
        public FakeTimeProvider Time { get; }

        /// <summary>
        /// Resolved ScheduledJobs folder paths for the temporary tree this harness creates.
        /// </summary>
        public ScheduledJobsPaths Paths { get; }

        /// <summary>
        /// Path of the placeholder Accepted CSV that the scheduled jobs are registered against.
        /// </summary>
        public string AcceptedCsv { get; }

        /// <summary>
        /// Completed folder inside the temporary ScheduledJobs tree; finished CSVs are archived here.
        /// </summary>
        public string CompletedDir { get; }

        /// <summary>
        /// Temporary ScheduledJobs root directory that holds every folder this harness creates; it is deleted on
        /// disposal.
        /// </summary>
        public string Root { get; }

        /// <summary>
        /// Waits until PendingCount equals the expected value.
        /// </summary>
        public async Task WaitForPendingAsync(int expected)
        {
            await WaitForAsync(() => Scheduler.PendingCount == expected, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Waits until the Accepted CSV has been archived to Completed.
        /// </summary>
        public async Task WaitForCompletedAsync()
        {
            await WaitForAsync(
                () => Directory.EnumerateFiles(CompletedDir, "*.csv").Any(),
                TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Stops background loops and deletes temporary directories.
        /// </summary>
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
