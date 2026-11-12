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

public sealed class ImportedCsvResultRetryIntegrationTests
{
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

    private sealed class TimelineMarks
    {
        public DateTimeOffset? ConnectObservedAt;
        public DateTimeOffset? UpgradeStartedAt;
    }

    private sealed class StartRecordingUpgradeExecutor(IImportedCsvResultWriter writer, TimelineMarks marks)
        : IRouterOsV6UpgradeExecutor
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

    private sealed class ImmediateSuccessConnectFactory : IMikrotikConnectionFactory
    {
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken) =>
            Task.FromResult((ITikConnection)FakeMikrotikConnections.CreateFullyPopulated());
    }

    private sealed class AlwaysTimeoutConnectFactory : IMikrotikConnectionFactory
    {
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken) =>
            throw new TimeoutException("Connection timeout");
    }

    private sealed class AlwaysAuthFailConnectFactory : IMikrotikConnectionFactory
    {
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken) =>
            throw new TikConnectionLoginRefusedException("invalid user name or password", "api");
    }

    private sealed class ObservingConnectFactory(IMikrotikConnectionFactory inner, TimelineMarks marks)
        : IMikrotikConnectionFactory
    {
        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            marks.ConnectObservedAt = DateTimeOffset.UtcNow;
            return await inner.CreateAsync(connection, cancellationToken);
        }
    }

    private sealed class RetryCsvHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly CancellationTokenSource _schedulerCts = new();
        private readonly Task _schedulerTask;
        private readonly Task _executionTask;
        private readonly CancellationTokenSource _executionCts;

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

        public InMemoryJobScheduler Scheduler { get; }
        public FakeTimeProvider Time { get; }
        public string AcceptedCsv { get; }
        public string CompletedDir { get; }
        public TimelineMarks Marks { get; }

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

        public string? TryReadResult(int rowNumber) =>
            TryReadColumn(AcceptedCsv, rowNumber, ImportedCsvResultColumns.Result);

        public string? TryReadResultFromAcceptedOrCompleted(int rowNumber) =>
            TryReadColumn(AcceptedCsv, rowNumber, ImportedCsvResultColumns.Result)
            ?? TryReadColumn(Path.Combine(CompletedDir, Path.GetFileName(AcceptedCsv)), rowNumber, ImportedCsvResultColumns.Result);

        public string? TryReadStartedFromAcceptedOrCompleted(int rowNumber) =>
            TryReadColumn(AcceptedCsv, rowNumber, ImportedCsvResultColumns.UpgradeStarted)
            ?? TryReadColumn(Path.Combine(CompletedDir, Path.GetFileName(AcceptedCsv)), rowNumber, ImportedCsvResultColumns.UpgradeStarted);

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

        private static string CreateFirmwareRoot()
        {
            string fw = Directory.CreateTempSubdirectory("fw-csv-").FullName;
            Directory.CreateDirectory(Path.Combine(fw, "mikrotik"));
            File.WriteAllBytes(Path.Combine(fw, "mikrotik", "routeros-mipsbe-6.49.23.npk"), [1]);
            File.WriteAllBytes(Path.Combine(fw, "mikrotik", "routeros-7.23.8-mipsbe.npk"), [1]);
            return fw;
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
}
