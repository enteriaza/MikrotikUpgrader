// -----------------------------------------------------------------------------
// File: ImportedCsvActivationCancellationTests.cs
//
// Description:
// Covers import activation cancel/failure: Remaining reconciliation,
// incomplete activation retiring without Completed archival, path retention
// while scheduled jobs remain, full success archival, and transient retry
// not decrementing outstanding or writing terminal CSV fields early.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Tests.Infrastructure.Hosts;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests.Scheduling;

/// <summary>
/// Activation/cancellation lifecycle: reconcile Remaining, retire incomplete imports without Completed archival, keep path while scheduled jobs remain outstanding.
/// </summary>
public sealed class ImportedCsvActivationCancellationTests
{
    /// <summary>
    /// Cancel in AfterAcceptedPathRegistered before any ScheduleAsync.
    /// Expects import untracked, path store cleared, file remains in Accepted (not Completed/Rejected),
    /// and a manual re-import of the Accepted file still works.
    /// </summary>
    [Fact]
    public async Task C1_CancelAfterAcceptBeforeScheduling_RetiresImportLeavesAccepted()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using ActivationHarness harness = await ActivationHarness.CreateAsync(time, rowCount: 3);

        using CancellationTokenSource cts = new();
        Guid importId = Guid.Empty;
        harness.Processor.AfterAcceptedPathRegisteredForTestsAsync = (id, _, ct) =>
        {
            importId = id;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        string incoming = harness.WriteIncomingCsv("c1.csv");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Processor.ProcessIncomingAsync(incoming, cts.Token));

        Assert.NotEqual(Guid.Empty, importId);
        Assert.False(harness.Scheduler.IsTracked(importId));
        Assert.False(harness.PathStore.TryGetAcceptedCsvPath(importId, out _));
        Assert.Equal(0, harness.Scheduler.DispatchedCount);
        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.True(File.Exists(harness.FindAccepted("c1.csv")));
        Assert.Empty(Directory.GetFiles(harness.CompletedDir, "*.csv"));
        Assert.Empty(Directory.GetFiles(harness.RejectedDir, "*.csv"));

