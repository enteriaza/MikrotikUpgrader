// -----------------------------------------------------------------------------
// File: PackageCompatibilityAuditModels.cs
//
// Description:
// Models for non-live package-compatibility audits: device fixtures, per-stage
// resolution rows, device reports, and catalogue coverage architecture rows.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Upgrade stage evaluated by the package-compatibility auditor.
/// </summary>
public enum PackageCompatibilityUpgradeStage
{
    /// <summary>Fixed v6 stepping target (<see cref="RouterOsUpgradeVersions.V6Stepping"/>).</summary>
    V6Stepping = 1,

    /// <summary>Configured RouterOS 7 target from the version file / provider.</summary>
    RouterOs7 = 2,
}

/// <summary>
/// Catalogue compatibility outcome for an installed independent-package inventory.
/// Distinct from assessment-level <see cref="Assessment.PackageCompatibilityStatus"/>.
/// </summary>
public enum CatalogueCompatibilityStatus
{
    /// <summary>All required independent packages resolve uniquely to catalogue .npk files.</summary>
    Compatible = 0,

    /// <summary>Main RouterOS package missing from inventory or catalogue.</summary>
    MissingMainPackage = 1,

    /// <summary>An optional independent package has no catalogue match.</summary>
    MissingOptionalPackage = 2,

    /// <summary>Multiple catalogue candidates matched a package.</summary>
    AmbiguousPackage = 3,

    /// <summary>Installed or target version could not be used for resolution.</summary>
    VersionMismatch = 4,

    /// <summary>Architecture missing or incompatible with catalogue packages.</summary>
    ArchitectureMismatch = 5,

    /// <summary>Inventory empty or otherwise unusable.</summary>
    InvalidInventory = 6,

    /// <summary>RouterOS 7 target unavailable when that stage was required.</summary>
    TargetUnavailable = 7,

    /// <summary>Stage not applicable for this device (for example v6 stepping on non-v6).</summary>
    NotApplicable = 8,
}

/// <summary>
/// Credential-free device inventory input for package compatibility auditing.
/// </summary>
public sealed record PackageCompatibilityAuditDevice
{
    /// <summary>RouterOS identity when known.</summary>
    public string? RouterIdentity { get; init; }

    /// <summary>Router management IP as text (fixture or live).</summary>
    public required string RouterIp { get; init; }

    /// <summary>Raw installed RouterOS version string.</summary>
    public string? RouterOsVersionRaw { get; init; }

    /// <summary>Device architecture name (for example mipsbe).</summary>
    public string? Architecture { get; init; }

    /// <summary>Installed package inventory used for resolution.</summary>
    public required IReadOnlyList<InstalledPackageInfo> Packages { get; init; }

    /// <summary>
    /// e.g. <c>SyntheticFixture</c>, <c>CapturedInventory</c>, <c>LiveReadOnly</c>.
    /// </summary>
    public required string InventorySource { get; init; }

    /// <summary>Optional synthetic fixture name for non-live audits.</summary>
    public string? FixtureName { get; init; }
}

/// <summary>
/// Compatibility outcome for one device and one upgrade stage.
/// </summary>
public sealed class PackageCompatibilityStageResult
{
    /// <summary>Router IP copied from the audit device.</summary>
    public required string RouterIp { get; init; }

    /// <summary>Identity when known.</summary>
    public string? RouterIdentity { get; init; }

    /// <summary>Installed RouterOS version string used for the stage.</summary>
    public string? InstalledRouterOsVersion { get; init; }

    /// <summary>Architecture used for resolution.</summary>
    public string? Architecture { get; init; }

    /// <summary>Which upgrade stage this row evaluates.</summary>
    public required PackageCompatibilityUpgradeStage Stage { get; init; }

    /// <summary>Target RouterOS version for the stage, when available.</summary>
    public string? TargetRouterOsVersion { get; init; }

    /// <summary>Catalogue compatibility status for this stage.</summary>
    public required CatalogueCompatibilityStatus Status { get; init; }

    /// <summary>Inventory source label from the device input.</summary>
    public required string InventorySource { get; init; }

    /// <summary>Fixture name when auditing synthetic inventories.</summary>
    public string? FixtureName { get; init; }

    /// <summary>Names of independently installed packages considered.</summary>
    public IReadOnlyList<string> IndependentPackageNames { get; init; } = [];

    /// <summary>Count of bundled component rows in the inventory.</summary>
    public int BundledComponentCount { get; init; }

    /// <summary>Catalogue .npk file names successfully resolved.</summary>
    public IReadOnlyList<string> ResolvedPackageFileNames { get; init; } = [];

    /// <summary>Installed package names that could not be resolved.</summary>
    public IReadOnlyList<string> MissingPackageNames { get; init; } = [];

    /// <summary>Ambiguous catalogue candidate file names when status is AmbiguousPackage.</summary>
    public IReadOnlyList<string> AmbiguousPackageCandidates { get; init; } = [];

    /// <summary>Human-readable explanation of the status.</summary>
    public required string Explanation { get; init; }

    /// <summary>Whether identity policy marks the device eligible for RouterOS 7.</summary>
    public bool IsEligibleForV7Policy { get; init; }

    /// <summary>Classified device type from identity.</summary>
    public DeviceType DeviceType { get; init; }
}

/// <summary>
/// Full compatibility report for one device across applicable stages.
/// </summary>
public sealed class PackageCompatibilityDeviceReport
{
    /// <summary>Input device inventory.</summary>
    public required PackageCompatibilityAuditDevice Device { get; init; }

    /// <summary>Identity classification used for reporting and eligibility.</summary>
    public required DeviceIdentityClassification Classification { get; init; }

    /// <summary>Per-stage resolution outcomes.</summary>
    public required IReadOnlyList<PackageCompatibilityStageResult> Stages { get; init; }
}

/// <summary>
/// One architecture's catalogue coverage for a target version.
/// </summary>
public sealed class CatalogueCoverageArchitectureRow
{
    /// <summary>Normalized architecture token.</summary>
    public required string Architecture { get; init; }

    /// <summary>Target RouterOS version as text.</summary>
    public required string TargetVersion { get; init; }

    /// <summary>Main routeros .npk file name when present.</summary>
    public string? MainPackageFileName { get; init; }

    /// <summary>Non-main independent package file names available for this arch/version.</summary>
    public IReadOnlyList<string> IndependentOptionalPackageFileNames { get; init; } = [];

    /// <summary>
    /// Coverage status string: <c>MissingMain</c>, <c>MainOnly</c>, or <c>MainAndOptionals</c>.
    /// </summary>
    public required string CatalogueStatus { get; init; }
}
