namespace MikrotikWorker.Upgrade;

public enum UpgradeOutcome
{
    NotAttempted = 0,
    SkippedAlreadyAtTarget = 1,
    SkippedUpgradeDisabled = 2,
    /// <summary>Full multi-stage success ending on the second-stage (RouterOS 7) target.</summary>
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
    /// <summary>
    /// RouterOS 7 skipped because the device was positively identified as ACE or BR.
    /// </summary>
    UpgradeSucceededV6Only = 13,
    /// <summary>
    /// Device already runs a RouterOS version newer than the validated target; no downgrade.
    /// </summary>
    SkippedNewerThanTarget = 14,
    /// <summary>
    /// RouterOS 7 target file is missing/invalid so a required v7 upgrade cannot start.
    /// </summary>
    TargetUnavailable = 15,
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
