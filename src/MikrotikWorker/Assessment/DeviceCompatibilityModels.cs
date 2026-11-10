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
    EligibleSubjectToManualPathVerification = 0,
    AlreadyAtTarget = 1,
    NewerV6RequiresReview = 2,
    OutOfScopeV7 = 3,
    UnknownRequiresManualReview = 4,
    Blocked = 5,
    NotEvaluated = 6,
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
