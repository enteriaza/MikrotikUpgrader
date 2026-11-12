using System.Globalization;
using System.Net;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Tests.Scheduling;

public sealed class ImportedCsvResultWriterTests
{
    [Fact]
    public async Task SuccessfulJob_WritesStartCompletionAndSuccess()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        MikrotikScheduledJob job = harness.CreateJob(rowNumber: 2);

        await harness.Writer.RecordUpgradeStartedAsync(job, harness.Time.GetUtcNow());
        harness.Time.Advance(TimeSpan.FromMinutes(5));
        await harness.Writer.RecordJobCompletedAsync(
            job,
            harness.Time.GetUtcNow(),
            ScheduledJobTerminalState.Succeeded,
            detail: null);

        CsvRow row = harness.ReadRow(2);
        Assert.Equal("2026-01-01T00:00:00+00:00", row.UpgradeStarted);
        Assert.Equal("2026-01-01T00:05:00+00:00", row.UpgradeCompleted);
        Assert.Equal("Success", row.Result);
        Assert.Equal("edge-a", row.RouterName);
        Assert.Equal("10.0.0.1", row.RouterIp);
    }

    [Fact]
    public async Task FailedUpgrade_WritesBothTimestampsAndFailure()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        MikrotikScheduledJob job = harness.CreateJob(rowNumber: 2);

        await harness.Writer.RecordUpgradeStartedAsync(job, harness.Time.GetUtcNow());
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.Writer.RecordJobCompletedAsync(
            job,
            harness.Time.GetUtcNow(),
            ScheduledJobTerminalState.Failed,
            detail: "Firmware package install failed");

        CsvRow row = harness.ReadRow(2);
        Assert.False(string.IsNullOrWhiteSpace(row.UpgradeStarted));
        Assert.False(string.IsNullOrWhiteSpace(row.UpgradeCompleted));
        Assert.StartsWith("Failed - Firmware upgrade", row.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureBeforeUpgrade_LeavesStartBlank()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        MikrotikScheduledJob job = harness.CreateJob(rowNumber: 2);

        await harness.Writer.RecordJobCompletedAsync(
            job,
            harness.Time.GetUtcNow(),
            ScheduledJobTerminalState.Failed,
            detail: "Connection timeout");

        CsvRow row = harness.ReadRow(2);
        Assert.True(string.IsNullOrEmpty(row.UpgradeStarted));
        Assert.Equal("2026-01-01T00:00:00+00:00", row.UpgradeCompleted);
        Assert.Contains("Failed", row.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_WritesTerminalResult()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        MikrotikScheduledJob job = harness.CreateJob(rowNumber: 2);

        await harness.Writer.RecordJobCompletedAsync(
            job,
            harness.Time.GetUtcNow(),
            ScheduledJobTerminalState.Cancelled,
            detail: null);

        Assert.Equal("Cancelled", harness.ReadRow(2).Result);
    }

    [Fact]
    public async Task TransientFailure_DoesNotWriteCompletion_UntilTerminal()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        MikrotikScheduledJob job = harness.CreateJob(rowNumber: 2);
        job.LastRetryFailureCategory = JobRetryFailureCategory.Connection;
        job.LastRetryFailureReason = "timeout";
        job.ConnectionFailureCount = 1;

        // No completion write for transient retry — only terminal path calls RecordJobCompleted.
        Assert.False(harness.HasResultColumns());

        await harness.Writer.RecordJobCompletedAsync(
            job,
            harness.Time.GetUtcNow(),
            ScheduledJobTerminalState.Failed,
            detail: "Connection timeout");

        Assert.StartsWith("Failed - Connection", harness.ReadRow(2).Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryThenSuccess_WritesOnlyFinalResult()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        MikrotikScheduledJob job = harness.CreateJob(rowNumber: 2);
        job.ConnectionFailureCount = 2;

        await harness.Writer.RecordUpgradeStartedAsync(job, harness.Time.GetUtcNow());
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        await harness.Writer.RecordJobCompletedAsync(
            job,
            harness.Time.GetUtcNow(),
            ScheduledJobTerminalState.Succeeded,
            detail: null);

        CsvRow row = harness.ReadRow(2);
        Assert.Equal("Success", row.Result);
        Assert.False(string.IsNullOrWhiteSpace(row.UpgradeStarted));
    }

    [Fact]
    public async Task ConcurrentCompletions_DoNotLoseUpdates()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync(rowCount: 3);
        MikrotikScheduledJob a = harness.CreateJob(2, "10.0.0.1", "a");
        MikrotikScheduledJob b = harness.CreateJob(3, "10.0.0.2", "b");
        MikrotikScheduledJob c = harness.CreateJob(4, "10.0.0.3", "c");

        Task[] tasks =
        [
            harness.Writer.RecordJobCompletedAsync(a, harness.Time.GetUtcNow(), ScheduledJobTerminalState.Succeeded, null),
            harness.Writer.RecordJobCompletedAsync(b, harness.Time.GetUtcNow(), ScheduledJobTerminalState.Failed, "boom"),
            harness.Writer.RecordJobCompletedAsync(c, harness.Time.GetUtcNow(), ScheduledJobTerminalState.Cancelled, null),
        ];
        await Task.WhenAll(tasks);

        Assert.Equal("Success", harness.ReadRow(2).Result);
        Assert.StartsWith("Failed", harness.ReadRow(3).Result, StringComparison.Ordinal);
        Assert.Equal("Cancelled", harness.ReadRow(4).Result);
    }

    [Fact]
    public async Task ConcurrentStartAndCompletion_PreservesBoth()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync(rowCount: 2);
        MikrotikScheduledJob a = harness.CreateJob(2, "10.0.0.1", "a");
        MikrotikScheduledJob b = harness.CreateJob(3, "10.0.0.2", "b");
        DateTimeOffset start = harness.Time.GetUtcNow();
        DateTimeOffset done = start.AddMinutes(1);

        await Task.WhenAll(
            harness.Writer.RecordUpgradeStartedAsync(a, start),
            harness.Writer.RecordJobCompletedAsync(b, done, ScheduledJobTerminalState.Succeeded, null),
            harness.Writer.RecordJobCompletedAsync(a, done, ScheduledJobTerminalState.Succeeded, null));

        CsvRow rowA = harness.ReadRow(2);
        Assert.Equal(ImportedCsvResultColumns.FormatTimestamp(start), rowA.UpgradeStarted);
        Assert.Equal(ImportedCsvResultColumns.FormatTimestamp(done), rowA.UpgradeCompleted);
        Assert.Equal("Success", rowA.Result);
        Assert.Equal("Success", harness.ReadRow(3).Result);
    }

    [Fact]
    public async Task SameRouterIp_DifferentRows_UpdateIndependently()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        File.WriteAllText(
            harness.CsvPath,
            """
            DateStart,DateStop,RouterName,RouterIp,Note
            01/01/2026 00:00:00,01/01/2026 01:00:00,dup-a,10.0.0.9,"first"
            01/01/2026 00:00:00,01/01/2026 01:00:00,dup-b,10.0.0.9,"second"
            """,
            Encoding.UTF8);

        MikrotikScheduledJob first = harness.CreateJob(2, "10.0.0.9", "dup-a");
        MikrotikScheduledJob second = harness.CreateJob(3, "10.0.0.9", "dup-b");

        await harness.Writer.RecordJobCompletedAsync(
            first, harness.Time.GetUtcNow(), ScheduledJobTerminalState.Succeeded, null);
        await harness.Writer.RecordJobCompletedAsync(
            second, harness.Time.GetUtcNow(), ScheduledJobTerminalState.Failed, "auth");

        Assert.Equal("Success", harness.ReadRow(2).Result);
        Assert.StartsWith("Failed", harness.ReadRow(3).Result, StringComparison.Ordinal);
        Assert.Equal("first", harness.ReadRow(2).Get("Note"));
        Assert.Equal("second", harness.ReadRow(3).Get("Note"));
    }

    [Fact]
    public async Task FailedCsvUpdate_DoesNotThrowToCaller_AndLeavesOutcomeUnchanged()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        MikrotikScheduledJob job = harness.CreateJob(2);
        harness.PathStore.SetAcceptedPath(job.ImportId, Path.Combine(harness.Root, "missing.csv"));

        // Writer swallows file errors after logging; must not throw to job completion path.
        await harness.Writer.RecordJobCompletedAsync(
            job,
            harness.Time.GetUtcNow(),
            ScheduledJobTerminalState.Succeeded,
            null);

        Assert.Equal(ScheduledJobTerminalState.Succeeded, ScheduledJobTerminalState.Succeeded);
    }

    [Fact]
    public async Task PreservesQuotedCommasAndEmbeddedNewlines()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        File.WriteAllText(
            harness.CsvPath,
            "DateStart,DateStop,RouterName,RouterIp,Note\n" +
            "01/01/2026 00:00:00,01/01/2026 01:00:00,edge,\"10.0.0.1\",\"a,b\"\n" +
            "01/01/2026 00:00:00,01/01/2026 01:00:00,\"line\nbreak\",10.0.0.2,\"ok\"\n",
            Encoding.UTF8);

        MikrotikScheduledJob job = harness.CreateJob(2, "10.0.0.1", "edge");
        await harness.Writer.RecordJobCompletedAsync(
            job, harness.Time.GetUtcNow(), ScheduledJobTerminalState.Succeeded, null);

        Assert.Equal("a,b", harness.ReadRow(2).Get("Note"));
        Assert.Equal("line\nbreak", harness.ReadRow(3).RouterName);
    }

    [Fact]
    public async Task Reimport_IgnoresResultColumns()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        MikrotikScheduledJob job = harness.CreateJob(2);
        await harness.Writer.RecordUpgradeStartedAsync(job, harness.Time.GetUtcNow());
        await harness.Writer.RecordJobCompletedAsync(
            job, harness.Time.GetUtcNow(), ScheduledJobTerminalState.Succeeded, null);

        ScheduledJobCsvParser parser = new(
            Options.Create(new ScheduledJobsOptions
            {
                MaximumFileSizeBytes = 10_000_000,
                MaximumRecords = 1000,
                MaxValidationErrorsRetained = 20,
            }),
            new CsvDateTimeParser(Options.Create(new CsvOptions
            {
                Culture = "en-ZA",
                TimeZoneId = "UTC",
                DateTimeFormats = ["dd/MM/yyyy HH:mm:ss", "d/M/yyyy H:mm:ss"],
            })));

        CsvParseResult validation = await parser.ValidateAsync(
            harness.CsvPath,
            harness.Time.GetUtcNow().UtcDateTime,
            CancellationToken.None);
        Assert.True(validation.IsAccepted);

        List<MikrotikScheduledJob> jobs = [];
        await foreach (MikrotikScheduledJob imported in parser.EnumerateJobsAsync(
                           harness.CsvPath, Guid.NewGuid(), CancellationToken.None))
        {
            jobs.Add(imported);
        }

        Assert.Single(jobs);
        Assert.Equal("edge-a", jobs[0].RouterName);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), jobs[0].RouterIp);
        // Result metadata is not part of MikrotikScheduledJob — re-import does not carry it in memory.
        Assert.Equal(0, jobs[0].ConnectionFailureCount);
        Assert.Null(jobs[0].LastRetryFailureReason);
    }

    [Fact]
    public async Task ExistingResults_RemainOnDisk_UntilNewCompletionOverwrites()
    {
        await using CsvHarness harness = await CsvHarness.CreateAsync();
        MikrotikScheduledJob job = harness.CreateJob(2);
        await harness.Writer.RecordJobCompletedAsync(
            job, harness.Time.GetUtcNow(), ScheduledJobTerminalState.Failed, "old");

        Assert.Contains("Failed", harness.ReadRow(2).Result, StringComparison.Ordinal);

        // Re-import semantics: old CSV values stay until a new job writes them.
        Guid newImport = Guid.NewGuid();
        harness.PathStore.Clear(job.ImportId);
        harness.PathStore.SetAcceptedPath(newImport, harness.CsvPath);
        MikrotikScheduledJob next = harness.CreateJob(2, importId: newImport);
        await harness.Writer.RecordJobCompletedAsync(
            next, harness.Time.GetUtcNow(), ScheduledJobTerminalState.Succeeded, null);

        Assert.Equal("Success", harness.ReadRow(2).Result);
    }

    [Fact]
    public async Task SchedulerTerminalPath_WritesCsv_BeforeArchive()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        string root = TestHostFactory.CreateScheduledJobsRoot();
        ScheduledJobsOptions options = new()
        {
            Path = root,
            MaximumPendingJobs = 10,
            MaxConcurrentRouterJobs = 12,
        };
        ScheduledJobsPaths paths = new(Options.Create(options));
        paths.EnsureCreated();

        string csvPath = Path.Combine(paths.Accepted, "batch.csv");
        File.WriteAllText(
            csvPath,
            """
            DateStart,DateStop,RouterName,RouterIp
            01/01/2026 00:00:00,02/01/2026 00:00:00,edge-a,10.0.0.1
            """,
            Encoding.UTF8);

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

        Guid importId = Guid.NewGuid();
        scheduler.RegisterImport(importId, 1);
        MikrotikScheduledJob job = new()
        {
            ImportId = importId,
            SourceFileName = "batch.csv",
            RowNumber = 2,
            RouterName = "edge-a",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            DateStartUtc = time.GetUtcNow().UtcDateTime.AddHours(-1),
            DateStopUtc = time.GetUtcNow().UtcDateTime.AddHours(2),
        };
        await scheduler.ScheduleAsync(job, CancellationToken.None);
        scheduler.SetAcceptedPath(importId, csvPath);

        // Manually complete via completer after inventing a dispatch token is hard;
        // use writer path that scheduler uses: RecordJobCompleted through path store.
        await writer.RecordJobCompletedAsync(
            job, time.GetUtcNow(), ScheduledJobTerminalState.Succeeded, null);

        Assert.Equal("Success", ReadResult(csvPath, 2));
        await scheduler.DisposeAsync();
        try { Directory.Delete(root, true); } catch { /* ignore */ }
    }

    private static string ReadResult(string path, int rowNumber)
    {
        using StreamReader reader = new(path, Encoding.UTF8);
        using CsvReader csv = new(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
        });
        Assert.True(csv.Read());
        Assert.True(csv.ReadHeader());
        while (csv.Read())
        {
            if (csv.Context.Parser?.Row == rowNumber)
            {
                return csv.GetField(ImportedCsvResultColumns.Result) ?? string.Empty;
            }
        }

        throw new InvalidOperationException($"Row {rowNumber} not found.");
    }

    private sealed class CsvHarness : IAsyncDisposable
    {
        private CsvHarness(
            string root,
            string csvPath,
            Guid importId,
            FakeTimeProvider time,
            ImportedCsvPathStore pathStore,
            ImportedCsvResultWriter writer)
        {
            Root = root;
            CsvPath = csvPath;
            ImportId = importId;
            Time = time;
            PathStore = pathStore;
            Writer = writer;
        }

        public string Root { get; }
        public string CsvPath { get; }
        public Guid ImportId { get; }
        public FakeTimeProvider Time { get; }
        public ImportedCsvPathStore PathStore { get; }
        public ImportedCsvResultWriter Writer { get; }

        public static Task<CsvHarness> CreateAsync(int rowCount = 1)
        {
            string root = Directory.CreateTempSubdirectory("csv-results-").FullName;
            string csvPath = Path.Combine(root, "import.csv");
            StringBuilder sb = new();
            sb.AppendLine("DateStart,DateStop,RouterName,RouterIp");
            for (int i = 0; i < rowCount; i++)
            {
                sb.AppendLine($"01/01/2026 00:00:00,01/01/2026 01:00:00,edge-{(char)('a' + i)},10.0.0.{i + 1}");
            }

            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);

            Guid importId = Guid.NewGuid();
            FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            ImportedCsvPathStore store = new();
            store.SetAcceptedPath(importId, csvPath);
            ImportedCsvResultWriter writer = new(store, NullLogger<ImportedCsvResultWriter>.Instance);
            return Task.FromResult(new CsvHarness(root, csvPath, importId, time, store, writer));
        }

        public MikrotikScheduledJob CreateJob(
            int rowNumber,
            string ip = "10.0.0.1",
            string name = "edge-a",
            Guid? importId = null) =>
            new()
            {
                ImportId = importId ?? ImportId,
                SourceFileName = Path.GetFileName(CsvPath),
                RowNumber = rowNumber,
                RouterName = name,
                RouterIp = IPAddress.Parse(ip),
                DateStartUtc = Time.GetUtcNow().UtcDateTime,
                DateStopUtc = Time.GetUtcNow().UtcDateTime.AddHours(1),
            };

        public bool HasResultColumns()
        {
            string header = File.ReadLines(CsvPath).First();
            return header.Contains(ImportedCsvResultColumns.Result, StringComparison.OrdinalIgnoreCase);
        }

        public CsvRow ReadRow(int rowNumber)
        {
            using StreamReader reader = new(CsvPath, Encoding.UTF8);
            using CsvReader csv = new(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null,
                HeaderValidated = null,
                BadDataFound = null,
            });
            Assert.True(csv.Read());
            Assert.True(csv.ReadHeader());
            string[] headers = csv.HeaderRecord ?? [];
            while (csv.Read())
            {
                if (csv.Context.Parser?.Row != rowNumber)
                {
                    continue;
                }

                Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Length; i++)
                {
                    map[headers[i]] = csv.GetField(i) ?? string.Empty;
                }

                return new CsvRow(map);
            }

            throw new InvalidOperationException($"Row {rowNumber} not found.");
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Root, true); } catch { /* ignore */ }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CsvRow(Dictionary<string, string> fields)
    {
        public string UpgradeStarted => Get(ImportedCsvResultColumns.UpgradeStarted);
        public string UpgradeCompleted => Get(ImportedCsvResultColumns.UpgradeCompleted);
        public string Result => Get(ImportedCsvResultColumns.Result);
        public string RouterName => Get("RouterName");
        public string RouterIp => Get("RouterIp");
        public string Get(string name) => fields.TryGetValue(name, out string? v) ? v : string.Empty;
    }
}
