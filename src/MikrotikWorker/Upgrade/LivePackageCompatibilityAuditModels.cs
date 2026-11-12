// -----------------------------------------------------------------------------
// File: LivePackageCompatibilityAuditModels.cs
//
// Description:
// Models for live read-only package-compatibility audits: CSV input rows,
// inventory collection results, fleet audit records/summaries, and collection
// status enums.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Outcome of a live read-only inventory collection attempt.
/// </summary>
public enum LiveInventoryCollectionStatus
{
    /// <summary>Collection was not started for this row.</summary>
    NotAttempted = 0,

    /// <summary>Full usable inventory collected.</summary>
    Succeeded = 1,

    /// <summary>Some prints succeeded but inventory is incomplete.</summary>
    Partial = 2,

    /// <summary>API connect failed.</summary>
    ConnectionFailed = 3,

    /// <summary>
    /// The router was reachable but rejected the API login (a tik4net login exception or an
    /// "invalid user name or password" / "cannot log in" / "login failed" error anywhere in the
    /// exception chain), so no inventory was read. Fix the credentials; retrying will not help.
    /// </summary>
    AuthenticationFailed = 4,

    /// <summary>Collection timed out.</summary>
    TimedOut = 5,

    /// <summary>Caller or deadline cancellation stopped collection.</summary>
    Cancelled = 6,

    /// <summary>Non-specific collection failure.</summary>
    Failed = 7,
}

/// <summary>
/// Outcome of reading RouterOS identity during live inventory collection.
/// </summary>
public enum LiveIdentityCollectionStatus
{
    /// <summary>Identity print was not attempted.</summary>
    NotAttempted = 0,

    /// <summary>Identity name was read successfully.</summary>
    Succeeded = 1,

    /// <summary>Identity was empty or unavailable on an otherwise successful session.</summary>
    Unavailable = 2,

    /// <summary>Identity print failed.</summary>
    Failed = 3,
}

/// <summary>
/// One validated (or rejected) row from the live-audit input CSV.
/// </summary>
public sealed class LiveAuditRouterInput
{
    /// <summary>1-based CSV row number.</summary>
    public required int RowNumber { get; init; }

    /// <summary>Optional router display name from CSV (not used as credentials).</summary>
    public string? RouterName { get; init; }

    /// <summary>Raw router IP text from CSV.</summary>
    public required string RouterIpText { get; init; }

    /// <summary>Parsed IP when <see cref="IsValid"/>; otherwise null.</summary>
    public System.Net.IPAddress? RouterIp { get; init; }

    /// <summary>True when the row has a usable unique IP.</summary>
    public bool IsValid { get; init; }

    /// <summary>Validation error text when the row is invalid.</summary>
    public string? ValidationError { get; init; }

    /// <summary>True when this IP duplicates an earlier valid row.</summary>
    public bool IsDuplicate { get; init; }
}

/// <summary>
/// Result of a single read-only inventory collection attempt.
/// </summary>
public sealed class ReadOnlyInventoryCollectionResult
{
    /// <summary>Router IP that was contacted.</summary>
    public required string RouterIp { get; init; }

    /// <summary>Optional display name from the CSV input.</summary>
    public string? RouterName { get; init; }

    /// <summary>Overall collection status.</summary>
    public required LiveInventoryCollectionStatus CollectionStatus { get; init; }

    /// <summary>Identity print status.</summary>
    public LiveIdentityCollectionStatus IdentityStatus { get; init; }

    /// <summary>RouterOS identity name when collected.</summary>
    public string? Identity { get; init; }

    /// <summary>Installed RouterOS version from resource print.</summary>
    public string? RouterOsVersion { get; init; }

    /// <summary>Architecture from resource print.</summary>
    public string? Architecture { get; init; }

    /// <summary>Parsed installed packages (may be empty on failure).</summary>
    public IReadOnlyList<InstalledPackageInfo> Packages { get; init; } = [];

    /// <summary>Failure detail when collection did not fully succeed.</summary>
    public string? FailureDetail { get; init; }

    /// <summary>Wall-clock duration of the collection attempt.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Exact RouterOS commands issued (allowlisted prints only).</summary>
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
    /// <summary>Original CSV input row.</summary>
    public required LiveAuditRouterInput Input { get; init; }

    /// <summary>Read-only collection outcome.</summary>
    public required ReadOnlyInventoryCollectionResult Collection { get; init; }

    /// <summary>
    /// Catalogue compatibility report when inventory was usable; otherwise null.
    /// </summary>
    public PackageCompatibilityDeviceReport? Compatibility { get; init; }

    /// <summary>UTC timestamp when this router record was finalized.</summary>
    public DateTimeOffset AuditedAtUtc { get; init; }
}

/// <summary>
/// Aggregate counters for a live fleet package-compatibility audit run.
/// </summary>
public sealed class LiveFleetAuditSummary
{
    /// <summary>Total CSV input rows processed.</summary>
    public required int TotalInputRows { get; init; }

    /// <summary>Valid unique router addresses scheduled for collection.</summary>
    public required int ValidUniqueAddresses { get; init; }

    /// <summary>Duplicate IP rows skipped.</summary>
    public required int DuplicateAddresses { get; init; }

    /// <summary>Invalid CSV rows skipped.</summary>
    public required int InvalidInputRows { get; init; }

    /// <summary>Routers with successful full inventory collection.</summary>
    public required int SuccessfullyCollected { get; init; }

    /// <summary>Routers that failed to connect.</summary>
    public required int ConnectionFailures { get; init; }

    /// <summary>Routers with partial inventories.</summary>
    public required int PartialInventories { get; init; }

    /// <summary>Routers fully catalogue-compatible for applicable stages.</summary>
    public required int FullyCompatibleDevices { get; init; }

    /// <summary>Routers blocked by missing catalogue packages.</summary>
    public required int BlockedByMissingPackages { get; init; }

    /// <summary>Routers with ambiguous catalogue matches.</summary>
    public required int AmbiguousPackages { get; init; }

    /// <summary>
    /// Obsolete. Always 0 — ACE/BR are eligible for RouterOS 7 under the same policy as other types.
    /// Retained for live-audit report schema compatibility.
    /// </summary>
    public required int AceOrBrExcludedFromV7 { get; init; }

    /// <summary>Routers with unknown or unavailable identity.</summary>
    public required int UnknownOrUnavailableIdentity { get; init; }

    /// <summary>True when the fleet run finished all scheduled work (or filled incompletes).</summary>
    public required bool Completed { get; init; }

    /// <summary>True when the run ended due to cancellation.</summary>
    public required bool Cancelled { get; init; }

    /// <summary>Optional free-form notes for the report.</summary>
    public string? Notes { get; init; }
}

/// <summary>
/// Complete live fleet audit artefact (summary + per-router records + targets).
/// </summary>
public sealed class LiveFleetAuditReport
{
    /// <summary>Aggregate summary counters.</summary>
    public required LiveFleetAuditSummary Summary { get; init; }

    /// <summary>Per-router audit records.</summary>
    public required IReadOnlyList<LiveFleetRouterAuditRecord> Routers { get; init; }

    /// <summary>UTC start time of the fleet run.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>UTC finish time of the fleet run.</summary>
    public required DateTimeOffset FinishedAtUtc { get; init; }

    /// <summary>v6 stepping target version text used during the audit.</summary>
    public required string V6SteppingTarget { get; init; }

    /// <summary>RouterOS 7 target version text when available.</summary>
    public string? V7Target { get; init; }
}
