namespace MikrotikWorker.Configuration;

/// <summary>
/// Transient connection and authentication retry policy for router job dispatch.
/// Retries apply only to failures during initial connect/login before the upgrade workflow starts.
/// In-process only — process restart discards pending delayed retries (no automatic resumption).
/// </summary>
public sealed class JobRetryOptions
{
    public const string SectionName = "JobRetry";

    /// <summary>
    /// Delay before a job becomes eligible again after a transient connection failure.
    /// </summary>
    public TimeSpan ConnectionRetryDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Delay before a job becomes eligible again after an authentication rejection.
    /// </summary>
    public TimeSpan AuthenticationRetryDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum unsuccessful attempts per failure category that remain retryable.
    /// Attempts 1..<see cref="MaximumUnsuccessfulAttempts"/> requeue; attempt
    /// MaximumUnsuccessfulAttempts+1 is terminal. Zero disables retry (first failure is terminal).
    /// </summary>
    public int MaximumUnsuccessfulAttempts { get; set; } = 10;
}