        // Manual re-import remains possible.
        string reimport = Path.Combine(harness.JobsRoot, "c1-reimport.csv");
        File.Copy(harness.FindAccepted("c1.csv"), reimport);
        harness.Processor.AfterAcceptedPathRegisteredForTestsAsync = null;
        await harness.Processor.ProcessIncomingAsync(reimport, CancellationToken.None);
        Assert.True(Directory.EnumerateFiles(harness.AcceptedDir, "c1-reimport*.csv").Any());
    }

    /// <summary>
    /// FinalizeImportActivation with scheduledCount=0 and activationIncomplete.
    /// Expects immediate untrack/path clear while Accepted file stays on disk.
    /// </summary>
    [Fact]
    public async Task D4_ZeroScheduledJobs_RetiresImmediately()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using ActivationHarness harness = await ActivationHarness.CreateAsync(time, rowCount: 2);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        harness.Scheduler.SetAcceptedPath(importId, harness.SeedAcceptedCsv(importId, "d4.csv", 2));
        Assert.True(harness.PathStore.TryGetAcceptedCsvPath(importId, out _));

        harness.Scheduler.FinalizeImportActivation(importId, scheduledCount: 0, activationIncomplete: true);

        Assert.False(harness.Scheduler.IsTracked(importId));
        Assert.False(harness.PathStore.TryGetAcceptedCsvPath(importId, out _));
        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.True(File.Exists(Path.Combine(harness.AcceptedDir, "d4.csv")));
        Assert.Empty(Directory.GetFiles(harness.CompletedDir, "*.csv"));
        Assert.Empty(Directory.GetFiles(harness.RejectedDir, "*.csv"));
    }

    /// <summary>
    /// Cancel after two of four rows scheduled.
    /// Expects Remaining=2, blank results on unscheduled rows, then after window advance
    /// import retires leaving Accepted (no Completed).
    /// </summary>
    [Fact]
    public async Task D1_CancelDuringScheduling_KeepsPathUntilScheduledJobsTerminal_ThenLeavesAccepted()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using ActivationHarness harness = await ActivationHarness.CreateAsync(time, rowCount: 4);

        using CancellationTokenSource cts = new();
        Guid importId = Guid.Empty;
        harness.Processor.AfterJobScheduledForTestsAsync = (id, queued, ct) =>
        {
            importId = id;
            if (queued >= 2)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        string incoming = harness.WriteIncomingCsv("d1.csv", rowCount: 4);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Processor.ProcessIncomingAsync(incoming, cts.Token));

        Assert.NotEqual(Guid.Empty, importId);
        Assert.True(harness.Scheduler.IsTracked(importId));
        Assert.True(harness.PathStore.TryGetAcceptedCsvPath(importId, out string? path));
        Assert.True(File.Exists(path));
        Assert.True(harness.Scheduler.TryGetImportRemaining(importId, out int remaining));
        Assert.Equal(2, remaining);
        Assert.Equal(2, harness.Scheduler.PendingCount);

        // Unscheduled rows must not have fabricated results (blank).
        Assert.True(string.IsNullOrEmpty(ReadColumn(path!, 4, ImportedCsvResultColumns.Result)));
        Assert.True(string.IsNullOrEmpty(ReadColumn(path!, 5, ImportedCsvResultColumns.Result)));

        // Advance into window so the two scheduled jobs can finish.
        time.SetUtcNow(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
        harness.Scheduler.NudgeWake();

        await WaitForAsync(() => !harness.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(10));
        Assert.False(harness.PathStore.TryGetAcceptedCsvPath(importId, out _));
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(harness.CompletedDir, "*.csv"));
        Assert.Empty(Directory.GetFiles(harness.RejectedDir, "*.csv"));
    }

    /// <summary>
    /// Two jobs mid-connect while FinalizeImportActivation races release.
    /// Expects clean untrack, path cleared, Accepted retained, no Completed, pending 0.
    /// </summary>
    [Fact]
    public async Task D2_TerminalDuringActivationReconcile_NoDoubleCountOrLeak()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        GateConnectionFactory gate = new();
        await using ActivationHarness harness = await ActivationHarness.CreateAsync(
            time,
            rowCount: 3,
            connectionFactory: gate,
            mikroTikEnabled: true);

        Guid importId = Guid.NewGuid();
        string csv = harness.SeedAcceptedCsv(importId, "d2.csv", 3);
        harness.Scheduler.RegisterImport(importId, 3);
        harness.Scheduler.SetAcceptedPath(importId, csv);

        MikrotikScheduledJob j1 = harness.CreateJob(importId, 2, "10.0.0.1");
        MikrotikScheduledJob j2 = harness.CreateJob(importId, 3, "10.0.0.2");
        await harness.Scheduler.ScheduleAsync(j1, CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(j2, CancellationToken.None);

        await WaitForAsync(() => gate.Active >= 2, TimeSpan.FromSeconds(5));

        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task finalizeTask = Task.Run(() =>
        {
            released.Task.GetAwaiter().GetResult();
            harness.Scheduler.FinalizeImportActivation(importId, scheduledCount: 2, activationIncomplete: true);
        });

        gate.ReleaseAll();
        released.SetResult();
        await finalizeTask;

        await WaitForAsync(() => !harness.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(10));
        Assert.False(harness.PathStore.TryGetAcceptedCsvPath(importId, out _));
        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.True(File.Exists(csv));
        Assert.Empty(Directory.GetFiles(harness.CompletedDir, "*.csv"));
    }

    /// <summary>
    /// Two of three rows finish; Remaining=1; then incomplete finalize.
    /// Expects retire without Completed archival.
    /// </summary>
    [Fact]
    public async Task D3_AllScheduledFinishBeforeCancelFinalize_RetiresCleanly()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await using ActivationHarness harness = await ActivationHarness.CreateAsync(
            time,
            rowCount: 3,
            mikroTikEnabled: true);

        Guid importId = Guid.NewGuid();
        string csv = harness.SeedAcceptedCsv(importId, "d3.csv", 3);
        harness.Scheduler.RegisterImport(importId, 3);
        harness.Scheduler.SetAcceptedPath(importId, csv);

        await harness.Scheduler.ScheduleAsync(harness.CreateJob(importId, 2, "10.0.0.1"), CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(harness.CreateJob(importId, 3, "10.0.0.2"), CancellationToken.None);
        harness.Scheduler.NudgeWake();

        // Both scheduled jobs terminal; never-scheduled row still holds Remaining at 1.
        await WaitForAsync(
            () => harness.Scheduler.TryGetImportRemaining(importId, out int r) && r == 1,
            TimeSpan.FromSeconds(10));

        harness.Scheduler.FinalizeImportActivation(importId, scheduledCount: 2, activationIncomplete: true);

        Assert.False(harness.Scheduler.IsTracked(importId));
        Assert.False(harness.PathStore.TryGetAcceptedCsvPath(importId, out _));
        Assert.True(File.Exists(csv));
        Assert.Empty(Directory.GetFiles(harness.CompletedDir, "*.csv"));
    }

    /// <summary>
    /// Full ProcessIncoming of an active-window CSV with start recording.
    /// Expects Completed archive with Success and timestamps; Accepted cleared; pending 0.
    /// </summary>
    [Fact]
    public async Task D5_FullSuccessfulActivation_ArchivesAndClearsPath()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        await using ActivationHarness harness = await ActivationHarness.CreateAsync(
            time,
            rowCount: 2,
            mikroTikEnabled: true,
            upgradeRecordsStart: true);

        string incoming = harness.WriteIncomingCsv("d5.csv", rowCount: 2, activeWindow: true);
        await harness.Processor.ProcessIncomingAsync(incoming, CancellationToken.None);

        await WaitForAsync(
            () => Directory.EnumerateFiles(harness.CompletedDir, "d5*.csv").Any(),
            TimeSpan.FromSeconds(10));

        string completed = Directory.GetFiles(harness.CompletedDir, "d5*.csv").Single();
        Assert.False(string.IsNullOrWhiteSpace(ReadColumn(completed, 2, ImportedCsvResultColumns.UpgradeStarted)));
        Assert.False(string.IsNullOrWhiteSpace(ReadColumn(completed, 2, ImportedCsvResultColumns.UpgradeCompleted)));
        Assert.Equal("Success", ReadColumn(completed, 2, ImportedCsvResultColumns.Result));
        Assert.Equal("Success", ReadColumn(completed, 3, ImportedCsvResultColumns.Result));
        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.Empty(Directory.GetFiles(harness.AcceptedDir, "d5*.csv"));
    }

    /// <summary>
    /// Fail-once-then-succeed connect during a fully activated import.
    /// Expects Remaining stays 1 and Result/Completed blank during retry; Success after delay.
    /// </summary>
    [Fact]
    public async Task D6_TransientRetry_DoesNotDecrementOutstandingOrWriteTerminalFields()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        FailOnceThenSucceedFactory factory = new();
        await using ActivationHarness harness = await ActivationHarness.CreateAsync(
            time,
            rowCount: 1,
            connectionFactory: factory,
            mikroTikEnabled: true,
            retryOptions: new JobRetryOptions
            {
                ConnectionRetryDelay = TimeSpan.FromMinutes(10),
                AuthenticationRetryDelay = TimeSpan.FromMinutes(10),
                MaximumUnsuccessfulAttempts = 5,
            });

        Guid importId = Guid.NewGuid();
        string csv = harness.SeedAcceptedCsv(importId, "d6.csv", 1);
        harness.Scheduler.RegisterImport(importId, 1);
        MikrotikScheduledJob job = harness.CreateJob(importId, 2, "10.0.0.9");
        await harness.Scheduler.ScheduleAsync(job, CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, csv);
        harness.Scheduler.FinalizeImportActivation(importId, scheduledCount: 1, activationIncomplete: false);

        await WaitForAsync(() => job.ConnectionFailureCount >= 1, TimeSpan.FromSeconds(5));
        Assert.True(harness.Scheduler.TryGetImportRemaining(importId, out int remaining));
        Assert.Equal(1, remaining);
        Assert.True(harness.Scheduler.IsTracked(importId));
        Assert.True(string.IsNullOrEmpty(ReadColumn(csv, 2, ImportedCsvResultColumns.UpgradeCompleted)));
        Assert.True(string.IsNullOrEmpty(ReadColumn(csv, 2, ImportedCsvResultColumns.Result)));

        time.Advance(TimeSpan.FromMinutes(10));
        harness.Scheduler.NudgeWake();

        await WaitForAsync(() => !harness.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(10));
        string completed = Path.Combine(harness.CompletedDir, "d6.csv");
        Assert.True(File.Exists(completed));
        Assert.Equal("Success", ReadColumn(completed, 2, ImportedCsvResultColumns.Result));
    }

    /// <summary>
    /// Polls a condition until timeout; asserts on expiry.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition was not met before timeout.");
    }

    /// <summary>
    /// Reads a single CSV cell by parser row number and column header (case-insensitive).
    /// </summary>
    private static string ReadColumn(string path, int rowNumber, string column)
    {
        using StreamReader reader = new(path, Encoding.UTF8);
        using CsvHelper.CsvReader csv = new(
            reader,
            new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null,
                HeaderValidated = null,
                BadDataFound = null,
            });
        Assert.True(csv.Read());
        Assert.True(csv.ReadHeader());
        string[] headers = csv.HeaderRecord ?? [];
        int index = Array.FindIndex(headers, h => string.Equals(h, column, StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, $"Column {column} missing");
        while (csv.Read())
        {
            if (csv.Context.Parser?.Row == rowNumber)
            {
                return csv.GetField(index) ?? string.Empty;
            }
        }

        throw new InvalidOperationException($"Row {rowNumber} not found");
    }

    /// <summary>
    /// First CreateAsync throws; subsequent calls succeed.
    /// </summary>
    private sealed class FailOnceThenSucceedFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Number of <see cref="CreateAsync"/> calls observed so far.
        /// </summary>
        private int _attempts;

        /// <summary>
        /// Fails the first attempt with a <see cref="TimeoutException"/>; later attempts return a populated fake connection.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <returns>A populated fake connection for every attempt after the first.</returns>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new TimeoutException("first connect fails");
            }

            return Task.FromResult((ITikConnection)FakeMikrotikConnections.CreateFullyPopulated());
        }
    }

    /// <summary>
    /// Holds connects to race finalize against in-flight work; ReleaseAll continues.
    /// </summary>
    private sealed class GateConnectionFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Guards <see cref="_blockers"/> and <see cref="_active"/>.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// Completion sources for connect attempts currently waiting for <see cref="ReleaseAll"/>.
        /// </summary>
        private readonly List<TaskCompletionSource> _blockers = [];

        /// <summary>
        /// Number of connect attempts currently blocked.
        /// </summary>
        private int _active;

        /// <summary>Number of CreateAsync calls currently blocked awaiting <see cref="ReleaseAll"/>.</summary>
        public int Active
        {
            get
            {
                lock (_gate)
                {
                    return _active;
                }
            }
        }

        /// <summary>
        /// Blocks until <see cref="ReleaseAll"/>, then returns a fully populated fake connection.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router (ignored).</param>
        /// <param name="cancellationToken">Token that aborts the wait.</param>
        /// <returns>A populated fake connection after release.</returns>
        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _blockers.Add(tcs);
                _active++;
            }

            try
            {
                await tcs.Task.WaitAsync(cancellationToken);
                return FakeMikrotikConnections.CreateFullyPopulated();
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                }
            }
        }

        /// <summary>
        /// Releases all gated connect waiters.
        /// </summary>
        public void ReleaseAll()
        {
            List<TaskCompletionSource> copy;
            lock (_gate)
            {
                copy = [.. _blockers];
                _blockers.Clear();
            }

            foreach (TaskCompletionSource tcs in copy)
            {
                tcs.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Writes UpgradeStarted then returns UpgradeSucceeded.
    /// </summary>
    /// <param name="writer">CSV result writer used to stamp the UpgradeStarted column.</param>
    private sealed class StartRecordingUpgradeExecutor(IImportedCsvResultWriter writer) : IRouterOsV6UpgradeExecutor
    {
        /// <summary>
        /// Records UpgradeStarted for the job via the CSV writer and reports a successful upgrade without touching the router.
        /// </summary>
        /// <param name="connection">Open router connection (ignored).</param>
        /// <param name="job">Scheduled job whose row receives the UpgradeStarted timestamp.</param>
        /// <param name="deviceInfo">Device information gathered before the upgrade (ignored).</param>
        /// <param name="assessment">Compatibility assessment for the device (ignored).</param>
        /// <param name="username">Router username (ignored).</param>
        /// <param name="password">Router password (ignored).</param>
        /// <param name="isWithinScheduleWindow">Callback reporting whether the schedule window is still open (ignored).</param>
        /// <param name="cancellationToken">Token passed to the CSV writer.</param>
        /// <returns>An <see cref="UpgradeOutcome.UpgradeSucceeded"/> result.</returns>
        public async Task<UpgradeExecutionResult> ExecuteAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            DeviceCompatibilityAssessment assessment,
            string username,
            string password,
            Func<bool> isWithinScheduleWindow,
            CancellationToken cancellationToken)
        {
            await writer.RecordUpgradeStartedAsync(job, DateTimeOffset.UtcNow, cancellationToken);
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.UpgradeSucceeded,
                Detail = "ok",
            };
        }
    }

    /// <summary>
    /// Processor + scheduler/workers + path store with hooks AfterAcceptedPathRegistered /
    /// AfterJobScheduled for cancellation injection; CSV seed/write helpers.
    /// </summary>
    private sealed class ActivationHarness : IAsyncDisposable
    {
        /// <summary>
        /// Temporary firmware root deleted on disposal.
        /// </summary>
        private readonly string _firmwareRoot;

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
        /// Initializes the harness state and starts the scheduler run loop.
        /// </summary>
        /// <param name="scheduler">Scheduler under test.</param>
        /// <param name="processor">CSV processor exposing the After* test seams.</param>
        /// <param name="pathStore">Accepted path store shared with the scheduler and writer.</param>
        /// <param name="paths">Resolved ScheduledJobs folder paths.</param>
        /// <param name="time">Fake clock shared with scheduler and executor.</param>
        /// <param name="jobsRoot">Temporary ScheduledJobs root directory.</param>
        /// <param name="firmwareRoot">Temporary firmware root directory.</param>
        /// <param name="rowCount">Default data row count for generated CSVs.</param>
        /// <param name="executionTask">Task running the execution workers.</param>
        /// <param name="executionCts">Cancellation source that stops the execution workers.</param>
        private ActivationHarness(
            InMemoryJobScheduler scheduler,
            ScheduledJobFileProcessor processor,
            ImportedCsvPathStore pathStore,
            ScheduledJobsPaths paths,
            FakeTimeProvider time,
            string jobsRoot,
            string firmwareRoot,
            int rowCount,
            Task executionTask,
            CancellationTokenSource executionCts)
        {
            Scheduler = scheduler;
            Processor = processor;
            PathStore = pathStore;
            Paths = paths;
            Time = time;
            JobsRoot = jobsRoot;
            _firmwareRoot = firmwareRoot;
            RowCount = rowCount;
            _executionTask = executionTask;
            _executionCts = executionCts;
            _schedulerTask = scheduler.RunAsync(_schedulerCts.Token);
        }

        /// <summary>In-memory scheduler under test.</summary>
        public InMemoryJobScheduler Scheduler { get; }

        /// <summary>CSV processor with optional After* test seams.</summary>
        public ScheduledJobFileProcessor Processor { get; }

        /// <summary>Shared Accepted path store wired into scheduler and writer.</summary>
        public ImportedCsvPathStore PathStore { get; }

        /// <summary>Resolved ScheduledJobs folder paths.</summary>
        public ScheduledJobsPaths Paths { get; }

        /// <summary>Fake clock shared with scheduler/executor.</summary>
        public FakeTimeProvider Time { get; }

        /// <summary>Temporary ScheduledJobs root directory.</summary>
        public string JobsRoot { get; }

        /// <summary>Default row count used by <see cref="WriteIncomingCsv"/> when unspecified.</summary>
        public int RowCount { get; }

        /// <summary>Accepted folder path.</summary>
        public string AcceptedDir => Paths.Accepted;

        /// <summary>Completed folder path.</summary>
        public string CompletedDir => Paths.Completed;

        /// <summary>Rejected folder path.</summary>
        public string RejectedDir => Paths.Rejected;

        /// <summary>
        /// Creates temp jobs and firmware trees, wires the processor, scheduler, and execution workers, and starts the loops.
        /// </summary>
        /// <param name="time">Fake clock driving scheduling and retries.</param>
        /// <param name="rowCount">Default data row count for generated CSVs.</param>
        /// <param name="connectionFactory">Connection factory for the executor; defaults to an immediate-success fake.</param>
        /// <param name="mikroTikEnabled">Whether MikroTik integration is enabled for the executor.</param>
        /// <param name="upgradeRecordsStart">When true the upgrade executor stamps UpgradeStarted; otherwise a stub is used.</param>
        /// <param name="retryOptions">Retry delays and limits; defaults are used when <see langword="null"/>.</param>
        /// <returns>A started harness; dispose it to stop loops and clean up.</returns>
        public static Task<ActivationHarness> CreateAsync(
            FakeTimeProvider time,
            int rowCount,
            IMikrotikConnectionFactory? connectionFactory = null,
            bool mikroTikEnabled = false,
            bool upgradeRecordsStart = false,
            JobRetryOptions? retryOptions = null)
        {
            string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
            string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
            ScheduledJobsOptions options = new()
            {
                Path = jobsRoot,
                MaximumPendingJobs = 100,
                MaxConcurrentRouterJobs = 12,
            };
            ScheduledJobsPaths paths = new(Options.Create(options));
            paths.EnsureCreated();

            ImportedCsvPathStore pathStore = new();
            ImportedCsvResultWriter writer = new(pathStore, NullLogger<ImportedCsvResultWriter>.Instance);
            RouterJobExecutionQueue queue = new(Options.Create(options));
            InMemoryJobScheduler scheduler = new(
                Options.Create(options),
                paths,
                new FileSystemScheduledJobFileMover(),
                queue,
                time,
                NullLogger<InMemoryJobScheduler>.Instance,
                Options.Create(retryOptions ?? new JobRetryOptions()),
                writer,
                pathStore);

            ScheduledJobCsvParser parser = new(
                Options.Create(options),
                new CsvDateTimeParser(Options.Create(new CsvOptions())));
            CsvFileReadinessChecker readiness = new(Options.Create(options));
            ScheduledJobFileProcessor processor = new(
                paths,
                parser,
                readiness,
                scheduler,
                new FileSystemScheduledJobFileMover(),
                Options.Create(options),
                time,
                NullLogger<ScheduledJobFileProcessor>.Instance);

            MikroTikOptions mikroTik = new()
            {
                Enabled = mikroTikEnabled,
                Username = "u",
                Password = "p",
                Port = 8728,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                ReconnectInterval = TimeSpan.FromSeconds(30),
            };

            IMikrotikConnectionFactory factory = connectionFactory
                ?? new ImmediateSuccessConnectFactory();
            IRouterOsV6UpgradeExecutor upgrades = upgradeRecordsStart
                ? new StartRecordingUpgradeExecutor(writer)
                : new StubUpgradeExecutor(UpgradeOutcome.UpgradeSucceeded);

            RouterJobExecutor executor = new(
                factory,
                new MikrotikApiClient(factory, Options.Create(mikroTik), NullLogger<MikrotikApiClient>.Instance),
                new AppSettingsRouterCredentialResolver(Options.Create(mikroTik)),
                new DeviceCompatibilityAssessor(
                    new StaticFirmwarePackageCatalog(firmwareRoot),
                    new FixedRouterOsV7TargetProvider("7.23.8")),
                upgrades,
                Options.Create(mikroTik),
                new FixedRouterOsV7TargetProvider("7.23.8"),
                Options.Create(new FirmwareUpgradeOptions
                {
                    Enabled = true,
                    AdvertisedBaseUrl = "http://10.255.255.1/firmware_files",
                }),
                time,
                NullLogger<RouterJobExecutor>.Instance);

            CancellationTokenSource execCts = new();
            Task execTask = RunWorkersAsync(queue, executor, scheduler, 4, execCts.Token);

            return Task.FromResult(new ActivationHarness(
                scheduler,
                processor,
                pathStore,
                paths,
                time,
                jobsRoot,
                firmwareRoot,
                rowCount,
                execTask,
                execCts));
        }

        /// <summary>
        /// Writes an Incoming CSV under the harness jobs root.
        /// </summary>
        public string WriteIncomingCsv(string fileName, int? rowCount = null, bool activeWindow = false)
        {
            int rows = rowCount ?? RowCount;
            string path = Path.Combine(JobsRoot, fileName);
            File.WriteAllText(path, BuildCsv(rows, activeWindow), Encoding.UTF8);
            return path;
        }

        /// <summary>
        /// Writes a CSV directly into Accepted for scheduler-only scenarios.
        /// </summary>
        public string SeedAcceptedCsv(Guid importId, string fileName, int rows)
        {
            _ = importId;
            string path = Path.Combine(AcceptedDir, fileName);
            File.WriteAllText(path, BuildCsv(rows, activeWindow: true), Encoding.UTF8);
            return path;
        }

        /// <summary>
        /// Locates an Accepted CSV by exact name or timestamped collision variant (<c>stem*.csv</c>).
        /// </summary>
        public string FindAccepted(string fileName)
        {
            string exact = Path.Combine(AcceptedDir, fileName);
            if (File.Exists(exact))
            {
                return exact;
            }

            string stem = Path.GetFileNameWithoutExtension(fileName);
            return Directory.EnumerateFiles(AcceptedDir, stem + "*.csv").First();
        }

        /// <summary>
        /// Builds a MikrotikScheduledJob for the harness clock and import.
        /// </summary>
        public MikrotikScheduledJob CreateJob(Guid importId, int row, string ip) =>
            new()
            {
                ImportId = importId,
                SourceFileName = "batch.csv",
                RowNumber = row,
                RouterName = $"R{row}",
                RouterIp = IPAddress.Parse(ip),
                DateStartUtc = Time.GetUtcNow().UtcDateTime.AddMinutes(-5),
                DateStopUtc = Time.GetUtcNow().UtcDateTime.AddHours(2),
            };

        /// <summary>
        /// Builds CSV text with empty result columns; <paramref name="activeWindow"/> chooses an always-open vs Dec-2026 window.
        /// </summary>
        private static string BuildCsv(int rows, bool activeWindow)
        {
            // Lifecycle ValidCsv uses Dec/2026 windows; activeWindow uses an always-open window.
            // Include empty result columns so unscheduled-row assertions work before any write.
            StringBuilder sb = new();
            sb.AppendLine(
                "DateStart,DateStop,RouterName,RouterIp," +
                $"{ImportedCsvResultColumns.UpgradeStarted},{ImportedCsvResultColumns.UpgradeCompleted},{ImportedCsvResultColumns.Result}");
            string start = activeWindow ? "01/01/2026 00:00:00" : "01/12/2026 08:00:00";
            string stop = activeWindow ? "01/01/2027 00:00:00" : "02/12/2026 18:00:00";
            for (int i = 0; i < rows; i++)
            {
                sb.AppendLine($"{start},{stop},Router-{i + 1},10.0.0.{i + 1},,,");
            }

            return sb.ToString();
        }

        /// <summary>Starts <paramref name="workerCount"/> queue-draining workers for the harness lifetime.</summary>
        private static async Task RunWorkersAsync(
            RouterJobExecutionQueue queue,
            RouterJobExecutor executor,
            IRouterJobExecutionCompleter completer,
            int workerCount,
            CancellationToken cancellationToken)
        {
            Task[] workers = Enumerable
                .Range(0, workerCount)
                .Select(_ => RunOneAsync(queue, executor, completer, cancellationToken))
                .ToArray();
            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        /// <summary>Single worker loop: read work items and invoke <see cref="RouterJobExecutor.ExecuteAsync"/>.</summary>
        private static async Task RunOneAsync(
            RouterJobExecutionQueue queue,
            RouterJobExecutor executor,
            IRouterJobExecutionCompleter completer,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (RouterJobWorkItem item in queue.Reader.ReadAllAsync(cancellationToken))
                {
                    await executor.ExecuteAsync(item, completer, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        /// <summary>
        /// Stops background loops and deletes temporary directories.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _schedulerCts.Cancel();
            _executionCts.Cancel();
            try { await _schedulerTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            try { await _executionTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            await Scheduler.DisposeAsync();
            _schedulerCts.Dispose();
            _executionCts.Dispose();
            try { Directory.Delete(JobsRoot, true); } catch { /* ignore */ }
            try { Directory.Delete(_firmwareRoot, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Returns a fully populated fake connection.
    /// </summary>
    private sealed class ImmediateSuccessConnectFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Immediately returns a fully populated fake connection.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <returns>A completed task containing the fake connection.</returns>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken) =>
            Task.FromResult((ITikConnection)FakeMikrotikConnections.CreateFullyPopulated());
    }
}
