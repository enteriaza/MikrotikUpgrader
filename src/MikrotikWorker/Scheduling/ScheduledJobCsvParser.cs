// -----------------------------------------------------------------------------
// File: ScheduledJobCsvParser.cs
//
// Description:
// Validates and enumerates ScheduledJobs CSV files: size/record limits, required
// headers, shared maintenance window rules, IP literals, and date parsing.
// Result columns are allowed and ignored on import; obsolete executionmode is
// rejected. Emits <see cref="CsvParseResult"/> and <see cref="MikrotikScheduledJob"/> streams.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Parses and validates schedule CSVs, then enumerates jobs from an accepted file.
/// </summary>
/// <param name="scheduledJobsOptions">File size, record, and retained-error limits.</param>
/// <param name="dateTimeParser">Culture/time-zone aware date conversion.</param>
internal sealed class ScheduledJobCsvParser(
    IOptions<ScheduledJobsOptions> scheduledJobsOptions,
    CsvDateTimeParser dateTimeParser)
{
    /// <summary>
    /// Lower-case column names every scheduled job CSV must contain.
    /// </summary>
    private static readonly string[] RequiredHeaders = ["datestart", "datestop", "routername", "routerip"];

    /// <summary>
    /// Name of the retired <c>ExecutionMode</c> column, which is rejected with guidance to remove it.
    /// </summary>
    private const string RemovedExecutionModeHeader = "executionmode";

    // Application-owned output columns on the Accepted CSV. Present after jobs finish; ignored on re-import.

    /// <summary>
    /// Full-file validation pass: existence, size, headers, per-row window/IP rules, and shared start/stop.
    /// Does not mutate the file.
    /// </summary>
    /// <param name="filePath">CSV to validate.</param>
    /// <param name="utcNow">Current UTC used to reject already-closed windows.</param>
    /// <param name="cancellationToken">Cancels mid-scan.</param>
    /// <returns>Accepted result with counts/window, or Rejected with retained errors.</returns>
    public async Task<CsvParseResult> ValidateAsync(
        string filePath,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        ScheduledJobsOptions limits = scheduledJobsOptions.Value;
        FileInfo fileInfo = new(filePath);
        string fileName = fileInfo.Name;

        if (!fileInfo.Exists)
        {
            return CsvParseResult.Rejected([
                new CsvValidationError { FileName = fileName, Reason = "File does not exist." },
            ], totalErrorCount: 1);
        }

        if (fileInfo.Length > limits.MaximumFileSizeBytes)
        {
            return CsvParseResult.Rejected([
                new CsvValidationError
                {
                    FileName = fileName,
                    Reason = $"File size {fileInfo.Length} exceeds MaximumFileSizeBytes ({limits.MaximumFileSizeBytes}).",
                },
            ], totalErrorCount: 1);
        }

        ErrorAccumulator errors = new(limits.MaxValidationErrorsRetained);
        int recordCount = 0;
        DateTime? expectedStartUtc = null;
        DateTime? expectedStopUtc = null;

        try
        {
            await using FileStream stream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using StreamReader reader = new(stream);
            using CsvReader csv = CreateReader(reader);

            if (!await csv.ReadAsync().ConfigureAwait(false) || !csv.ReadHeader())
            {
                errors.Add(new CsvValidationError { FileName = fileName, Reason = "CSV header row is missing." });
                return CsvParseResult.Rejected(errors.Retained, errors.TotalCount);
            }

            Dictionary<string, int> headers = BuildHeaderMap(csv, fileName, errors);
            if (errors.TotalCount > 0)
            {
                return CsvParseResult.Rejected(errors.Retained, errors.TotalCount);
            }

            while (await csv.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                recordCount++;

                if (recordCount > limits.MaximumRecords)
                {
                    errors.Add(new CsvValidationError
                    {
                        FileName = fileName,
                        RowNumber = csv.Context.Parser?.Row,
                        Reason = $"Record count exceeds MaximumRecords ({limits.MaximumRecords}).",
                    });
                    break;
                }

                try
                {
                    ParsedCsvRow row = ReadRow(csv, headers, fileName, dateTimeParser);
                    ValidateRow(fileName, csv.Context.Parser?.Row, row, utcNow, expectedStartUtc, expectedStopUtc, errors);

                    expectedStartUtc ??= row.DateStartUtc;
                    expectedStopUtc ??= row.DateStopUtc;
                }
                catch (CsvScheduleException ex)
                {
                    errors.Add(ex.ToValidationError());
                }
            }
        }
        catch (CsvHelperException ex)
        {
            errors.Add(new CsvValidationError
            {
                FileName = fileName,
                Reason = $"Malformed CSV: {ex.Message}",
            });
        }

        if (recordCount == 0 && errors.TotalCount == 0)
        {
            errors.Add(new CsvValidationError
            {
                FileName = fileName,
                Reason = "CSV does not contain any data rows.",
            });
        }

        if (errors.TotalCount > 0)
        {
            return CsvParseResult.Rejected(errors.Retained, errors.TotalCount);
        }

        return CsvParseResult.Accepted(recordCount, expectedStartUtc!.Value, expectedStopUtc!.Value);
    }

    /// <summary>
    /// Streams jobs from a previously validated Accepted CSV. Assumes headers/rows are valid;
    /// missing header yields an empty sequence.
    /// </summary>
    /// <param name="filePath">Accepted CSV path.</param>
    /// <param name="importId">Import id stamped onto each job.</param>
    /// <param name="cancellationToken">Cancels between rows.</param>
    public async IAsyncEnumerable<MikrotikScheduledJob> EnumerateJobsAsync(
        string filePath,
        Guid importId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream);
        using CsvReader csv = CreateReader(reader);

        if (!await csv.ReadAsync().ConfigureAwait(false) || !csv.ReadHeader())
        {
            yield break;
        }

        Dictionary<string, int> headers = BuildHeaderMap(csv, fileName, errors: null);
        while (await csv.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParsedCsvRow row = ReadRow(csv, headers, fileName, dateTimeParser);
            int rowNumber = csv.Context.Parser?.Row ?? 0;
            yield return new MikrotikScheduledJob
            {
                ImportId = importId,
                SourceFileName = fileName,
                RowNumber = rowNumber,
                RouterName = row.RouterName,
                RouterIp = row.RouterIp,
                DateStartUtc = row.DateStartUtc,
                DateStopUtc = row.DateStopUtc,
            };
        }
    }

    /// <summary>Creates a CsvHelper reader with trimmed, lowercased header matching.</summary>
    private static CsvReader CreateReader(TextReader reader)
    {
        CsvConfiguration configuration = new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            DetectDelimiter = false,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
            PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
        };

        return new CsvReader(reader, configuration);
    }

    /// <summary>
    /// Builds a header→index map, rejecting obsolete <c>executionmode</c> and missing required columns.
    /// Result columns are permitted and ignored by <see cref="ReadRow"/>.
    /// </summary>
    private static Dictionary<string, int> BuildHeaderMap(
        CsvReader csv,
        string fileName,
        ErrorAccumulator? errors)
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        string[]? headerRecord = csv.HeaderRecord;
        if (headerRecord is null)
        {
            errors?.Add(new CsvValidationError { FileName = fileName, Reason = "CSV header row is missing." });
            return map;
        }

        for (int i = 0; i < headerRecord.Length; i++)
        {
            string header = headerRecord[i].Trim();
            if (header.Length == 0)
            {
                continue;
            }

            map[header] = i;
        }

        if (map.ContainsKey(RemovedExecutionModeHeader))
        {
            errors?.Add(new CsvValidationError
            {
                FileName = fileName,
                ColumnName = RemovedExecutionModeHeader,
                Reason =
                    "Column 'executionmode' is no longer supported. ExecutionMode (FullUpgrade / V6SteppingOnly) has been removed; "
                    + "eligible devices are always upgraded automatically to the configured RouterOS 7 target. Remove the column and re-submit the file.",
            });
        }

        // Result columns (UpgradeStarted/UpgradeCompleted/Result) are allowed and ignored:
        // ReadRow only maps RequiredHeaders, so prior results never become job inputs.

        foreach (string required in RequiredHeaders)
        {
            if (!map.ContainsKey(required))
            {
                errors?.Add(new CsvValidationError
                {
                    FileName = fileName,
                    ColumnName = required,
                    Reason = $"Required column '{required}' is missing.",
                });
            }
        }

        return map;
    }

    /// <summary>Reads required schedule fields from the current CSV row into a <see cref="ParsedCsvRow"/>.</summary>
    private static ParsedCsvRow ReadRow(
        CsvReader csv,
        Dictionary<string, int> headers,
        string fileName,
        CsvDateTimeParser dateTimeParser)
    {
        int rowNumber = csv.Context.Parser?.Row ?? 0;
        string dateStartRaw = GetRequiredField(csv, headers, "datestart", fileName, rowNumber);
        string dateStopRaw = GetRequiredField(csv, headers, "datestop", fileName, rowNumber);
        string routerName = GetRequiredField(csv, headers, "routername", fileName, rowNumber);
        string routerIpRaw = GetRequiredField(csv, headers, "routerip", fileName, rowNumber);

        DateTime dateStartUtc = dateTimeParser.ParseToUtc(dateStartRaw, fileName, rowNumber, "datestart");
        DateTime dateStopUtc = dateTimeParser.ParseToUtc(dateStopRaw, fileName, rowNumber, "datestop");
        IPAddress routerIp = ParseIpAddress(routerIpRaw, fileName, rowNumber);

        return new ParsedCsvRow(routerName, routerIp, dateStartUtc, dateStopUtc);
    }

    /// <summary>Gets a non-empty trimmed field or throws <see cref="CsvScheduleException"/>.</summary>
    private static string GetRequiredField(
        CsvReader csv,
        Dictionary<string, int> headers,
        string columnName,
        string fileName,
        int rowNumber)
    {
        if (!headers.TryGetValue(columnName, out int index))
        {
            throw new CsvScheduleException(fileName, rowNumber, columnName, $"Required column '{columnName}' is missing.");
        }

        string? value = csv.GetField(index);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CsvScheduleException(fileName, rowNumber, columnName, "Value is empty.");
        }

        return value.Trim();
    }

    /// <summary>
    /// Parses an IPv4/IPv6 literal; rejects hostnames and non-literal forms that
    /// <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> might accept.
    /// </summary>
    private static IPAddress ParseIpAddress(string value, string fileName, int rowNumber)
    {
        if (value.Any(char.IsLetter) && !value.Contains(':', StringComparison.Ordinal))
        {
            throw new CsvScheduleException(
                fileName,
                rowNumber,
                "routerip",
                $"Value '{value}' is a hostname; an IPv4 or IPv6 address is required.");
        }

        if (!IPAddress.TryParse(value, out IPAddress? address))
        {
            throw new CsvScheduleException(
                fileName,
                rowNumber,
                "routerip",
                $"Value '{value}' is not a valid IP address.");
        }

        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new CsvScheduleException(
                fileName,
                rowNumber,
                "routerip",
                $"Value '{value}' is not a valid IPv4 or IPv6 address.");
        }

        if (!LooksLikeIpLiteral(value, address))
        {
            throw new CsvScheduleException(
                fileName,
                rowNumber,
                "routerip",
                $"Value '{value}' is not a valid IP address literal.");
        }

        return address;
    }

    /// <summary>Heuristic that the original text looks like a dotted IPv4 or colon IPv6 literal.</summary>
    private static bool LooksLikeIpLiteral(string value, IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return value.Contains(':', StringComparison.Ordinal);
        }

        return value.All(static ch => char.IsDigit(ch) || ch == '.');
    }

    /// <summary>
    /// Enforces stop &gt; start, stop still open relative to <paramref name="utcNow"/>, and shared window across rows.
    /// </summary>
    private static void ValidateRow(
        string fileName,
        int? rowNumber,
        ParsedCsvRow row,
        DateTime utcNow,
        DateTime? expectedStartUtc,
        DateTime? expectedStopUtc,
        ErrorAccumulator errors)
    {
        // Maintenance window: DateStartUtc inclusive, DateStopUtc exclusive (matches the scheduler).
        // Past/equal start with a still-open stop is valid (already-active window → immediate eligibility).
        if (row.DateStopUtc <= row.DateStartUtc)
        {
            errors.Add(new CsvValidationError
            {
                FileName = fileName,
                RowNumber = rowNumber,
                ColumnName = "datestop",
                Reason = "datestop must be later than datestart.",
            });
        }
        else if (row.DateStopUtc <= utcNow)
        {
            errors.Add(new CsvValidationError
            {
                FileName = fileName,
                RowNumber = rowNumber,
                ColumnName = "datestop",
                Reason =
                    $"datestop '{row.DateStopUtc:O}' is not after the current time (UTC); the maintenance window has expired.",
            });
        }

        if (expectedStartUtc is not null && row.DateStartUtc != expectedStartUtc)
        {
            errors.Add(new CsvValidationError
            {
                FileName = fileName,
                RowNumber = rowNumber,
                ColumnName = "datestart",
                Reason = "All rows in a CSV must share the same datestart value.",
            });
        }

        if (expectedStopUtc is not null && row.DateStopUtc != expectedStopUtc)
        {
            errors.Add(new CsvValidationError
            {
                FileName = fileName,
                RowNumber = rowNumber,
                ColumnName = "datestop",
                Reason = "All rows in a CSV must share the same datestop value.",
            });
        }
    }

    /// <summary>Normalized required fields from one CSV data row.</summary>
    /// <param name="RouterName">Trimmed router name.</param>
    /// <param name="RouterIp">Parsed IP literal.</param>
    /// <param name="DateStartUtc">Inclusive window start (UTC).</param>
    /// <param name="DateStopUtc">Exclusive window end (UTC).</param>
    private sealed record ParsedCsvRow(
        string RouterName,
        IPAddress RouterIp,
        DateTime DateStartUtc,
        DateTime DateStopUtc);

    /// <summary>
    /// Counts all validation errors while retaining only the first <paramref name="maxRetained"/> for reports.
    /// </summary>
    /// <param name="maxRetained">Cap from <see cref="ScheduledJobsOptions.MaxValidationErrorsRetained"/>.</param>
    private sealed class ErrorAccumulator(int maxRetained)
    {
        /// <summary>
        /// Errors kept for the reject report, capped at the maximum retained.
        /// </summary>
        private readonly List<CsvValidationError> _retained = [];

        /// <summary>Total errors seen, including those omitted from <see cref="Retained"/>.</summary>
        public int TotalCount { get; private set; }

        /// <summary>Errors kept for the reject report (≤ max retained).</summary>
        public IReadOnlyList<CsvValidationError> Retained => _retained;

        /// <summary>Increments <see cref="TotalCount"/> and appends when under the retain cap.</summary>
        public void Add(CsvValidationError error)
        {
            TotalCount++;
            if (_retained.Count < maxRetained)
            {
                _retained.Add(error);
            }
        }
    }
}

