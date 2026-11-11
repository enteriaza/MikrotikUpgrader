using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

public enum LiveInventoryCollectionStatus
{
    NotAttempted = 0,
    Succeeded = 1,
    Partial = 2,
    ConnectionFailed = 3,
    AuthenticationFailed = 4,
    TimedOut = 5,
    Cancelled = 6,
    Failed = 7,
}

public enum LiveIdentityCollectionStatus
{
    NotAttempted = 0,
    Succeeded = 1,
    Unavailable = 2,
    Failed = 3,
}

/// <summary>
/// One validated (or rejected) row from the live-audit input CSV.
/// </summary>
public sealed class LiveAuditRouterInput
{
    public required int RowNumber { get; init; }

    public string? RouterName { get; init; }

    public required string RouterIpText { get; init; }

    public System.Net.IPAddress? RouterIp { get; init; }

    public bool IsValid { get; init; }

    public string? ValidationError { get; init; }

    public bool IsDuplicate { get; init; }
}

/// <summary>
/// Result of a single read-only inventory collection attempt.
/// </summary>
public sealed class ReadOnlyInventoryCollectionResult
{
    public required string RouterIp { get; init; }

    public string? RouterName { get; init; }

    public required LiveInventoryCollectionStatus CollectionStatus { get; init; }

    public LiveIdentityCollectionStatus IdentityStatus { get; init; }

    public string? Identity { get; init; }

    public string? RouterOsVersion { get; init; }

    public string? Architecture { get; init; }

    public IReadOnlyList<InstalledPackageInfo> Packages { get; init; } = [];

    public string? FailureDetail { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<string> CommandsIssued { get; init; } = [];

    /// <summary>
    /// True only for a complete, successful inventory that may be assessed for catalogue compatibility.
    /// Partial / failed collections must never be treated as usable for a Compatible outcome.
    /// </summary>
    public bool HasUsableInventory =>
        CollectionStatus == LiveInventoryCollectionStatus.Succeeded
        && Packages.Count > 0
        && !string.IsNullOrWhiteSpace(Architecture)
        && !string.IsNullOrWhiteSpace(RouterOsVersion);
}

/// <summary>
/// Per-router fleet audit record combining collection + catalogue compatibility.
/// </summary>
public sealed class LiveFleetRouterAuditRecord
{
    public required LiveAuditRouterInput Input { get; init; }

    public required ReadOnlyInventoryCollectionResult Collection { get; init; }

    public PackageCompatibilityDeviceReport? Compatibility { get; init; }

    public DateTimeOffset AuditedAtUtc { get; init; }
}

public sealed class LiveFleetAuditSummary
{
    public required int TotalInputRows { get; init; }

    public required int ValidUniqueAddresses { get; init; }

    public required int DuplicateAddresses { get; init; }

    public required int InvalidInputRows { get; init; }

    public required int SuccessfullyCollected { get; init; }

    public required int ConnectionFailures { get; init; }

    public required int PartialInventories { get; init; }

    public required int FullyCompatibleDevices { get; init; }

    public required int BlockedByMissingPackages { get; init; }

    public required int AmbiguousPackages { get; init; }

    public required int AceOrBrExcludedFromV7 { get; init; }

    public required int UnknownOrUnavailableIdentity { get; init; }

    public required bool Completed { get; init; }

    public required bool Cancelled { get; init; }

    public string? Notes { get; init; }
}

public sealed class LiveFleetAuditReport
{
    public required LiveFleetAuditSummary Summary { get; init; }

    public required IReadOnlyList<LiveFleetRouterAuditRecord> Routers { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset FinishedAtUtc { get; init; }

    public required string V6SteppingTarget { get; init; }

    public string? V7Target { get; init; }
}
