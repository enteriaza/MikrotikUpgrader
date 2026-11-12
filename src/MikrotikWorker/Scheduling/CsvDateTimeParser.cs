// -----------------------------------------------------------------------------
// File: CsvDateTimeParser.cs
//
// Description:
// Parses CSV date/time literals into UTC using configured culture, format list,
// and time zone. Rejects empty values, invalid DST gaps, and prefers the earlier
// (standard-time) offset when a local time is ambiguous.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Converts schedule CSV date/time strings into UTC <see cref="DateTime"/> values.
/// </summary>
/// <param name="options">CSV culture, formats, and time-zone configuration.</param>
internal sealed class CsvDateTimeParser(IOptions<CsvOptions> options)
{
    /// <summary>
    /// Parses <paramref name="rawValue"/> with configured formats and converts it to UTC.
    /// Ambiguous local times use the earliest offset; invalid DST gap times are rejected.
    /// </summary>
    /// <param name="rawValue">Raw cell text from the CSV.</param>
    /// <param name="fileName">File name for structured exceptions.</param>
    /// <param name="rowNumber">Parser row for structured exceptions.</param>
    /// <param name="columnName">Column name for structured exceptions.</param>
    /// <returns>UTC instant with <see cref="DateTimeKind.Utc"/> semantics via <c>UtcDateTime</c> conversion.</returns>
    /// <exception cref="CsvScheduleException">Thrown when the value is empty, unparsable, or invalid in the configured zone.</exception>
    public DateTime ParseToUtc(string rawValue, string fileName, int rowNumber, string columnName)
    {
        CsvOptions csvOptions = options.Value;
        CultureInfo culture = CultureInfo.GetCultureInfo(csvOptions.Culture);
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(csvOptions.TimeZoneId);

        string value = rawValue.Trim();
        if (value.Length == 0)
        {
            throw new CsvScheduleException(fileName, rowNumber, columnName, "Value is empty.");
        }

        if (!DateTime.TryParseExact(
                value,
                csvOptions.DateTimeFormats,
                culture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            throw new CsvScheduleException(
                fileName,
                rowNumber,
                columnName,
                $"Value '{value}' is not a valid date/time for culture '{csvOptions.Culture}'.");
        }

        DateTime unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(unspecified))
        {
            throw new CsvScheduleException(
                fileName,
                rowNumber,
                columnName,
                $"Value '{value}' falls in an invalid daylight-saving gap for time zone '{csvOptions.TimeZoneId}'.");
        }

        if (timeZone.IsAmbiguousTime(unspecified))
        {
            // Prefer the standard-time (earlier) offset for ambiguous local times.
            TimeSpan[] offsets = timeZone.GetAmbiguousTimeOffsets(unspecified);
            TimeSpan chosen = offsets.Min();
            return new DateTimeOffset(unspecified, chosen).UtcDateTime;
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
    }
}
