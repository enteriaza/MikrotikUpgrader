namespace MikrotikWorker.Upgrade;

public enum UpgradeOutcome
{
    NotAttempted = 0,

    /// <summary>Device already runs the configured RouterOS 7 target; verified, no upgrade needed.</summary>
    SkippedAlreadyAtTarget = 1,

    /// <summary>
    /// Firmware upgrades are administratively disabled (<c>FirmwareUpgrade:Enabled=false</c>).
    /// Must map to a failed/blocked job terminal — never success.
    /// </summary>
    SkippedUpgradeDisabled = 2,

    /// <summary>Full multi-stage success ending on the configured RouterOS 7 target.</summary>
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

    // 13 was UpgradeSucceededV6Only (removed: the workflow never stops after v6 and reports success).

    /// <summary>
    /// Device already runs a RouterOS version newer than the validated target; no downgrade.
    /// </summary>
    SkippedNewerThanTarget = 14,

    /// <summary>
    /// RouterOS 7 target file is missing/invalid so a required v7 upgrade cannot start.
    /// </summary>
    TargetUnavailable = 15,

    // 16 was SkippedStageNotApplicable (removed together with the operator ExecutionMode).

    /// <summary>
    /// Legacy router-side FleetUpgrade script/scheduler could not be removed or verified absent
    /// before firmware mutation.
    /// </summary>
    LegacyFirmwareAutomationCleanupFailed = 17,

    /// <summary>RouterBOARD upgrade command failed or assessment forbade a safe decision.</summary>
    RouterboardUpgradeFailed = 18,

    /// <summary>RouterBOARD reboot reconnect/verification window timed out.</summary>
    RouterboardUpgradeTimedOut = 19,

    /// <summary>Post-RouterBOARD-reboot verification failed (firmware or RouterOS identity).</summary>
    RouterboardVerificationFailed = 20,

    /// <summary>OSPF configuration could not be captured from the router.</summary>
    OspfCaptureFailed = 21,

    /// <summary>OSPF migration plan failed fleet-policy validation before v7 mutation.</summary>
    OspfPlanValidationFailed = 22,

    /// <summary>OSPF v7 reconciliation/apply failed.</summary>
    OspfMigrationApplyFailed = 23,

    /// <summary>Post-apply OSPF configuration verification failed.</summary>
    OspfMigrationVerificationFailed = 24,

    /// <summary>
    /// Optional static-summary redistribution stage failed (routes, <c>ospf-v2-out</c> filter,
    /// or OSPFv2 redistribute/out-filter-chain verification).
    /// </summary>
    OspfStaticSummaryRedistributionFailed = 25,
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

    /// <summary>RouterOS 7 package stage completed and verified (pre–RouterBOARD stage).</summary>
    public const string RouterOs7 = "routeros7";

    /// <summary>
    /// OSPF migration applied and configuration-verified after RouterOS 7 package verification.
    /// </summary>
    public const string OspfMigration = "ospf-migration";

    /// <summary>
    /// Optional static-summary redistribution (blackhole routes + <c>ospf-v2-out</c>) verified
    /// after OSPF migration and before RouterBOARD firmware.
    /// </summary>
    public const string OspfSummaryRedistribution = "ospf-summary-redistribution";

    /// <summary>
    /// RouterBOARD assessment completed and any required upgrade/reboot verified
    /// (or explicitly skipped as not required). Terminal success stage for the normal workflow.
    /// </summary>
    public const string RouterboardFirmware = "routerboard-firmware";
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
    /// Highest upgrade stage whose verification succeeded.
    /// Use <see cref="UpgradeCompletedStages"/> values; never claim a stage that failed or was skipped.
    /// </summary>
    public string? CompletedStage { get; init; }
}
