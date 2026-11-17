// -----------------------------------------------------------------------------
// File: DeviceCompatibilityModels.cs
//
// Description:
// Assessment enums and DeviceCompatibilityAssessment / AssessmentFinding DTOs
// produced by DeviceCompatibilityAssessor for scheduling and upgrade gating.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Assessment;

/// <summary>
/// How the device version sits relative to the fixed v6 stepping target.
/// </summary>
public enum RouterOsVersionClassification
{
    /// <summary>Version could not be parsed or is outside v6/v7 expectations.</summary>
    Unknown = 0,
    /// <summary>RouterOS 6 older than the fixed stepping release.</summary>
    OlderThanTargetV6 = 1,
    /// <summary>RouterOS 6 exactly at the fixed stepping release.</summary>
    AtTargetV6 = 2,
    /// <summary>RouterOS 6 newer than the fixed stepping release.</summary>
    NewerThanTargetV6 = 3,
    /// <summary>RouterOS 7 or later.</summary>
    RouterOsV7OrLater = 4,
}

/// <summary>
/// Result of matching the device against catalogue packages for the resolved target.
/// </summary>
public enum PackageCompatibilityStatus
{
    /// <summary>A single matching package was found.</summary>
    Compatible = 0,
    /// <summary>No package matched the target.</summary>
    Missing = 1,
    /// <summary>Packages exist but architecture does not match the device.</summary>
    ArchitectureMismatch = 2,
    /// <summary>Packages exist but version does not match the target.</summary>
    VersionMismatch = 3,
    /// <summary>Multiple packages matched ambiguously.</summary>
    Ambiguous = 4,
    /// <summary>Device identity fields were insufficient to evaluate packages.</summary>
    Unidentifiable = 5,

    // 6 was NotEvaluated (removed: package assessor always returns a concrete status).
}

/// <summary>
/// Recommended automatic upgrade path for the device.
/// </summary>
public enum UpgradePathStatus
{
    /// <summary>RouterOS v6 older than the fixed stepping release — step to 6.49.23 then v7.</summary>
    EligibleSubjectToManualPathVerification = 0,

    // 1 was AlreadyAtTarget (removed: prefer AlreadyAtV7Target / NewerThanV7Target).
    // 2 was NewerV6RequiresReview (removed: prefer SkipV6SteppingProceedToV7).
    // 3 was OutOfScopeV7 (removed: v7 is in scope via EligibleDirectV7Upgrade / AlreadyAtV7Target).

    /// <summary>Version unparseable or otherwise unknown — manual review required.</summary>
    UnknownRequiresManualReview = 4,
    /// <summary>Path blocked (e.g. v7 device with no validated FirmwareFiles target).</summary>
    Blocked = 5,

    // 6 was NotEvaluated (removed: assessor always assigns a concrete path).

    /// <summary>Device already runs 6.49.23 or newer RouterOS 6 — skip stepping, proceed to v7.</summary>
    SkipV6SteppingProceedToV7 = 7,
    /// <summary>Device runs RouterOS 7 older than the dynamic target — upgrade directly.</summary>
    EligibleDirectV7Upgrade = 8,
    /// <summary>Device already runs the dynamic RouterOS 7 target.</summary>
    AlreadyAtV7Target = 9,
    /// <summary>Device runs RouterOS 7 newer than target — never downgrade.</summary>
    NewerThanV7Target = 10,
}

/// <summary>
/// RouterBOARD loader firmware assessment relative to upgrade-firmware.
/// </summary>
public enum RouterboardFirmwareStatus
{
    /// <summary>Current firmware equals upgrade firmware.</summary>
    Current = 0,
    /// <summary>Upgrade firmware differs from current.</summary>
    UpgradeAvailable = 1,
    /// <summary>RouterBOARD fields were not available.</summary>
    Unavailable = 2,
    /// <summary>Fields present but comparison inconclusive.</summary>
    Inconclusive = 3,

    // 4 was NotEvaluated (removed: routerboard assessor always returns a concrete status).
}

