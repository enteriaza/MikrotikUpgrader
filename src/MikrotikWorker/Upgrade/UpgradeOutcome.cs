namespace MikrotikWorker.Upgrade;

public enum UpgradeOutcome
{
    NotAttempted = 0,
    SkippedAlreadyAtTarget = 1,
    SkippedUpgradeDisabled = 2,
    UpgradeSucceeded = 3,
    UpgradeFailed = 4,
    UpgradeTimedOut = 5,
    UpgradeVersionMismatch = 6,
    UpgradePackageMismatch = 7,
    UpgradeInterrupted = 8,
    ManualReviewRequired = 9,
    BackupFailed = 10,
    PackageResolutionFailed = 11,
    PolicyBlocked = 12,
}

public sealed class UpgradeExecutionResult
{
    public required UpgradeOutcome Outcome { get; init; }

    public string? Detail { get; init; }

    public string? PostUpgradeVersion { get; init; }

    /// <summary>
    /// True when <c>/system/reboot</c> was issued; the original API connection must not be reused.
    /// </summary>
    public bool RebootInitiated { get; init; }
}
