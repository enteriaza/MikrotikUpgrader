using System.Globalization;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

internal sealed class CsvDateTimeParser(IOptions<CsvOptions> options)
{
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
