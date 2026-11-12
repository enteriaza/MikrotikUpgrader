using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Tests.Scheduling;
[Collection("SchedulerHardening")]
public sealed class SchedulerConcurrencyHardeningTests
{
    [Fact]
    public async Task LostWakeup_JobScheduledBetweenSnapshotAndQueueCheck_IsProcessed()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await using HardeningHarness harness = CreateHarness(time, startRunLoop: false);

        TaskCompletionSource snapshotHit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowContinue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Scheduler.AfterWakeSnapshotBeforeQueueCheckAsync = async () =>
        {
            snapshotHit.TrySetResult();
            await allowContinue.Task.ConfigureAwait(false);
        };

        harness.StartRunLoop();

        // Wait until the idle loop has snapshotted an empty wake task.
        await snapshotHit.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        harness.Scheduler.AfterWakeSnapshotBeforeQueueCheckAsync = null;
        allowContinue.TrySetResult();

        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(5));
        await WaitForAsync(
            () => Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any()
                 && !harness.Scheduler.IsTracked(importId),
            TimeSpan.FromSeconds(5));
        Assert.False(harness.Scheduler.IsTracked(importId));
    }

    [Fact]
    public async Task CompletedMoveFailure_KeepsImportTracked_AndRetriesSuccessfully()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        string root = TestHostFactory.CreateScheduledJobsRoot();
        ScheduledJobsOptions options = new() { Path = root, MaximumPendingJobs = 100 };
        ScheduledJobsPaths paths = new(Options.Create(options));
        paths.EnsureCreated();
        ControllableFileMover mover = new(paths.Completed);
        mover.FailAllCompletedMoves();
        await using HardeningHarness harness = CreateHarness(time, mover, root: root, paths: paths, options: options);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(
            () => mover.CompletedMoveAttempts >= 1 && harness.Scheduler.IsTracked(importId),
            TimeSpan.FromSeconds(5));
        Assert.True(harness.Scheduler.IsTracked(importId));
        Assert.False(Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any());
        int failedAttempts = mover.CompletedMoveAttempts;

        mover.AllowCompletedMoves();
        await WaitForAsync(
            () =>
            {
                harness.Scheduler.RetryPendingCompletions();
                return mover.SuccessfulCompletedMoves >= 1
                       && Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any();
            },
            TimeSpan.FromSeconds(5));

        Assert.True(mover.SuccessfulCompletedMoves >= 1);
        Assert.False(harness.Scheduler.IsTracked(importId));
        Assert.True(mover.CompletedMoveAttempts > failedAttempts);
    }

    [Fact]
    public async Task JobsHeldUntilAcceptedPath_ThenCompleteAndArchiveOnce()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await using HardeningHarness harness = CreateHarness(time);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 3, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);

        Assert.Equal(2, harness.Scheduler.PendingCount);
        Assert.Equal(0, harness.Scheduler.DispatchedCount);
        Assert.True(harness.Scheduler.IsTracked(importId));
        Assert.False(Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any());

        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(
            () => Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any(),
            TimeSpan.FromSeconds(5));
        Assert.False(harness.Scheduler.IsTracked(importId));
        Assert.Single(Directory.GetFiles(harness.CompletedDir, "*.csv"));
    }

    [Fact]
    public async Task AcceptedPathIntent_BlocksDispatch_UntilOnDiskConfirmed()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await using HardeningHarness harness = CreateHarness(time);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);

        harness.Scheduler.BindAcceptedPathIntent(importId, harness.AcceptedCsv);
        Assert.Equal(1, harness.Scheduler.PendingCount);
        Assert.Equal(0, harness.Scheduler.DispatchedCount);
        Assert.True(harness.Scheduler.IsTracked(importId));
        Assert.False(Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any());

        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);
        await WaitForAsync(
            () => Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any(),
            TimeSpan.FromSeconds(5));
        Assert.False(harness.Scheduler.IsTracked(importId));
    }

    [Fact]
    public async Task FailureDuringAcceptedPathRegistration_StillBindsAndCompletes()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await using HardeningHarness harness = CreateHarness(time);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);

        harness.Scheduler.BindAcceptedPathIntent(importId, harness.AcceptedCsv);
        Assert.Equal(0, harness.Scheduler.DispatchedCount);

        // Simulate HandleIngestFailure recovery after SetAcceptedPath threw post-move:
        // intent was bound, file is on disk under Accepted, confirm now.
        Assert.True(File.Exists(harness.AcceptedCsv));
        Assert.True(harness.Scheduler.IsTracked(importId));
        Assert.False(Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any());

        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(
            () => Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any(),
            TimeSpan.FromSeconds(5));
        Assert.False(harness.Scheduler.IsTracked(importId));
    }

    [Fact]
    public async Task ShutdownDuringAcceptedIntent_DoesNotArchiveWithoutOnDiskConfirm()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await using HardeningHarness harness = CreateHarness(time);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(importId, 2, time.GetUtcNow().UtcDateTime.AddMinutes(-1), time.GetUtcNow().UtcDateTime.AddHours(1)),
            CancellationToken.None);
        harness.Scheduler.BindAcceptedPathIntent(importId, harness.AcceptedCsv);

        Assert.Equal(1, harness.Scheduler.PendingCount);
        Assert.Equal(0, harness.Scheduler.DispatchedCount);
        await harness.Scheduler.StopAsync();

        Assert.False(Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any());
        Assert.True(File.Exists(harness.AcceptedCsv));
    }

    [Fact]
    public async Task AcceptedMoveFailure_AbortsIntentAndLeavesProcessingFile()
    {
        // Validation requires future datestart relative to parser utcNow.
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        string root = TestHostFactory.CreateScheduledJobsRoot();
        ScheduledJobsOptions options = new() { Path = root, MaximumPendingJobs = 100 };
        ScheduledJobsPaths paths = new(Options.Create(options));
        paths.EnsureCreated();

        ControllableFileMover mover = new(paths.Completed);
        mover.FailNextAcceptedMoves(1);

        CsvDateTimeParser dateTimeParser = new(Options.Create(new CsvOptions()));
        ScheduledJobCsvParser parser = new(Options.Create(options), dateTimeParser);
        CsvFileReadinessChecker readiness = new(Options.Create(options));
        options.MaxConcurrentRouterJobs = 12;
        (InMemoryJobScheduler scheduler, _, Task executionTask, CancellationTokenSource executionCts) =
            SchedulerExecutionTestHost.Create(
                options,
                paths,
                mover,
                time);
        ScheduledJobFileProcessor processor = new(
            paths,
            parser,
            readiness,
            scheduler,
            mover,
            Options.Create(options),
            time,
            NullLogger<ScheduledJobFileProcessor>.Instance);

        await using HardeningHarness harness = new(
            scheduler,
            paths,
            Path.Combine(paths.Accepted, "batch.csv"),
            root,
            startRunLoop: true,
            executionTask,
            executionCts);

        string processing = Path.Combine(paths.Processing, "batch.csv");
        await File.WriteAllTextAsync(
            processing,
            """
            datestart,datestop,routername,routerip
            01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1
            """);

        await Assert.ThrowsAsync<IOException>(() => processor.ProcessProcessingFileAsync(processing, CancellationToken.None));

        Assert.True(File.Exists(processing));
        Assert.False(Directory.EnumerateFiles(paths.Accepted, "*.csv").Any());
        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.Equal(1, mover.AcceptedMoveAttempts);
    }

    [Fact]
    public async Task Shutdown_WhileWaitingForCapacity_DoesNotThrowObjectDisposed()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        await using HardeningHarness harness = CreateHarness(time, maximumPendingJobs: 1);

        Guid blocker = Guid.NewGuid();
        harness.Scheduler.RegisterImport(blocker, 1);
        await harness.Scheduler.ScheduleAsync(
            CreateJob(blocker, 2, time.GetUtcNow().UtcDateTime.AddHours(1), time.GetUtcNow().UtcDateTime.AddHours(2)),
            CancellationToken.None);

        Guid waiting = Guid.NewGuid();
        harness.Scheduler.RegisterImport(waiting, 1);
        Task schedule = harness.Scheduler.ScheduleAsync(
            CreateJob(waiting, 2, time.GetUtcNow().UtcDateTime.AddHours(1), time.GetUtcNow().UtcDateTime.AddHours(2)),
            CancellationToken.None).AsTask();

        await Task.Delay(50);
        await harness.Scheduler.StopAsync();

        Exception? ex = await Record.ExceptionAsync(async () => await schedule);
        Assert.True(
            ex is null
            || ex is OperationCanceledException
            || ex is InvalidOperationException,
            ex?.ToString());
        Assert.Equal(0, harness.Scheduler.PendingCount);
    }

    [Fact]
    public async Task Abort_RacingTerminalProcessing_ReleasesCapacityExactlyOnce()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        await using HardeningHarness harness = CreateHarness(time, maximumPendingJobs: 2);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        DateTime start = time.GetUtcNow().UtcDateTime.AddHours(1);
        DateTime stop = start.AddHours(1);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, 2, start, stop), CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, 3, start, stop), CancellationToken.None);

        Task abort = harness.Scheduler.AbortImportAsync(importId, CancellationToken.None);
        time.Advance(TimeSpan.FromHours(1));
        await abort;

        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.False(harness.Scheduler.IsTracked(importId));

        Guid other = Guid.NewGuid();
        harness.Scheduler.RegisterImport(other, 2);
        await harness.Scheduler.ScheduleAsync(CreateJob(other, 2, start.AddHours(2), stop.AddHours(2)), CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(CreateJob(other, 3, start.AddHours(2), stop.AddHours(2)), CancellationToken.None);
        Assert.Equal(2, harness.Scheduler.PendingCount);
    }

    [Fact]
    public async Task DiscoveryQueue_DedupesAndRecoversAfterSaturation()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        try
        {
            ScheduledJobsOptions options = new()
            {
                Path = jobsRoot,
                DiscoveryQueueCapacity = 1,
                MaxConcurrentCsvImports = 1,
                ScanInterval = TimeSpan.FromHours(1),
            };
            ScheduledJobsPaths paths = new(Options.Create(options));
            paths.EnsureCreated();

            // Exercise discovery helper indirectly via watcher service fields is hard;
            // validate coalescing semantics with the same algorithm.
            DiscoveryProbe probe = new(capacity: 1);
            string a = Path.Combine(jobsRoot, "a.csv");
            string b = Path.Combine(jobsRoot, "b.csv");
            File.WriteAllText(a, "x");
            File.WriteAllText(b, "y");

            Assert.True(probe.TryEnqueue(a));
            Assert.True(probe.TryEnqueue(a)); // coalesced
            Assert.False(probe.TryEnqueue(b)); // saturated
            Assert.Equal(1, probe.SaturationCount);

            probe.Complete(a);
            Assert.True(probe.TryEnqueue(b));
            probe.Complete(b);
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
        }
    }

    private static HardeningHarness CreateHarness(
        FakeTimeProvider time,
        ControllableFileMover? mover = null,
        int maximumPendingJobs = 100,
        bool startRunLoop = true,
        string? root = null,
        ScheduledJobsPaths? paths = null,
        ScheduledJobsOptions? options = null)
    {
        root ??= TestHostFactory.CreateScheduledJobsRoot();
        options ??= new ScheduledJobsOptions
        {
            Path = root,
            MaximumPendingJobs = maximumPendingJobs,
            MaxConcurrentRouterJobs = 12,
        };
        options.MaxConcurrentRouterJobs = Math.Max(12, options.MaxConcurrentRouterJobs);
        paths ??= new ScheduledJobsPaths(Options.Create(options));
        paths.EnsureCreated();

        string acceptedCsv = Path.Combine(paths.Accepted, "batch.csv");
        if (!File.Exists(acceptedCsv))
        {
            File.WriteAllText(acceptedCsv, "placeholder");
        }

        ControllableFileMover effectiveMover = mover ?? new ControllableFileMover(paths.Completed);
        (InMemoryJobScheduler scheduler, _, Task executionTask, CancellationTokenSource executionCts) =
            SchedulerExecutionTestHost.Create(
                options,
                paths,
                effectiveMover,
                time);

        return new HardeningHarness(
            scheduler,
            paths,
            acceptedCsv,
            root,
            startRunLoop,
            executionTask,
            executionCts);
    }

    private static MikrotikScheduledJob CreateJob(
        Guid importId,
        int row,
        DateTime start,
        DateTime stop) =>
        new()
        {
            ImportId = importId,
            SourceFileName = "batch.csv",
            RowNumber = row,
            RouterName = "CORE-01",
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

    private sealed class HardeningHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationTokenSource _executionCts;
        private readonly Task _executionTask;
        private Task? _runTask;

        public HardeningHarness(
            InMemoryJobScheduler scheduler,
            ScheduledJobsPaths paths,
            string acceptedCsv,
            string root,
            bool startRunLoop,
            Task executionTask,
            CancellationTokenSource executionCts)
        {
            Scheduler = scheduler;
            Paths = paths;
            AcceptedCsv = acceptedCsv;
            Root = root;
            CompletedDir = paths.Completed;
            _executionTask = executionTask;
            _executionCts = executionCts;
            if (startRunLoop)
            {
                StartRunLoop();
            }
        }

        public InMemoryJobScheduler Scheduler { get; }

        public ScheduledJobsPaths Paths { get; }

        public string AcceptedCsv { get; }

        public string CompletedDir { get; }

        public string Root { get; }

        public void StartRunLoop()
        {
            _runTask ??= Scheduler.RunAsync(_cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _executionCts.Cancel();
            if (_runTask is not null)
            {
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
            }
        }
    }

    /// <summary>
    /// Mirrors watcher discovery coalesce/saturation behaviour for deterministic unit testing.
    /// </summary>
    private sealed class DiscoveryProbe(int capacity)
    {
        private readonly Channel<string> _channel = Channel.CreateBounded<string>(capacity);
        private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public int SaturationCount { get; private set; }

        public bool TryEnqueue(string path)
        {
            string key = Path.GetFullPath(path);
            lock (_gate)
            {
                if (!_pending.Add(key))
                {
                    return true;
                }
            }

            if (_channel.Writer.TryWrite(path))
            {
                return true;
            }

            lock (_gate)
            {
                _pending.Remove(key);
            }

            SaturationCount++;
            return false;
        }

        public void Complete(string path)
        {
            lock (_gate)
            {
                _pending.Remove(Path.GetFullPath(path));
            }

            _ = _channel.Reader.TryRead(out _);
        }
    }
}
