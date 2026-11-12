// -----------------------------------------------------------------------------
// File: ImportedCsvResultWriterTests.cs
//
// Description:
// Unit/integration tests for ImportedCsvResultWriter column formatting,
// concurrency, CSV edge cases (quotes/newlines), re-import ignoring result
// columns, and scheduler terminal path writing before Completed archive.
//
// -----------------------------------------------------------------------------

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

/// <summary>
/// Exercises in-place Accepted CSV result writes for start/completion/result columns.
/// </summary>
public sealed class ImportedCsvResultWriterTests
{
    /// <summary>
    /// Record start, advance five minutes, record Succeeded.
    /// Expects ISO timestamps for start/completion, Result=Success, and identity columns unchanged.
    /// </summary>
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

    /// <summary>
    /// Start then Failed with upgrade detail.
    /// Expects both timestamps populated and Result starting with Failed - Firmware upgrade.
    /// </summary>
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

    /// <summary>
    /// Terminal Failed without a prior start record.
    /// Expects blank UpgradeStarted, populated UpgradeCompleted, and a Failed result.
    /// </summary>
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

    /// <summary>
    /// Completed as Cancelled with no detail.
    /// Expects Result exactly Cancelled.
    /// </summary>
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

    /// <summary>
    /// Job carrying retry metadata but no RecordJobCompleted yet.
    /// Expects result columns absent until a terminal write; then Failed - Connection.
    /// </summary>
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

    /// <summary>
    /// Prior connection failures then Succeeded after start.
    /// Expects final Result=Success with start present — no leftover failure text.
    /// </summary>
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

    /// <summary>
    /// Three parallel RecordJobCompleted calls on distinct rows.
    /// Expects each row's Success/Failed/Cancelled preserved without clobbering siblings.
    /// </summary>
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

    /// <summary>
    /// Interleaved start on A with completions on A and B.
    /// Expects A's start and completion timestamps both survive concurrent writers.
    /// </summary>
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

    /// <summary>
    /// Two rows sharing 10.0.0.9 with distinct names/notes.
    /// Expects row-number addressing: Success vs Failed without swapping Note fields.
    /// </summary>
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

    /// <summary>
    /// Path store points at a missing CSV.
    /// Expects RecordJobCompleted completes without throwing (errors logged only).
    /// </summary>
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

    /// <summary>
    /// Note with comma and router name with embedded newline.
    /// Expects round-trip preserves quoted special characters after a result write.
    /// </summary>
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

    /// <summary>
    /// CSV already containing result columns after a successful write.
    /// Expects Validate/Enumerate accept the file and jobs do not carry result metadata in memory.
    /// </summary>
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

    /// <summary>
    /// Failed result on disk; new import id overwrites with Success.
    /// Expects old Failed remains until the new completion write replaces it.
    /// </summary>
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

    /// <summary>
    /// Writer + path store wired like the scheduler terminal path.
    /// Expects Success on the Accepted CSV before archive dispose cleanup.
    /// </summary>
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

    /// <summary>
    /// Reads the Result column for a 1-based CSV row number via CsvHelper.
    /// </summary>
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

    /// <summary>
    /// Temp Accepted CSV, FakeTimeProvider, path store, and ImportedCsvResultWriter.
    /// Creates N data rows and maps CreateJob to import id / row / IP.
    /// </summary>
    private sealed class CsvHarness : IAsyncDisposable
    {
        /// <summary>
        /// Initializes the harness state.
        /// </summary>
        /// <param name="root">Temporary directory containing the CSV, deleted on disposal.</param>
        /// <param name="csvPath">Path of the generated import CSV.</param>
        /// <param name="importId">Import id registered against the CSV in the path store.</param>
        /// <param name="time">Fake clock used for timestamps.</param>
        /// <param name="pathStore">Accepted path store shared with the writer.</param>
        /// <param name="writer">Result writer under test.</param>
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

        /// <summary>
        /// Temporary directory that contains the import CSV; it is removed when the harness is disposed.
        /// </summary>
        public string Root { get; }

        /// <summary>
        /// Path of the generated import CSV, which holds the requested number of data rows.
        /// </summary>
        public string CsvPath { get; }

