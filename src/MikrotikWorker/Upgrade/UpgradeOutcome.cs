// -----------------------------------------------------------------------------
// File: UpgradeOutcome.cs
//
// Description:
// Terminal upgrade outcomes, completed-stage labels, and the UpgradeExecutionResult
// DTO returned by RouterOsV6UpgradeExecutor and related upgrade services. Stage
// labels are reported only after that stage's required verification succeeds.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Terminal outcome of an upgrade job or stage. Some values are skips that must still
/// map to a failed/blocked job terminal (for example <see cref="SkippedUpgradeDisabled"/>).
/// </summary>
public enum UpgradeOutcome
{
    /// <summary>Upgrade path was not entered or no attempt was made.</summary>
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

    /// <summary>Package fetch, staging, or a non-specific package-stage failure.</summary>
    UpgradeFailed = 4,

    /// <summary>Reboot reconnect or post-reboot verification window timed out.</summary>
    UpgradeTimedOut = 5,

    /// <summary>Post-reboot RouterOS version did not match the stage target.</summary>
    UpgradeVersionMismatch = 6,

    /// <summary>Post-reboot package inventory failed strict verification.</summary>
    UpgradePackageMismatch = 7,

    /// <summary>Caller cancellation interrupted an in-progress upgrade stage.</summary>
    UpgradeInterrupted = 8,

    /// <summary>Automated path cannot safely decide; operator review required.</summary>
    ManualReviewRequired = 9,

    /// <summary>Pre-upgrade or pre-OSPF central backup failed verification.</summary>
    BackupFailed = 10,

    /// <summary>Catalogue resolution could not select required .npk packages.</summary>
    PackageResolutionFailed = 11,

    /// <summary>Assessment, schedule window, identity policy, or similar gate blocked the job.</summary>
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

/// <summary>
/// Result returned by the upgrade executor and RouterBOARD service for one job attempt.
/// </summary>
public sealed class UpgradeExecutionResult
{
    /// <summary>Terminal outcome for this attempt.</summary>
    public required UpgradeOutcome Outcome { get; init; }

    /// <summary>Optional human-readable detail explaining skips and failures.</summary>
    public string? Detail { get; init; }

    /// <summary>Last observed RouterOS version after the attempt (when known).</summary>
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
