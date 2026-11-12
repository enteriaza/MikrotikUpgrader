// -----------------------------------------------------------------------------
// File: ImportedCsvResultWriter.cs
//
// Description:
// Thread-safe in-place updater for Accepted CSV result columns (UpgradeStarted,
// UpgradeCompleted, Result). Resolves paths via <see cref="IImportedCsvPathResolver"/>,
// serializes writers with a semaphore, writes through a temp file, and never lets
// I/O failures change in-memory job outcomes. Also defines the path-resolver contract.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MikrotikWorker.Logging;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Thread-safe in-place updater for the imported Accepted CSV identified by import path + row number.
/// </summary>
/// <param name="pathResolver">Resolves Accepted CSV path for an import id.</param>
/// <param name="logger">Service-only logging for skip/error/success.</param>
internal sealed class ImportedCsvResultWriter(
    IImportedCsvPathResolver pathResolver,
    ILogger<ImportedCsvResultWriter> logger) : IImportedCsvResultWriter
{
    /// <summary>
    /// Serializes result updates so concurrent jobs never rewrite the same CSV at once.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public async Task RecordUpgradeStartedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await ApplyAsync(
                job,
                startedAt: ImportedCsvResultColumns.FormatTimestamp(startedAt),
                completedAt: null,
                result: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordJobCompletedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset completedAt,
        ScheduledJobTerminalState state,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await ApplyAsync(
                job,
                startedAt: null,
                completedAt: ImportedCsvResultColumns.FormatTimestamp(completedAt),
                result: ImportedCsvResultColumns.FormatResult(state, detail, job),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the Accepted path, takes the writer gate, and updates the matching row.
    /// Missing path is a logged no-op; other failures are logged without rethrowing (except cancel).
    /// </summary>
    private async Task ApplyAsync(
        MikrotikScheduledJob job,
        string? startedAt,
        string? completedAt,
        string? result,
        CancellationToken cancellationToken)
    {
        if (!pathResolver.TryGetAcceptedCsvPath(job.ImportId, out string? csvPath) || csvPath is null)
        {
            using (DeviceLogging.BeginServiceOnly())
            {
                logger.LogWarning(
                    "CSV result update skipped; Accepted CSV path is not available. ImportId={ImportId}, JobId={JobId}, RowNumber={RowNumber}",
                    job.ImportId,
                    job.JobId,
                    job.RowNumber);
            }

            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UpdateFileAsync(csvPath, job, startedAt, completedAt, result, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            using (DeviceLogging.BeginServiceOnly())
            {
                logger.LogError(
                    ex,
                    "Failed to persist router result to imported CSV. ImportId={ImportId}, JobId={JobId}, RowNumber={RowNumber}, Path={Path}. In-memory job outcome is unchanged.",
                    job.ImportId,
                    job.JobId,
                    job.RowNumber,
                    csvPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the CSV, ensures result headers, patches the job's row, and atomically replaces via <c>.results.tmp</c>.
    /// </summary>
    private async Task UpdateFileAsync(
        string csvPath,
        MikrotikScheduledJob job,
        string? startedAt,
        string? completedAt,
        string? result,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(csvPath))
        {
            throw new FileNotFoundException("Imported Accepted CSV was not found.", csvPath);
        }

        CsvDocument document = await ReadDocumentAsync(csvPath, cancellationToken).ConfigureAwait(false);
        EnsureResultHeaders(document);

        CsvDataRow? row = document.Rows.FirstOrDefault(r => r.FileRowNumber == job.RowNumber);
        if (row is null)
        {
            throw new InvalidOperationException(
                $"CSV row {job.RowNumber} was not found in '{csvPath}' for ImportId={job.ImportId:N}.");
        }

        if (startedAt is not null)
        {
            row.Set(ImportedCsvResultColumns.UpgradeStarted, startedAt);
        }

        if (completedAt is not null)
        {
            row.Set(ImportedCsvResultColumns.UpgradeCompleted, completedAt);
        }

        if (result is not null)
        {
            row.Set(ImportedCsvResultColumns.Result, result);
        }

        string tempPath = csvPath + ".results.tmp";
        try
        {
            await WriteDocumentAsync(tempPath, document, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, csvPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        using (DeviceLogging.BeginServiceOnly())
        {
            logger.LogInformation(
                "Persisted CSV result columns. ImportId={ImportId}, JobId={JobId}, RowNumber={RowNumber}, Path={Path}, UpgradeStartedSet={UpgradeStartedSet}, CompletedSet={CompletedSet}",
                job.ImportId,
                job.JobId,
                job.RowNumber,
                csvPath,
                startedAt is not null,
                completedAt is not null);
        }
    }

    /// <summary>Reads header + data rows with CsvHelper; file row numbers come from the parser context.</summary>
    private static async Task<CsvDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using CsvReader csv = new(reader, CreateConfiguration());

        if (!await csv.ReadAsync().ConfigureAwait(false) || !csv.ReadHeader())
        {
            throw new InvalidOperationException($"CSV header is missing in '{path}'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string[] headerRecord = csv.HeaderRecord
            ?? throw new InvalidOperationException($"CSV header is missing in '{path}'.");

        List<string> headers = [.. headerRecord];
        List<CsvDataRow> rows = [];
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int fileRowNumber = csv.Context.Parser?.Row
                ?? throw new InvalidOperationException("CSV parser did not report a row number.");

            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Count; i++)
            {
                fields[headers[i]] = csv.GetField(i) ?? string.Empty;
            }

            rows.Add(new CsvDataRow(fileRowNumber, fields));
        }

        return new CsvDocument(headers, rows);
    }

    /// <summary>Writes the in-memory document as UTF-8 CSV without a BOM.</summary>
    private static async Task WriteDocumentAsync(
        string path,
        CsvDocument document,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await using CsvWriter csv = new(writer, CreateConfiguration());

        foreach (string header in document.Headers)
        {
            csv.WriteField(header);
        }

        await csv.NextRecordAsync().ConfigureAwait(false);

        foreach (CsvDataRow row in document.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string header in document.Headers)
            {
                csv.WriteField(row.Get(header));
            }

            await csv.NextRecordAsync().ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends any missing result columns to the header list (case-insensitive check).</summary>
    private static void EnsureResultHeaders(CsvDocument document)
    {
        foreach (string column in ImportedCsvResultColumns.All)
        {
            bool exists = document.Headers.Any(h => string.Equals(h, column, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                document.Headers.Add(column);
            }
        }
    }

    /// <summary>CsvHelper settings: invariant culture, no trim, permissive missing/bad-data callbacks.</summary>
    private static CsvConfiguration CreateConfiguration() =>
        new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.None,
            DetectDelimiter = false,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
        };

    /// <summary>Best-effort delete of a temp path; swallows all failures.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    /// <summary>In-memory CSV header + row model used for rewrite.</summary>
    /// <param name="headers">Ordered header names.</param>
    /// <param name="rows">Data rows with original file row numbers.</param>
    private sealed class CsvDocument(List<string> headers, List<CsvDataRow> rows)
    {
        /// <summary>Ordered header names (may grow when result columns are appended).</summary>
        public List<string> Headers { get; } = headers;

        /// <summary>Data rows keyed for lookup by <see cref="CsvDataRow.FileRowNumber"/>.</summary>
        public List<CsvDataRow> Rows { get; } = rows;
    }

    /// <summary>One CSV data row with case-insensitive field map.</summary>
    /// <param name="fileRowNumber">Parser row number matching <see cref="MikrotikScheduledJob.RowNumber"/>.</param>
    /// <param name="fields">Header→value map.</param>
    private sealed class CsvDataRow(int fileRowNumber, Dictionary<string, string> fields)
    {
        /// <summary>Original 1-based file row number from CsvHelper.</summary>
        public int FileRowNumber { get; } = fileRowNumber;

        /// <summary>Returns the field for <paramref name="header"/>, or empty when absent.</summary>
        public string Get(string header) =>
            fields.TryGetValue(header, out string? value) ? value : string.Empty;

        /// <summary>Sets or overwrites the field for <paramref name="header"/>.</summary>
        public void Set(string header, string value) => fields[header] = value;
    }
}

/// <summary>
/// Resolves the on-disk Accepted CSV path for an import.
/// </summary>
internal interface IImportedCsvPathResolver
{
    /// <summary>
    /// Looks up the Accepted CSV path registered for <paramref name="importId"/>.
    /// </summary>
    /// <param name="importId">Import whose path is requested.</param>
    /// <param name="path">Full path when found; otherwise null.</param>
    /// <returns><c>true</c> when a path is registered.</returns>
    bool TryGetAcceptedCsvPath(Guid importId, out string? path);
}
