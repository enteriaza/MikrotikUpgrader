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
/// Activation/cancellation lifecycle: reconcile Remaining, retire incomplete imports without
/// Completed archival, keep path while scheduled jobs remain outstanding.
/// </summary>
public sealed class ImportedCsvActivationCancellationTests
{
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

    private sealed class FailOnceThenSucceedFactory : IMikrotikConnectionFactory
    {
        private int _attempts;

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

    private sealed class GateConnectionFactory : IMikrotikConnectionFactory
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _blockers = [];
        private int _active;

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

    private sealed class ActivationHarness : IAsyncDisposable
    {
        private readonly string _firmwareRoot;
        private readonly CancellationTokenSource _schedulerCts = new();
        private readonly CancellationTokenSource _executionCts;
        private readonly Task _schedulerTask;
        private readonly Task _executionTask;

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

        public InMemoryJobScheduler Scheduler { get; }
        public ScheduledJobFileProcessor Processor { get; }
        public ImportedCsvPathStore PathStore { get; }
        public ScheduledJobsPaths Paths { get; }
        public FakeTimeProvider Time { get; }
        public string JobsRoot { get; }
        public int RowCount { get; }
        public string AcceptedDir => Paths.Accepted;
        public string CompletedDir => Paths.Completed;
        public string RejectedDir => Paths.Rejected;

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

        public string WriteIncomingCsv(string fileName, int? rowCount = null, bool activeWindow = false)
        {
            int rows = rowCount ?? RowCount;
            string path = Path.Combine(JobsRoot, fileName);
            File.WriteAllText(path, BuildCsv(rows, activeWindow), Encoding.UTF8);
            return path;
        }

        public string SeedAcceptedCsv(Guid importId, string fileName, int rows)
        {
            _ = importId;
            string path = Path.Combine(AcceptedDir, fileName);
            File.WriteAllText(path, BuildCsv(rows, activeWindow: true), Encoding.UTF8);
            return path;
        }

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
            try { Directory.Delete(JobsRoot, true); } catch { /* ignore */ }
            try { Directory.Delete(_firmwareRoot, true); } catch { /* ignore */ }
        }
    }

    private sealed class ImmediateSuccessConnectFactory : IMikrotikConnectionFactory
    {
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken) =>
            Task.FromResult((ITikConnection)FakeMikrotikConnections.CreateFullyPopulated());
    }
}
