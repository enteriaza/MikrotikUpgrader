// -----------------------------------------------------------------------------
// File: ImportedCsvResultColumns.cs
//
// Description:
// Names and formatters for application-owned result columns appended to the
// imported Accepted CSV (UpgradeStarted, UpgradeCompleted, Result). These are
// ignored by import mapping and never become job inputs. Result text is truncated
// and categorized for concise operator-facing outcomes.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Application-owned result columns appended to the imported Accepted CSV.
/// Ignored by import mapping; not job inputs.
/// </summary>
internal static class ImportedCsvResultColumns
{
    /// <summary>Column holding the upgrade/workflow start timestamp.</summary>
    public const string UpgradeStarted = "UpgradeStarted";

    /// <summary>Column holding the job completion timestamp.</summary>
    public const string UpgradeCompleted = "UpgradeCompleted";

    /// <summary>Column holding the concise terminal Result string.</summary>
    public const string Result = "Result";

    /// <summary>All result column names in append order.</summary>
    public static readonly string[] All =
    [
        UpgradeStarted,
        UpgradeCompleted,
        Result,
    ];

    /// <summary>
    /// Returns <c>true</c> when <paramref name="header"/> matches a result column (case-insensitive).
    /// </summary>
    /// <param name="header">CSV header text to test.</param>
    public static bool IsResultColumn(string header) =>
        All.Any(c => string.Equals(c, header, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Formats a timestamp for CSV persistence using an invariant offset pattern.
    /// </summary>
    /// <param name="value">Instant to serialize.</param>
    /// <returns>String like <c>yyyy-MM-dd'T'HH:mm:sszzz</c>.</returns>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Maps a terminal job state (plus optional detail and retry context on <paramref name="job"/>)
    /// into a concise Result column value.
    /// </summary>
    /// <param name="state">Terminal lifecycle state.</param>
    /// <param name="detail">Optional exception/detail message.</param>
    /// <param name="job">Job providing last retry category/reason for Failed formatting.</param>
    /// <returns>Operator-facing Result text (max ~180 characters when detail is included).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="job"/> is null.</exception>
    public static string FormatResult(
        ScheduledJobTerminalState state,
        string? detail,
        MikrotikScheduledJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return state switch
        {
            ScheduledJobTerminalState.Succeeded => "Success",
            ScheduledJobTerminalState.AlreadyUpToDate => "Already up to date",
            ScheduledJobTerminalState.Cancelled => "Cancelled",
            ScheduledJobTerminalState.Expired => "Expired - schedule window elapsed",
            ScheduledJobTerminalState.ObservedEligible => "Observed eligible",
            ScheduledJobTerminalState.Skipped => Concise("Skipped", detail),
            ScheduledJobTerminalState.Blocked => Concise("Failed - Blocked", detail),
            ScheduledJobTerminalState.Failed => FormatFailed(detail, job),
            _ => Concise("Failed", detail),
        };
    }

    /// <summary>
    /// Builds a Failed Result using retry category heuristics and optional detail keywords
    /// (backup / firmware / upgrade / package).
    /// </summary>
    private static string FormatFailed(string? detail, MikrotikScheduledJob job)
    {
        if (job.LastRetryFailureCategory == JobRetryFailureCategory.Connection)
        {
            return Concise("Failed - Connection", detail ?? job.LastRetryFailureReason);
        }

        if (job.LastRetryFailureCategory == JobRetryFailureCategory.Authentication)
        {
            return Concise("Failed - Authentication", detail ?? job.LastRetryFailureReason);
        }

        string? text = detail ?? job.LastRetryFailureReason;
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Failed";
        }

        if (Contains(text, "backup"))
        {
            return Concise("Failed - Backup", text);
        }

        if (Contains(text, "firmware") || Contains(text, "upgrade") || Contains(text, "package"))
        {
            return Concise("Failed - Firmware upgrade", text);
        }

        return Concise("Failed", text);
    }

    /// <summary>
    /// Joins prefix and detail into a single-line Result, avoiding duplicate prefixes, then truncates.
    /// </summary>
    private static string Concise(string prefix, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return prefix;
        }

        string trimmed = detail.Trim().Replace('\r', ' ').Replace('\n', ' ');
        while (trimmed.Contains("  ", StringComparison.Ordinal))
        {
            trimmed = trimmed.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Truncate(trimmed, 180);
        }

        return Truncate($"{prefix} - {trimmed}", 180);
    }

    /// <summary>Truncates with a trailing ellipsis when longer than <paramref name="max"/>.</summary>
    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        return value[..(max - 3)] + "...";
    }

    /// <summary>Case-insensitive substring check.</summary>
    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
