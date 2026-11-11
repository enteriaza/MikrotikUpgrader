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
    /// Terminal v6-only success: either ACE/BR identity policy skipped RouterOS 7 under
    /// <see cref="UpgradeExecutionMode.FullUpgrade"/>, or
    /// <see cref="UpgradeExecutionMode.V6SteppingOnly"/> stopped after the v6 stage cap.
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
    /// <summary>
    /// Selected execution mode does not apply to the device's current major version
    /// (for example <see cref="UpgradeExecutionMode.V6SteppingOnly"/> on RouterOS 7).
    /// Deliberate no-op — not a policy violation.
    /// </summary>
    SkippedStageNotApplicable = 16,
}

/// <summary>
/// Values for <see cref="UpgradeExecutionResult.CompletedStage"/>.
/// A stage label is reported only after that stage's required verification succeeds.
/// </summary>
public static class UpgradeCompletedStages
{
    /// <summary>No upgrade stage was successfully completed (skipped, failed, or not applicable).</summary>
    public const string None = "none";

    /// <summary>v6 stepping completed and post-reboot verification succeeded.</summary>
    public const string V6Stepping = "v6-stepping";

    /// <summary>RouterOS 7 stage completed and verified, or device already at/above the v7 target under FullUpgrade.</summary>
    public const string RouterOs7 = "routeros7";
}

public sealed class UpgradeExecutionResult
{
    public required UpgradeOutcome Outcome { get; init; }

    public string? Detail { get; init; }

    public string? PostUpgradeVersion { get; init; }

    /// <summary>
    /// Last verified architecture name when known (never an intended target).
    /// </summary>
    public string? PostUpgradeArchitecture { get; init; }

    /// <summary>
    /// True when <c>/system/reboot</c> was issued; the original API connection must not be reused.
    /// </summary>
    public bool RebootInitiated { get; init; }

    /// <summary>
    /// Job stage cap that governed this execution.
    /// </summary>
    public UpgradeExecutionMode ExecutionMode { get; init; } = UpgradeExecutionMode.FullUpgrade;

    /// <summary>
    /// Highest upgrade stage whose verification succeeded.
    /// Use <see cref="UpgradeCompletedStages"/> values; never claim a stage that failed or was skipped.
    /// </summary>
    public string? CompletedStage { get; init; }
}
