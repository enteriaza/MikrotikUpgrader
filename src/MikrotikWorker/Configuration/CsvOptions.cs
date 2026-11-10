namespace MikrotikWorker.Configuration;

/// <summary>
/// Culture-aware CSV date/time parsing settings for Excel-exported schedules.
/// </summary>
public sealed class CsvOptions
{
    public const string SectionName = "Csv";

    /// <summary>
    /// Culture used for <see cref="DateTimeFormats"/> parsing (for example <c>en-ZA</c>).
    /// </summary>
    public string Culture { get; set; } = "en-ZA";

    /// <summary>
    /// IANA or Windows time zone id used to interpret timezone-naive CSV date/time values.
    /// </summary>
    public string TimeZoneId { get; set; } = "Africa/Johannesburg";

    /// <summary>
    /// Accepted exact date/time formats for Excel CSV exports.
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
