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
/// Regression: Accepted CSV path must be registered before jobs can dispatch,
/// and concurrent imports must not cross-write results.
/// </summary>
public sealed class ImportedCsvPathRegistrationTests
{
    [Fact]
    public async Task JobsHeldUntilAcceptedPathRegistered_ThenUpgradeStartedPersists()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using PathRaceHarness harness = await PathRaceHarness.CreateAsync(time);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        MikrotikScheduledJob job = harness.CreateJob(importId, rowNumber: 2, ip: "10.0.0.1", name: "Router-A");

        await harness.Scheduler.ScheduleAsync(job, CancellationToken.None);

        // Eligible window is open, but Accepted path is not registered yet → must not dispatch.
        Assert.Equal(0, harness.Scheduler.DispatchedCount);
        Assert.Equal(1, harness.Scheduler.PendingCount);
        Assert.False(harness.PathStore.TryGetAcceptedCsvPath(importId, out _));
        Assert.True(string.IsNullOrEmpty(harness.TryReadStarted(2)));

        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);
        harness.Scheduler.FinalizeImportActivation(importId, scheduledCount: 1, activationIncomplete: false);

        await WaitForAsync(
            () => harness.TryReadResultFromAcceptedOrCompleted(2) == "Success"
                  && !string.IsNullOrEmpty(harness.TryReadStartedFromAcceptedOrCompleted(2)),
            TimeSpan.FromSeconds(8));

        Assert.False(string.IsNullOrWhiteSpace(harness.TryReadStartedFromAcceptedOrCompleted(2)));
        Assert.Equal("Success", harness.TryReadResultFromAcceptedOrCompleted(2));

        await WaitForAsync(() => !harness.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));
        string completed = Path.Combine(harness.CompletedDir, Path.GetFileName(harness.AcceptedCsv));
        Assert.True(File.Exists(completed));
        Assert.Equal("Success", ReadColumn(completed, 2, ImportedCsvResultColumns.Result));
        Assert.False(string.IsNullOrWhiteSpace(ReadColumn(completed, 2, ImportedCsvResultColumns.UpgradeStarted)));
        Assert.False(string.IsNullOrWhiteSpace(ReadColumn(completed, 2, ImportedCsvResultColumns.UpgradeCompleted)));
    }

    [Fact]
    public async Task TerminalConnectionFailure_BeforeUpgrade_PersistsAfterPathRegistration()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using PathRaceHarness harness = await PathRaceHarness.CreateAsync(
            time,
            connectionFactory: new AlwaysTimeoutConnectFactory(),
            retryOptions: new JobRetryOptions
            {
                ConnectionRetryDelay = TimeSpan.FromMinutes(10),
                AuthenticationRetryDelay = TimeSpan.FromMinutes(10),
                MaximumUnsuccessfulAttempts = 0,
            },
            upgradeRecordsStart: false);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        MikrotikScheduledJob job = harness.CreateJob(importId, 2, "10.0.0.9", "fail-a");
        await harness.Scheduler.ScheduleAsync(job, CancellationToken.None);
        Assert.Equal(0, harness.Scheduler.DispatchedCount);

        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);
        harness.Scheduler.FinalizeImportActivation(importId, scheduledCount: 1, activationIncomplete: false);

        await WaitForAsync(
            () => !string.IsNullOrEmpty(harness.TryReadResultFromAcceptedOrCompleted(2)),
            TimeSpan.FromSeconds(8));

        Assert.True(string.IsNullOrEmpty(harness.TryReadStartedFromAcceptedOrCompleted(2)));
        Assert.StartsWith(
            "Failed - Connection",
            harness.TryReadResultFromAcceptedOrCompleted(2)!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DualImports_InterleavedCompletions_DoNotCrossWrite()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await using DualImportHarness harness = await DualImportHarness.CreateAsync(time);

        // Import A: Router-A 10.0.0.1 row2, Router-B 10.0.0.2 row3
        // Import B: Router-C 10.0.0.1 row2 (same IP as A), Router-D 10.0.0.4 row3
        (Guid importA, string csvA) = harness.AcceptImport(
            "import-a.csv",
            """
            DateStart,DateStop,RouterName,RouterIp
            01/01/2026 00:00:00,02/01/2026 00:00:00,Router-A,10.0.0.1
            01/01/2026 00:00:00,02/01/2026 00:00:00,Router-B,10.0.0.2
            """);
        (Guid importB, string csvB) = harness.AcceptImport(
            "import-b.csv",
            """
            DateStart,DateStop,RouterName,RouterIp
            01/01/2026 00:00:00,02/01/2026 00:00:00,Router-C,10.0.0.1
            01/01/2026 00:00:00,02/01/2026 00:00:00,Router-D,10.0.0.4
            """);

        MikrotikScheduledJob a1 = harness.Job(importA, 2, "10.0.0.1", "Router-A");
        MikrotikScheduledJob a2 = harness.Job(importA, 3, "10.0.0.2", "Router-B");
        MikrotikScheduledJob b1 = harness.Job(importB, 2, "10.0.0.1", "Router-C");
        MikrotikScheduledJob b2 = harness.Job(importB, 3, "10.0.0.4", "Router-D");

        // Schedule before path registration would hold; paths already registered via AcceptImport.
        await harness.Scheduler.ScheduleAsync(a1, CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(b1, CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(a2, CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(b2, CancellationToken.None);

        // Interleave start/completion in non-input order via the writer directly under the same
        // path store used by the scheduler (deterministic; no sleeps for ordering).
        DateTimeOffset t0 = time.GetUtcNow();
        await harness.Writer.RecordUpgradeStartedAsync(b2, t0.AddMinutes(1));
        await harness.Writer.RecordUpgradeStartedAsync(a1, t0.AddMinutes(2));
        await harness.Writer.RecordJobCompletedAsync(a2, t0.AddMinutes(3), ScheduledJobTerminalState.Failed, "boom");
        await harness.Writer.RecordJobCompletedAsync(b1, t0.AddMinutes(4), ScheduledJobTerminalState.Succeeded, null);
        await harness.Writer.RecordJobCompletedAsync(b2, t0.AddMinutes(5), ScheduledJobTerminalState.Cancelled, null);
        await harness.Writer.RecordJobCompletedAsync(a1, t0.AddMinutes(6), ScheduledJobTerminalState.Succeeded, null);

        Assert.Equal("Success", ReadColumn(csvA, 2, ImportedCsvResultColumns.Result));
        Assert.StartsWith("Failed", ReadColumn(csvA, 3, ImportedCsvResultColumns.Result), StringComparison.Ordinal);
        Assert.Equal("Router-A", ReadColumn(csvA, 2, "RouterName"));
        Assert.Equal("Router-B", ReadColumn(csvA, 3, "RouterName"));
        Assert.Equal("10.0.0.1", ReadColumn(csvA, 2, "RouterIp"));

        Assert.Equal("Success", ReadColumn(csvB, 2, ImportedCsvResultColumns.Result));
        Assert.Equal("Cancelled", ReadColumn(csvB, 3, ImportedCsvResultColumns.Result));
        Assert.Equal("Router-C", ReadColumn(csvB, 2, "RouterName"));
        Assert.Equal("10.0.0.1", ReadColumn(csvB, 2, "RouterIp"));
        Assert.False(string.IsNullOrEmpty(ReadColumn(csvB, 3, ImportedCsvResultColumns.UpgradeStarted)));

        // No cross-write: A's row2 result is Success (not Cancelled); B's row2 is Success (not Failed).
        Assert.DoesNotContain("Cancelled", ReadColumn(csvA, 2, ImportedCsvResultColumns.Result), StringComparison.Ordinal);
        Assert.DoesNotContain("Failed", ReadColumn(csvB, 2, ImportedCsvResultColumns.Result), StringComparison.Ordinal);

        // Archive each Accepted CSV independently; the other import's file must remain intact.
        FileSystemScheduledJobFileMover mover = new();
        string completedA = mover.MoveSafely(csvA, harness.Paths.Completed, Path.GetFileName(csvA));
        Assert.True(File.Exists(completedA));
        Assert.False(File.Exists(csvA));
        Assert.True(File.Exists(csvB));
        Assert.Equal("Success", ReadColumn(completedA, 2, ImportedCsvResultColumns.Result));
        Assert.Equal("Router-A", ReadColumn(completedA, 2, "RouterName"));

        string completedB = mover.MoveSafely(csvB, harness.Paths.Completed, Path.GetFileName(csvB));
        Assert.True(File.Exists(completedB));
        Assert.False(File.Exists(csvB));
        Assert.Equal("Success", ReadColumn(completedB, 2, ImportedCsvResultColumns.Result));
        Assert.Equal("Router-C", ReadColumn(completedB, 2, "RouterName"));
        Assert.Equal("Cancelled", ReadColumn(completedB, 3, ImportedCsvResultColumns.Result));
    }

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
        Assert.True(index >= 0, $"Column {column} missing in {path}");
        while (csv.Read())
        {
            if (csv.Context.Parser?.Row == rowNumber)
            {
                return csv.GetField(index) ?? string.Empty;
            }
        }

        throw new InvalidOperationException($"Row {rowNumber} not found in {path}");
    }

    private sealed class AlwaysTimeoutConnectFactory : IMikrotikConnectionFactory
    {
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken) =>
            throw new TimeoutException("Connection timeout");
    }

    private sealed class StartRecordingUpgradeExecutor(IImportedCsvResultWriter writer) : IRouterOsV6UpgradeExecutor
    {
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

    private sealed class ImmediateSuccessConnectFactory : IMikrotikConnectionFactory
    {
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken) =>
            Task.FromResult((ITikConnection)FakeMikrotikConnections.CreateFullyPopulated());
    }

    private sealed class PathRaceHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CancellationTokenSource _schedulerCts = new();
        private readonly Task _schedulerTask;
        private readonly Task _executionTask;
        private readonly CancellationTokenSource _executionCts;

        private PathRaceHarness(
            InMemoryJobScheduler scheduler,
            FakeTimeProvider time,
            ImportedCsvPathStore pathStore,
            string acceptedCsv,
            string completedDir,
            string root,
            Task executionTask,
            CancellationTokenSource executionCts)
        {
            Scheduler = scheduler;
            Time = time;
            PathStore = pathStore;
            AcceptedCsv = acceptedCsv;
            CompletedDir = completedDir;
            _root = root;
            _executionTask = executionTask;
            _executionCts = executionCts;
            _schedulerTask = scheduler.RunAsync(_schedulerCts.Token);
        }

        public InMemoryJobScheduler Scheduler { get; }
        public FakeTimeProvider Time { get; }
        public ImportedCsvPathStore PathStore { get; }
        public string AcceptedCsv { get; }
        public string CompletedDir { get; }

        public static Task<PathRaceHarness> CreateAsync(
            FakeTimeProvider time,
            IMikrotikConnectionFactory? connectionFactory = null,
            JobRetryOptions? retryOptions = null,
            bool upgradeRecordsStart = true)
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
                01/01/2026 00:00:00,02/01/2026 00:00:00,Router-A,10.0.0.1
                """,
                Encoding.UTF8);

            ImportedCsvPathStore pathStore = new();
            ImportedCsvResultWriter writer = new(pathStore, NullLogger<ImportedCsvResultWriter>.Instance);
            IMikrotikConnectionFactory factory = connectionFactory ?? new ImmediateSuccessConnectFactory();
            IRouterOsV6UpgradeExecutor upgrades = upgradeRecordsStart
                ? new StartRecordingUpgradeExecutor(writer)
                : new StubUpgradeExecutor(UpgradeOutcome.UpgradeSucceeded);

            RouterJobExecutionQueue queue = new(Options.Create(options));
            InMemoryJobScheduler scheduler = new(
                Options.Create(options),
                paths,
                new FileSystemScheduledJobFileMover(),
                queue,
                time,
                NullLogger<InMemoryJobScheduler>.Instance,
                Options.Create(retryOptions ?? new JobRetryOptions()),
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

            string fw = Directory.CreateTempSubdirectory("fw-path-race-").FullName;
            Directory.CreateDirectory(Path.Combine(fw, "mikrotik"));
            File.WriteAllBytes(Path.Combine(fw, "mikrotik", "routeros-mipsbe-6.49.23.npk"), [1]);
            File.WriteAllBytes(Path.Combine(fw, "mikrotik", "routeros-7.23.8-mipsbe.npk"), [1]);

            RouterJobExecutor executor = new(
                factory,
                new MikrotikApiClient(factory, Options.Create(mikroTik), NullLogger<MikrotikApiClient>.Instance),
                new AppSettingsRouterCredentialResolver(Options.Create(mikroTik)),
                new DeviceCompatibilityAssessor(
                    new StaticFirmwarePackageCatalog(fw),
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

            return Task.FromResult(new PathRaceHarness(
                scheduler,
                time,
                pathStore,
                acceptedCsv,
                paths.Completed,
                root,
                execTask,
                execCts));
        }

        public MikrotikScheduledJob CreateJob(Guid importId, int rowNumber, string ip, string name) =>
            new()
            {
                ImportId = importId,
                SourceFileName = Path.GetFileName(AcceptedCsv),
                RowNumber = rowNumber,
                RouterName = name,
                RouterIp = IPAddress.Parse(ip),
                DateStartUtc = Time.GetUtcNow().UtcDateTime.AddMinutes(-5),
                DateStopUtc = Time.GetUtcNow().UtcDateTime.AddHours(2),
            };

        public string? TryReadStarted(int row) => TryRead(AcceptedCsv, row, ImportedCsvResultColumns.UpgradeStarted);
        public string? TryReadCompleted(int row) => TryRead(AcceptedCsv, row, ImportedCsvResultColumns.UpgradeCompleted);
        public string? TryReadResult(int row) => TryRead(AcceptedCsv, row, ImportedCsvResultColumns.Result);

        public string? TryReadResultFromAcceptedOrCompleted(int row) =>
            TryRead(AcceptedCsv, row, ImportedCsvResultColumns.Result)
            ?? TryRead(Path.Combine(CompletedDir, Path.GetFileName(AcceptedCsv)), row, ImportedCsvResultColumns.Result);

        public string? TryReadStartedFromAcceptedOrCompleted(int row) =>
            TryRead(AcceptedCsv, row, ImportedCsvResultColumns.UpgradeStarted)
            ?? TryRead(Path.Combine(CompletedDir, Path.GetFileName(AcceptedCsv)), row, ImportedCsvResultColumns.UpgradeStarted);

        private static string? TryRead(string path, int rowNumber, string column)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return ReadColumn(path, rowNumber, column);
            }
            catch
            {
                return string.Empty;
            }
        }

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

    private sealed class DualImportHarness : IAsyncDisposable
    {
        private readonly string _root;

        private DualImportHarness(
            InMemoryJobScheduler scheduler,
            ImportedCsvResultWriter writer,
            ImportedCsvPathStore pathStore,
            ScheduledJobsPaths paths,
            FakeTimeProvider time,
            string root)
        {
            Scheduler = scheduler;
            Writer = writer;
            PathStore = pathStore;
            Paths = paths;
            Time = time;
            _root = root;
        }

        public InMemoryJobScheduler Scheduler { get; }
        public ImportedCsvResultWriter Writer { get; }
        public ImportedCsvPathStore PathStore { get; }
        public ScheduledJobsPaths Paths { get; }
        public FakeTimeProvider Time { get; }

        public static Task<DualImportHarness> CreateAsync(FakeTimeProvider time)
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
                csvResultWriter: writer,
                csvPathStore: pathStore);

            return Task.FromResult(new DualImportHarness(scheduler, writer, pathStore, paths, time, root));
        }

        public (Guid ImportId, string CsvPath) AcceptImport(string fileName, string csvContent)
        {
            Guid importId = Guid.NewGuid();
            string csvPath = Path.Combine(Paths.Accepted, fileName);
            File.WriteAllText(csvPath, csvContent, Encoding.UTF8);
            int rows = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length - 1;
            Scheduler.RegisterImport(importId, rows);
            Scheduler.SetAcceptedPath(importId, csvPath);
            return (importId, csvPath);
        }

        public MikrotikScheduledJob Job(Guid importId, int row, string ip, string name) =>
            new()
            {
                ImportId = importId,
                SourceFileName = "x.csv",
                RowNumber = row,
                RouterName = name,
                RouterIp = IPAddress.Parse(ip),
                DateStartUtc = Time.GetUtcNow().UtcDateTime.AddMinutes(-1),
                DateStopUtc = Time.GetUtcNow().UtcDateTime.AddHours(1),
            };

        public async ValueTask DisposeAsync()
        {
            await Scheduler.DisposeAsync();
            try { Directory.Delete(_root, true); } catch { /* ignore */ }
        }
    }
}
