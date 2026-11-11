using System.Globalization;
using System.Net;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Parses a read-only live-audit router list CSV. Credentials are never accepted from CSV.
/// The source file is never modified.
/// </summary>
internal static class LiveAuditRouterCsvParser
{
    public sealed class ParseResult
    {
        public required IReadOnlyList<LiveAuditRouterInput> Rows { get; init; }

        public IReadOnlyList<LiveAuditRouterInput> ValidUnique =>
            Rows.Where(static r => r.IsValid && !r.IsDuplicate).ToList();

        public int DuplicateCount => Rows.Count(static r => r.IsDuplicate);

        public int InvalidCount => Rows.Count(static r => !r.IsValid);
    }

    public static ParseResult ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader);
    }

    public static ParseResult Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        CsvConfiguration config = new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
            DetectDelimiter = true,
        };

        using CsvReader csv = new(reader, config);
        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is null)
        {
            throw new InvalidOperationException("Live audit CSV must contain a header row with at least 'routerip'.");
        }

        HashSet<string> headers = new(
            csv.HeaderRecord.Select(static h => h.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
        if (!headers.Contains("routerip"))
        {
            throw new InvalidOperationException("Live audit CSV requires a 'routerip' column.");
        }

        // Reject credential-looking columns so operators cannot accidentally supply secrets via CSV.
        string[] forbidden = ["password", "passwd", "username", "user", "credential", "credentials", "secret"];
        foreach (string header in headers)
        {
            if (forbidden.Contains(header, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Live audit CSV must not contain credential columns (found '{header}'). Use shared Mikrotik credentials instead.");
            }
        }

        bool hasName = headers.Contains("routername");
        List<LiveAuditRouterInput> rows = [];
        HashSet<string> seenIps = new(StringComparer.OrdinalIgnoreCase);
        int rowNumber = 1; // header is row 1

        while (csv.Read())
        {
            rowNumber++;
            string ipText = (csv.GetField("routerip") ?? string.Empty).Trim();
            string? name = hasName ? NullIfWhiteSpace(csv.GetField("routername")) : null;

            if (string.IsNullOrWhiteSpace(ipText))
            {
                rows.Add(new LiveAuditRouterInput
                {
                    RowNumber = rowNumber,
                    RouterName = name,
                    RouterIpText = ipText,
                    IsValid = false,
                    ValidationError = "routerip is empty.",
                });
                continue;
            }

            if (!IPAddress.TryParse(ipText, out IPAddress? address))
            {
                rows.Add(new LiveAuditRouterInput
                {
                    RowNumber = rowNumber,
                    RouterName = name,
                    RouterIpText = ipText,
                    IsValid = false,
                    ValidationError = $"'{ipText}' is not a valid IP address. Hostnames are not supported.",
                });
                continue;
            }

            string key = address.ToString();
            bool duplicate = !seenIps.Add(key);
            rows.Add(new LiveAuditRouterInput
            {
                RowNumber = rowNumber,
                RouterName = name,
                RouterIpText = ipText,
                RouterIp = address,
                IsValid = true,
                IsDuplicate = duplicate,
                ValidationError = duplicate ? $"Duplicate of address {key}." : null,
            });
        }

        return new ParseResult { Rows = rows };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
