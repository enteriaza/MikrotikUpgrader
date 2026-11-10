using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

internal sealed class ScheduledJobCsvParser(
    IOptions<ScheduledJobsOptions> scheduledJobsOptions,
    CsvDateTimeParser dateTimeParser)
{
    private static readonly string[] RequiredHeaders = ["datestart", "datestop", "routername", "routerip"];

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

    private static bool LooksLikeIpLiteral(string value, IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return value.Contains(':', StringComparison.Ordinal);
        }

        return value.All(static ch => char.IsDigit(ch) || ch == '.');
    }

    private static void ValidateRow(
        string fileName,
        int? rowNumber,
        ParsedCsvRow row,
        DateTime utcNow,
        DateTime? expectedStartUtc,
        DateTime? expectedStopUtc,
        ErrorAccumulator errors)
    {
        if (row.DateStartUtc <= utcNow)
        {
            errors.Add(new CsvValidationError
            {
                FileName = fileName,
                RowNumber = rowNumber,
                ColumnName = "datestart",
                Reason = $"datestart '{row.DateStartUtc:O}' must be in the future (UTC).",
            });
        }

        if (row.DateStopUtc <= utcNow)
        {
            errors.Add(new CsvValidationError
            {
                FileName = fileName,
                RowNumber = rowNumber,
                ColumnName = "datestop",
                Reason = $"datestop '{row.DateStopUtc:O}' must be in the future (UTC).",
            });
        }

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

    private sealed record ParsedCsvRow(
        string RouterName,
        IPAddress RouterIp,
        DateTime DateStartUtc,
        DateTime DateStopUtc);

    private sealed class ErrorAccumulator(int maxRetained)
    {
        private readonly List<CsvValidationError> _retained = [];

        public int TotalCount { get; private set; }

        public IReadOnlyList<CsvValidationError> Retained => _retained;

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

internal sealed class CsvParseResult
{
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

    public bool IsAccepted { get; }

    public int RecordCount { get; }

    public DateTime DateStartUtc { get; }

    public DateTime DateStopUtc { get; }

    public IReadOnlyList<CsvValidationError> Errors { get; }

    public int TotalErrorCount { get; }

    public static CsvParseResult Accepted(int recordCount, DateTime dateStartUtc, DateTime dateStopUtc) =>
        new(true, recordCount, dateStartUtc, dateStopUtc, [], 0);

    public static CsvParseResult Rejected(IReadOnlyList<CsvValidationError> errors, int totalErrorCount) =>
        new(false, 0, default, default, errors, totalErrorCount);
}
