namespace MikrotikWorker.Scheduling;

/// <summary>
/// Application-owned result columns appended to the imported Accepted CSV.
/// Ignored by import mapping; not job inputs.
/// </summary>
internal static class ImportedCsvResultColumns
{
    public const string UpgradeStarted = "UpgradeStarted";
    public const string UpgradeCompleted = "UpgradeCompleted";
    public const string Result = "Result";

    public static readonly string[] All =
    [
        UpgradeStarted,
        UpgradeCompleted,
        Result,
    ];

    public static bool IsResultColumn(string header) =>
        All.Any(c => string.Equals(c, header, StringComparison.OrdinalIgnoreCase));

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

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

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        return value[..(max - 3)] + "...";
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