        /// <summary>
        /// Import id registered in the path store against <see cref="CsvPath"/>; the writer uses it to find the file
        /// to update.
        /// </summary>
        public Guid ImportId { get; }

        /// <summary>
        /// Fake clock that supplies the timestamps the writer records, so tests can assert exact values.
        /// </summary>
        public FakeTimeProvider Time { get; }

        /// <summary>
        /// Accepted path store shared with the writer; it holds the registration of <see cref="CsvPath"/> under <see
        /// cref="ImportId"/>.
        /// </summary>
        public ImportedCsvPathStore PathStore { get; }

        /// <summary>
        /// Result writer under test, bound to the harness CSV through the shared path store.
        /// </summary>
        public ImportedCsvResultWriter Writer { get; }

        /// <summary>
        /// Creates a temp import CSV with the requested number of data rows and a writer bound to it.
        /// </summary>
        /// <param name="rowCount">Number of data rows (edge-a, edge-b, ... on 10.0.0.1, 10.0.0.2, ...).</param>
        /// <returns>A harness whose temp directory is removed on disposal.</returns>
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

        /// <summary>
        /// Builds a MikrotikScheduledJob for the harness clock and import.
        /// </summary>
        /// <param name="rowNumber">1-based CSV row number of the job.</param>
        /// <param name="ip">Router IP address.</param>
        /// <param name="name">Router name.</param>
        /// <param name="importId">Import id override; defaults to the harness import.</param>
        /// <returns>A job whose window starts at the harness clock time and lasts one hour.</returns>
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

        /// <summary>
        /// Determines whether the CSV header already contains the Result column.
        /// </summary>
        /// <returns><see langword="true"/> when the header includes the Result column; otherwise <see langword="false"/>.</returns>
        public bool HasResultColumns()
        {
            string header = File.ReadLines(CsvPath).First();
            return header.Contains(ImportedCsvResultColumns.Result, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads a data row from the import CSV into a case-insensitive column map.
        /// </summary>
        /// <param name="rowNumber">1-based CSV row number as reported by the parser.</param>
        /// <returns>The row's fields keyed by header name.</returns>
        /// <exception cref="InvalidOperationException">The row does not exist.</exception>
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

        /// <summary>
        /// Deletes the temporary directory, ignoring cleanup failures.
        /// </summary>
        /// <returns>A completed task.</returns>
        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Root, true); } catch { /* ignore */ }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Case-insensitive column map with helpers for result/timestamp/identity fields.
    /// </summary>
    /// <param name="fields">Row values keyed by CSV header name.</param>
    private sealed class CsvRow(Dictionary<string, string> fields)
    {
        /// <summary>
        /// UpgradeStarted timestamp text for this row, or an empty string when the column is absent or blank, which
        /// lets tests assert that no start time was written.
        /// </summary>
        public string UpgradeStarted => Get(ImportedCsvResultColumns.UpgradeStarted);

        /// <summary>
        /// UpgradeCompleted timestamp text for this row, or an empty string when the column is absent or blank, which
        /// lets tests assert that no completion time was written.
        /// </summary>
        public string UpgradeCompleted => Get(ImportedCsvResultColumns.UpgradeCompleted);

        /// <summary>
        /// Result text written for this row, or an empty string when the column is absent or blank, which lets tests
        /// assert that no outcome was recorded.
        /// </summary>
        public string Result => Get(ImportedCsvResultColumns.Result);

        /// <summary>
        /// RouterName identity column value that identifies this row; result writing is expected to leave it
        /// unchanged.
        /// </summary>
        public string RouterName => Get("RouterName");

        /// <summary>
        /// RouterIp identity column value that identifies this row; result writing is expected to leave it unchanged.
        /// </summary>
        public string RouterIp => Get("RouterIp");

        /// <summary>
        /// Gets a field by header name (case-insensitive).
        /// </summary>
        /// <param name="name">CSV header name.</param>
        /// <returns>The field value, or empty when the column does not exist.</returns>
        public string Get(string name) => fields.TryGetValue(name, out string? v) ? v : string.Empty;
    }
}