/// <summary>
/// Roll-up readiness used by the scheduler/upgrade executor.
/// </summary>
public enum DeviceAssessmentStatus
{
    /// <summary>Automatic upgrade path may proceed.</summary>
    ReadyForUpgrade = 0,
    /// <summary>Device already at the intended target (v7 target or newer than target).</summary>
    AlreadyAtTarget = 1,

    // 2 was ManualReviewRequired (removed: DetermineOverall never emits it).

    /// <summary>Hard blockers prevent automatic upgrade.</summary>
    Blocked = 3,

    // 4 was Unsupported (removed: DetermineOverall never emits it).
}

/// <summary>
/// Severity of an individual assessment finding.
/// </summary>
public enum AssessmentFindingSeverity
{
    /// <summary>Informational note.</summary>
    Info = 0,
    /// <summary>Non-blocking warning.</summary>
    Warning = 1,
    /// <summary>Blocks automatic upgrade.</summary>
    Blocker = 2,
}

/// <summary>
/// One coded finding contributed during assessment.
/// </summary>
/// <param name="Severity">Finding severity.</param>
/// <param name="Code">Stable machine-readable code.</param>
/// <param name="Message">Human-readable detail (no credentials).</param>
public sealed record AssessmentFinding(AssessmentFindingSeverity Severity, string Code, string Message);

/// <summary>
/// Structured, credential-free firmware compatibility assessment for a single device.
/// </summary>
public sealed class DeviceCompatibilityAssessment
{
    /// <summary>Management IP of the assessed router.</summary>
    public required string RouterIp { get; init; }

    /// <summary>RouterOS identity when known.</summary>
    public string? RouterIdentity { get; init; }

    /// <summary>Raw version string from the device.</summary>
    public string? RouterOsVersionRaw { get; init; }

    /// <summary>Parsed numeric version when parseable.</summary>
    public RouterOsVersion? RouterOsVersion { get; init; }

    /// <summary>Classification vs the fixed v6 stepping target.</summary>
    public RouterOsVersionClassification RouterOsVersionClassification { get; init; }

    /// <summary>Device architecture when known.</summary>
    public string? Architecture { get; init; }

    /// <summary>Board name when known.</summary>
    public string? BoardName { get; init; }

    /// <summary>Platform string when known.</summary>
    public string? Platform { get; init; }

    /// <summary>RouterBOARD model when known.</summary>
    public string? RouterboardModel { get; init; }

    /// <summary>RouterBOARD firmware type when known.</summary>
    public string? RouterboardFirmwareType { get; init; }

    /// <summary>Current RouterBOARD firmware when known.</summary>
    public string? RouterboardCurrentFirmware { get; init; }

    /// <summary>Upgrade RouterBOARD firmware when known.</summary>
    public string? RouterboardUpgradeFirmware { get; init; }

    /// <summary>Factory RouterBOARD firmware when known.</summary>
    public string? RouterboardFactoryFirmware { get; init; }

    /// <summary>Reported target version text for this assessment.</summary>
    public required string TargetRouterOsVersion { get; init; }

    /// <summary>Matched catalogue file name when compatible.</summary>
    public string? MatchingPackageFileName { get; init; }

    /// <summary>Architecture of the matched package.</summary>
    public string? PackageArchitecture { get; init; }

    /// <summary>Version of the matched package.</summary>
    public string? PackageVersion { get; init; }

    /// <summary>Package match outcome.</summary>
    public PackageCompatibilityStatus PackageCompatibility { get; init; }

    /// <summary>Recommended automatic upgrade path.</summary>
    public UpgradePathStatus UpgradePath { get; init; }

    /// <summary>RouterBOARD loader assessment.</summary>
    public RouterboardFirmwareStatus RouterboardAssessment { get; init; }

    /// <summary>Roll-up readiness status.</summary>
    public DeviceAssessmentStatus OverallStatus { get; init; }

    /// <summary>UTC timestamp when assessment was produced.</summary>
    public DateTimeOffset AssessedAtUtc { get; init; }

    /// <summary>Findings collected during assessment. Defaults to empty.</summary>
    public IReadOnlyList<AssessmentFinding> Findings { get; init; } = [];
}
