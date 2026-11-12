// -----------------------------------------------------------------------------
// File: ImportedCsvResultRetryIntegrationTests.cs
//
// Description:
// Integration coverage for CSV result columns under transient connect/auth
// retry: non-terminal retries leave rows incomplete; exhaustion writes
// terminal failure with blank UpgradeStarted; success records start at the
// upgrade boundary after connect.
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
using tik4net.Testing;

namespace MikrotikWorker.Tests.Scheduling;

/// <summary>
/// Verifies Accepted CSV result persistence across transient JobRetry and terminal outcomes.
/// </summary>
public sealed class ImportedCsvResultRetryIntegrationTests
{
    /// <summary>
    /// Always-timeout connect factory.
    /// After first connection failure, expects Result column still empty.
    /// </summary>
    [Fact]
    public async Task TransientConnectionFailure_DoesNotCompleteCsvRow()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using RetryCsvHarness harness = await RetryCsvHarness.CreateAsync(
            time,
            new AlwaysTimeoutConnectFactory());

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => job.ConnectionFailureCount >= 1, TimeSpan.FromSeconds(5));

        Assert.True(string.IsNullOrEmpty(harness.TryReadResult(2)));
    }

    /// <summary>
    /// Always login-refused factory.
    /// After first auth failure, expects Result column still empty.
    /// </summary>
    [Fact]
    public async Task TransientAuthFailure_DoesNotCompleteCsvRow()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using RetryCsvHarness harness = await RetryCsvHarness.CreateAsync(
            time,
            new AlwaysAuthFailConnectFactory());

        MikrotikScheduledJob job = await harness.ScheduleSingleAsync();
        await WaitForAsync(() => job.AuthenticationFailureCount >= 1, TimeSpan.FromSeconds(5));

        Assert.True(string.IsNullOrEmpty(harness.TryReadResult(2)));
    }

    /// <summary>
    /// MaximumUnsuccessfulAttempts=0 so first timeout is terminal.
    /// Expects Failed - Connection with blank UpgradeStarted on Accepted/Completed.
    /// </summary>
    [Fact]
    public async Task RetryExhaustion_WritesTerminalFailure_WithBlankStart()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using RetryCsvHarness harness = await RetryCsvHarness.CreateAsync(
            time,
            new AlwaysTimeoutConnectFactory(),
            retryOptions: new JobRetryOptions
            {
                ConnectionRetryDelay = TimeSpan.FromMinutes(10),
                AuthenticationRetryDelay = TimeSpan.FromMinutes(10),
                // attempt is incremented then compared with `>`; 0 makes the first failure terminal.
                MaximumUnsuccessfulAttempts = 0,
            });

        await harness.ScheduleSingleAsync();
        await WaitForAsync(
            () => !string.IsNullOrEmpty(harness.TryReadResultFromAcceptedOrCompleted(2)),
            TimeSpan.FromSeconds(8));

        string? result = harness.TryReadResultFromAcceptedOrCompleted(2);
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.StartsWith("Failed - Connection", result!, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(harness.TryReadStartedFromAcceptedOrCompleted(2)));
    }

    /// <summary>
    /// Immediate success connect + executor that records start.
    /// Expects Success, non-blank start, and ConnectObservedAt ≤ UpgradeStartedAt.
    /// </summary>
    [Fact]
    public async Task SuccessfulUpgrade_RecordsStartAtUpgradeBoundary()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using RetryCsvHarness harness = await RetryCsvHarness.CreateAsync(
            time,
            new ImmediateSuccessConnectFactory(),
            upgradeRecordsStart: true);

        await harness.ScheduleSingleAsync();
        await WaitForAsync(
            () => harness.TryReadResultFromAcceptedOrCompleted(2) == "Success",
            TimeSpan.FromSeconds(8));

        Assert.False(string.IsNullOrWhiteSpace(harness.TryReadStartedFromAcceptedOrCompleted(2)));
        Assert.Equal("Success", harness.TryReadResultFromAcceptedOrCompleted(2));
        Assert.NotNull(harness.Marks.UpgradeStartedAt);
        Assert.NotNull(harness.Marks.ConnectObservedAt);
        Assert.True(harness.Marks.ConnectObservedAt <= harness.Marks.UpgradeStartedAt);
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
    /// Mutable connect/upgrade timestamps for ordering assertions.
    /// </summary>
    private sealed class TimelineMarks
    {
        /// <summary>
        /// UTC time at which the observing connection factory first saw a connect attempt.
        /// </summary>
        public DateTimeOffset? ConnectObservedAt;

        /// <summary>
        /// UTC time at which the fake upgrade executor recorded UpgradeStarted.
        /// </summary>
        public DateTimeOffset? UpgradeStartedAt;
    }

    /// <summary>
    /// Fake V6 upgrade executor that stamps UpgradeStarted via the CSV writer and returns Succeeded.
    /// </summary>
    /// <param name="writer">CSV result writer used to stamp the UpgradeStarted column.</param>
    /// <param name="marks">Timeline marks updated with the UpgradeStarted time.</param>
    private sealed class StartRecordingUpgradeExecutor(IImportedCsvResultWriter writer, TimelineMarks marks)
        : IRouterOsV6UpgradeExecutor
    {
        /// <summary>
        /// Captures the start time in the timeline marks, records UpgradeStarted via the CSV writer, and reports success.
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
            DateTimeOffset started = DateTimeOffset.UtcNow;
            marks.UpgradeStartedAt = started;
            await writer.RecordUpgradeStartedAsync(job, started, cancellationToken);
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.UpgradeSucceeded,
                Detail = "ok",
            };
        }
    }

    /// <summary>
    /// Returns a fully populated fake tik4net connection.
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

    /// <summary>
    /// Throws TimeoutException on every CreateAsync.
    /// </summary>
    private sealed class AlwaysTimeoutConnectFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Always fails the connection attempt with a <see cref="TimeoutException"/>.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken) =>
            throw new TimeoutException("Connection timeout");
    }

    /// <summary>
    /// Throws TikConnectionLoginRefusedException on every CreateAsync.
    /// </summary>
    private sealed class AlwaysAuthFailConnectFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Always fails the connection attempt with a <see cref="TikConnectionLoginRefusedException"/>.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken) =>
            throw new TikConnectionLoginRefusedException("invalid user name or password", "api");
    }

    /// <summary>
    /// Decorator that records ConnectObservedAt before delegating.
    /// </summary>
    /// <param name="inner">Factory that actually creates the connection.</param>
    /// <param name="marks">Timeline marks updated with the connect observation time.</param>
    private sealed class ObservingConnectFactory(IMikrotikConnectionFactory inner, TimelineMarks marks)
        : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Stamps <see cref="TimelineMarks.ConnectObservedAt"/> and then delegates to the inner factory.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router.</param>
        /// <param name="cancellationToken">Token forwarded to the inner factory.</param>
        /// <returns>The connection created by the inner factory.</returns>
        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            marks.ConnectObservedAt = DateTimeOffset.UtcNow;
            return await inner.CreateAsync(connection, cancellationToken);
        }
    }

    /// <summary>
    /// Temp jobs root, path store, result writer, scheduler/workers, and helpers to
    /// schedule a single job and read result columns from Accepted or Completed.
    /// </summary>
    private sealed class RetryCsvHarness : IAsyncDisposable
    {
        /// <summary>
        /// Temporary ScheduledJobs root deleted on disposal.
        /// </summary>
        private readonly string _root;

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
        /// Cancels the router execution workers during disposal.
        /// </summary>
        private readonly CancellationTokenSource _executionCts;

        /// <summary>
        /// Initializes the harness state and starts the scheduler run loop.
        /// </summary>
        /// <param name="scheduler">Scheduler under test.</param>
        /// <param name="time">Fake clock shared with scheduler and executor.</param>
        /// <param name="acceptedCsv">Path of the seeded Accepted CSV.</param>
        /// <param name="completedDir">Completed folder path.</param>
        /// <param name="root">Temporary ScheduledJobs root to delete on disposal.</param>
        /// <param name="executionTask">Task running the execution workers.</param>
        /// <param name="executionCts">Cancellation source that stops the execution workers.</param>
        /// <param name="marks">Timeline marks populated by the observing factory and fake executor.</param>
        private RetryCsvHarness(
            InMemoryJobScheduler scheduler,
            FakeTimeProvider time,
            string acceptedCsv,
            string completedDir,
            string root,
            Task executionTask,
            CancellationTokenSource executionCts,
            TimelineMarks marks)
        {
            Scheduler = scheduler;
            Time = time;
            AcceptedCsv = acceptedCsv;
            CompletedDir = completedDir;
            Marks = marks;
            _root = root;
            _executionTask = executionTask;
            _executionCts = executionCts;
            _schedulerTask = scheduler.RunAsync(_schedulerCts.Token);
        }

        /// <summary>
        /// Scheduler under test; it runs the loop and execution workers this harness starts against the one-row
        /// Accepted CSV.
        /// </summary>
        public InMemoryJobScheduler Scheduler { get; }

        /// <summary>
        /// Fake clock shared by the scheduler and executor; tests advance it to make jobs eligible and to release
        /// retry delays.
        /// </summary>
        public FakeTimeProvider Time { get; }

        /// <summary>
        /// Path of the seeded one-row Accepted CSV whose result columns the tests read back.
        /// </summary>
        public string AcceptedCsv { get; }

        /// <summary>
        /// Completed folder inside the temporary jobs tree; the finished CSV is archived here, so tests can tell
        /// whether the import finished.
        /// </summary>
        public string CompletedDir { get; }

        /// <summary>
        /// Timeline marks recorded by the observing connection factory and the upgrade stub; tests compare their
        /// order to prove each connect happens before the upgrade starts.
        /// </summary>
        public TimelineMarks Marks { get; }

        /// <summary>
        /// Creates the temp jobs tree with a one-row Accepted CSV and starts the scheduler plus execution workers.
        /// </summary>
        /// <param name="time">Fake clock driving scheduling and retries.</param>
        /// <param name="factory">Connection factory wrapped by the observing decorator.</param>
        /// <param name="retryOptions">Retry delays and limits; defaults to 10-minute delays and 10 attempts when <see langword="null"/>.</param>
        /// <param name="upgradeRecordsStart">When true the upgrade executor stamps UpgradeStarted; otherwise a stub is used.</param>
        /// <returns>A started harness; dispose it to stop loops and clean up.</returns>
        public static Task<RetryCsvHarness> CreateAsync(
            FakeTimeProvider time,
            IMikrotikConnectionFactory factory,
            JobRetryOptions? retryOptions = null,
            bool upgradeRecordsStart = false)
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
            File.WriteAllText(
                acceptedCsv,
                """
                DateStart,DateStop,RouterName,RouterIp
                01/01/2026 00:00:00,02/01/2026 00:00:00,edge-a,10.0.0.50
                """,
                Encoding.UTF8);

            TimelineMarks marks = new();
            ImportedCsvPathStore pathStore = new();
            ImportedCsvResultWriter writer = new(pathStore, NullLogger<ImportedCsvResultWriter>.Instance);
            IMikrotikConnectionFactory timedFactory = new ObservingConnectFactory(factory, marks);

            IRouterOsV6UpgradeExecutor upgrades = upgradeRecordsStart
                ? new StartRecordingUpgradeExecutor(writer, marks)
                : new StubUpgradeExecutor(UpgradeOutcome.UpgradeSucceeded);

            RouterJobExecutionQueue queue = new(Options.Create(options));
            InMemoryJobScheduler scheduler = new(
                Options.Create(options),
                paths,
                new FileSystemScheduledJobFileMover(),
                queue,
                time,
                NullLogger<InMemoryJobScheduler>.Instance,
                Options.Create(retryOptions ?? new JobRetryOptions
                {
                    ConnectionRetryDelay = TimeSpan.FromMinutes(10),
                    AuthenticationRetryDelay = TimeSpan.FromMinutes(10),
                    MaximumUnsuccessfulAttempts = 10,
                }),
                csvResultWriter: writer,
                csvPathStore: pathStore);

            MikroTikOptions mikroTik = new()
            {
                Enabled = true,
                Username = "u",
                Password = "p",
                Port = 8728,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                ReconnectInterval = TimeSpan.FromSeconds(30),
            };

            RouterJobExecutor executor = new(
                timedFactory,
                new MikrotikApiClient(timedFactory, Options.Create(mikroTik), NullLogger<MikrotikApiClient>.Instance),
                new AppSettingsRouterCredentialResolver(Options.Create(mikroTik)),
                new DeviceCompatibilityAssessor(
                    new StaticFirmwarePackageCatalog(CreateFirmwareRoot()),
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

            return Task.FromResult(new RetryCsvHarness(
                scheduler,
                time,
                acceptedCsv,
                paths.Completed,
                root,
                execTask,
                execCts,
                marks));
        }

        /// <summary>
        /// Registers a one-row import, schedules it, and binds Accepted path.
        /// </summary>
        public async Task<MikrotikScheduledJob> ScheduleSingleAsync(string ip = "10.0.0.50")
        {
            Guid importId = Guid.NewGuid();
            Scheduler.RegisterImport(importId, 1);
            MikrotikScheduledJob job = new()
            {
                ImportId = importId,
                SourceFileName = "batch.csv",
                RowNumber = 2,
                RouterName = "edge-a",
                RouterIp = IPAddress.Parse(ip),
                DateStartUtc = Time.GetUtcNow().UtcDateTime.AddMinutes(-5),
                DateStopUtc = Time.GetUtcNow().UtcDateTime.AddHours(2),
            };
            await Scheduler.ScheduleAsync(job, CancellationToken.None);
            Scheduler.SetAcceptedPath(importId, AcceptedCsv);
            Scheduler.FinalizeImportActivation(importId, scheduledCount: 1, activationIncomplete: false);
            return job;
        }

        /// <summary>
        /// Reads the Result cell for a row from the Accepted CSV.
        /// </summary>
        /// <param name="rowNumber">1-based CSV row number.</param>
        /// <returns>The cell value, or <see langword="null"/> when the file or row is not available.</returns>
        public string? TryReadResult(int rowNumber) =>
            TryReadColumn(AcceptedCsv, rowNumber, ImportedCsvResultColumns.Result);

        /// <summary>
        /// Reads the Result cell from the Accepted CSV, falling back to the archived copy in Completed.
        /// </summary>
        /// <param name="rowNumber">1-based CSV row number.</param>
        /// <returns>The cell value from whichever file exists, or <see langword="null"/> when neither has the row.</returns>
        public string? TryReadResultFromAcceptedOrCompleted(int rowNumber) =>
            TryReadColumn(AcceptedCsv, rowNumber, ImportedCsvResultColumns.Result)
            ?? TryReadColumn(Path.Combine(CompletedDir, Path.GetFileName(AcceptedCsv)), rowNumber, ImportedCsvResultColumns.Result);

        /// <summary>
        /// Reads the UpgradeStarted cell from the Accepted CSV, falling back to the archived copy in Completed.
        /// </summary>
        /// <param name="rowNumber">1-based CSV row number.</param>
        /// <returns>The cell value from whichever file exists, or <see langword="null"/> when neither has the row.</returns>
        public string? TryReadStartedFromAcceptedOrCompleted(int rowNumber) =>
            TryReadColumn(AcceptedCsv, rowNumber, ImportedCsvResultColumns.UpgradeStarted)
            ?? TryReadColumn(Path.Combine(CompletedDir, Path.GetFileName(AcceptedCsv)), rowNumber, ImportedCsvResultColumns.UpgradeStarted);

        /// <summary>
        /// Reads one CSV cell by parser row number and column header (case-insensitive).
        /// </summary>
        /// <param name="path">CSV file path.</param>
        /// <param name="rowNumber">1-based CSV row number.</param>
        /// <param name="column">Column header name.</param>
        /// <returns>
        /// The cell value; <see langword="null"/> when the file, header, or row is missing;
        /// or empty when the column is not present in the header.
        /// </returns>
        private static string? TryReadColumn(string path, int rowNumber, string column)
        {
            if (!File.Exists(path))
            {
                return null;
            }

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
            if (!csv.Read() || !csv.ReadHeader())
            {
                return null;
            }

            string[] headers = csv.HeaderRecord ?? [];
            int index = Array.FindIndex(headers, h => string.Equals(h, column, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return string.Empty;
            }

            while (csv.Read())
            {
                if (csv.Context.Parser?.Row == rowNumber)
                {
                    return csv.GetField(index) ?? string.Empty;
                }
            }

            return null;
        }

        /// <summary>
        /// Creates a temporary firmware tree containing the RouterOS v6 and v7 packages needed for assessment.
        /// </summary>
        /// <returns>The temporary firmware root directory path.</returns>
        private static string CreateFirmwareRoot()
        {
            string fw = Directory.CreateTempSubdirectory("fw-csv-").FullName;
            Directory.CreateDirectory(Path.Combine(fw, "mikrotik"));
            File.WriteAllBytes(Path.Combine(fw, "mikrotik", "routeros-mipsbe-6.49.23.npk"), [1]);
            File.WriteAllBytes(Path.Combine(fw, "mikrotik", "routeros-7.23.8-mipsbe.npk"), [1]);
            return fw;
        }

        /// <summary>
        /// Runs the requested number of queue-draining workers until cancellation.
        /// </summary>
        /// <param name="queue">Execution queue to drain.</param>
        /// <param name="executor">Executor invoked for each work item.</param>
        /// <param name="completer">Completer notified when a job finishes.</param>
        /// <param name="workerCount">Number of parallel workers to start.</param>
        /// <param name="cancellationToken">Token that stops the workers.</param>
        /// <returns>A task that completes when all workers have stopped.</returns>
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

        /// <summary>
        /// Single worker loop that reads work items from the queue and executes them until cancelled.
        /// </summary>
        /// <param name="queue">Execution queue to read from.</param>
        /// <param name="executor">Executor invoked for each work item.</param>
        /// <param name="completer">Completer notified when a job finishes.</param>
        /// <param name="cancellationToken">Token that stops the worker.</param>
        /// <returns>A task that completes when the worker stops.</returns>
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
            try { Directory.Delete(_root, true); } catch { /* ignore */ }
        }
    }
}
