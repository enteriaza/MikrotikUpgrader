// -----------------------------------------------------------------------------
// File: CsvOptions.cs
//
// Description:
// Options bound from the "Csv" section controlling culture, time zone, and
// accepted exact date/time formats for Excel-exported scheduled-job CSVs.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Configuration;

/// <summary>
/// Culture-aware CSV date/time parsing settings for Excel-exported schedules.
/// </summary>
public sealed class CsvOptions
{
    /// <summary>Configuration section name: <c>Csv</c>.</summary>
    public const string SectionName = "Csv";

    /// <summary>
    /// Culture used for <see cref="DateTimeFormats"/> parsing (for example <c>en-ZA</c>).
    /// Default: <c>en-ZA</c>.
    /// </summary>
    public string Culture { get; set; } = "en-ZA";

    /// <summary>
    /// IANA or Windows time zone id used to interpret timezone-naive CSV date/time values.
    /// Default: <c>Africa/Johannesburg</c>.
    /// </summary>
    public string TimeZoneId { get; set; } = "Africa/Johannesburg";

    /// <summary>
    /// Accepted exact date/time formats for Excel CSV exports.
    /// Defaults to common <c>en-ZA</c> / ISO-like day-first and year-first patterns.
    /// </summary>
    public string[] DateTimeFormats { get; set; } =
    [
        "dd/MM/yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm",
        "d/M/yyyy H:mm:ss",
        "d/M/yyyy H:mm",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm",
    ];
}