/// <summary>
/// Outcome of <see cref="ScheduledJobCsvParser.ValidateAsync"/>: either accepted counts/window or retained errors.
/// </summary>
internal sealed class CsvParseResult
{
    /// <summary>
    /// Creates a result; use <see cref="Accepted"/> or <see cref="Rejected"/>.
    /// </summary>
    /// <param name="isAccepted">Whether validation passed.</param>
    /// <param name="recordCount">Number of data rows accepted.</param>
    /// <param name="dateStartUtc">Shared window start in UTC.</param>
    /// <param name="dateStopUtc">Shared window stop in UTC.</param>
    /// <param name="errors">Retained validation errors.</param>
    /// <param name="totalErrorCount">Total errors found, including any not retained.</param>
    private CsvParseResult(
        bool isAccepted,
        int recordCount,
        DateTime dateStartUtc,
        DateTime dateStopUtc,
        IReadOnlyList<CsvValidationError> errors,
        int totalErrorCount)
    {
        IsAccepted = isAccepted;
        RecordCount = recordCount;
        DateStartUtc = dateStartUtc;
        DateStopUtc = dateStopUtc;
        Errors = errors;
        TotalErrorCount = totalErrorCount;
    }

    /// <summary><c>true</c> when the file may proceed to Accepted/scheduling.</summary>
    public bool IsAccepted { get; }

    /// <summary>Validated data-row count when accepted; otherwise 0.</summary>
    public int RecordCount { get; }

    /// <summary>Shared maintenance window start when accepted.</summary>
    public DateTime DateStartUtc { get; }

    /// <summary>Shared maintenance window stop when accepted.</summary>
    public DateTime DateStopUtc { get; }

    /// <summary>Retained validation errors (may be fewer than <see cref="TotalErrorCount"/>).</summary>
    public IReadOnlyList<CsvValidationError> Errors { get; }

    /// <summary>Total validation errors discovered (including omitted ones).</summary>
    public int TotalErrorCount { get; }

    /// <summary>Factory for a successful validation result.</summary>
    public static CsvParseResult Accepted(int recordCount, DateTime dateStartUtc, DateTime dateStopUtc) =>
        new(true, recordCount, dateStartUtc, dateStopUtc, [], 0);

    /// <summary>Factory for a rejected validation result.</summary>
    public static CsvParseResult Rejected(IReadOnlyList<CsvValidationError> errors, int totalErrorCount) =>
        new(false, 0, default, default, errors, totalErrorCount);
}
