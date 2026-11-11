namespace MikrotikWorker.Assessment;

public enum RouterOsVersionClassification
{
    Unknown = 0,
    OlderThanTargetV6 = 1,
    AtTargetV6 = 2,
    NewerThanTargetV6 = 3,
    RouterOsV7OrLater = 4,
}

public enum PackageCompatibilityStatus
{
    Compatible = 0,
    Missing = 1,
    ArchitectureMismatch = 2,
    VersionMismatch = 3,
    Ambiguous = 4,
    Unidentifiable = 5,
    NotEvaluated = 6,
}

public enum UpgradePathStatus
{
    /// <summary>RouterOS v6 older than the fixed stepping release — step to 6.49.23 then v7.</summary>
    EligibleSubjectToManualPathVerification = 0,
    AlreadyAtTarget = 1,
    /// <summary>Legacy: newer v6 no longer requires review; prefer <see cref="SkipV6SteppingProceedToV7"/>.</summary>
    NewerV6RequiresReview = 2,
    /// <summary>Legacy: v7 is no longer out of scope; prefer the direct-v7 path statuses.</summary>
    OutOfScopeV7 = 3,
    UnknownRequiresManualReview = 4,
    Blocked = 5,
    NotEvaluated = 6,
    /// <summary>Device already runs 6.49.23 or newer RouterOS 6 — skip stepping, proceed to v7.</summary>
    SkipV6SteppingProceedToV7 = 7,
    /// <summary>Device runs RouterOS 7 older than the dynamic target — upgrade directly.</summary>
    EligibleDirectV7Upgrade = 8,
    /// <summary>Device already runs the dynamic RouterOS 7 target.</summary>
    AlreadyAtV7Target = 9,
    /// <summary>Device runs RouterOS 7 newer than target — never downgrade.</summary>
    NewerThanV7Target = 10,
}

public enum RouterboardFirmwareStatus
{
    Current = 0,
    UpgradeAvailable = 1,
    Unavailable = 2,
    Inconclusive = 3,
    NotEvaluated = 4,
}

public enum DeviceAssessmentStatus
{
    ReadyForUpgrade = 0,
    AlreadyAtTarget = 1,
    ManualReviewRequired = 2,
    Blocked = 3,
    Unsupported = 4,
}

public enum AssessmentFindingSeverity
{
    Info = 0,
    Warning = 1,
    Blocker = 2,
}

public sealed record AssessmentFinding(AssessmentFindingSeverity Severity, string Code, string Message);

/// <summary>
/// Structured, credential-free firmware compatibility assessment for a single device.
/// </summary>
public sealed class DeviceCompatibilityAssessment
{
    public required string RouterIp { get; init; }

    public string? RouterIdentity { get; init; }

    public string? RouterOsVersionRaw { get; init; }

    public RouterOsVersion? RouterOsVersion { get; init; }

    public RouterOsVersionClassification RouterOsVersionClassification { get; init; }

    public string? Architecture { get; init; }

    public string? BoardName { get; init; }

    public string? Platform { get; init; }

    public string? RouterboardModel { get; init; }

    public string? RouterboardFirmwareType { get; init; }

    public string? RouterboardCurrentFirmware { get; init; }

    public string? RouterboardUpgradeFirmware { get; init; }

    public string? RouterboardFactoryFirmware { get; init; }

    public required string TargetRouterOsVersion { get; init; }

    public string? MatchingPackageFileName { get; init; }

    public string? PackageArchitecture { get; init; }

    public string? PackageVersion { get; init; }

    public PackageCompatibilityStatus PackageCompatibility { get; init; }

    public UpgradePathStatus UpgradePath { get; init; }

    public RouterboardFirmwareStatus RouterboardAssessment { get; init; }

    public DeviceAssessmentStatus OverallStatus { get; init; }

    public DateTimeOffset AssessedAtUtc { get; init; }

    public IReadOnlyList<AssessmentFinding> Findings { get; init; } = [];
}
