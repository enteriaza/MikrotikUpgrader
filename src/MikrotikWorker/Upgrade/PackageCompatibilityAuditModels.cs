using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

public enum PackageCompatibilityUpgradeStage
{
    V6Stepping = 1,
    RouterOs7 = 2,
}

/// <summary>
/// Catalogue compatibility outcome for an installed independent-package inventory.
/// Distinct from assessment-level <see cref="Assessment.PackageCompatibilityStatus"/>.
/// </summary>
public enum CatalogueCompatibilityStatus
{
    Compatible = 0,
    MissingMainPackage = 1,
    MissingOptionalPackage = 2,
    AmbiguousPackage = 3,
    VersionMismatch = 4,
    ArchitectureMismatch = 5,
    InvalidInventory = 6,
    TargetUnavailable = 7,
    NotApplicable = 8,
}

/// <summary>
/// Credential-free device inventory input for package compatibility auditing.
/// </summary>
public sealed record PackageCompatibilityAuditDevice
{
    public string? RouterIdentity { get; init; }

    public required string RouterIp { get; init; }

    public string? RouterOsVersionRaw { get; init; }

    public string? Architecture { get; init; }

    public required IReadOnlyList<InstalledPackageInfo> Packages { get; init; }

    /// <summary>
    /// e.g. <c>SyntheticFixture</c>, <c>CapturedInventory</c>, <c>LiveReadOnly</c>.
    /// </summary>
    public required string InventorySource { get; init; }

    public string? FixtureName { get; init; }
}

/// <summary>
/// Compatibility outcome for one device and one upgrade stage.
/// </summary>
public sealed class PackageCompatibilityStageResult
{
    public required string RouterIp { get; init; }

    public string? RouterIdentity { get; init; }

    public string? InstalledRouterOsVersion { get; init; }

    public string? Architecture { get; init; }

    public required PackageCompatibilityUpgradeStage Stage { get; init; }

    public string? TargetRouterOsVersion { get; init; }

    public required CatalogueCompatibilityStatus Status { get; init; }

    public required string InventorySource { get; init; }

    public string? FixtureName { get; init; }

    public IReadOnlyList<string> IndependentPackageNames { get; init; } = [];

    public int BundledComponentCount { get; init; }

    public IReadOnlyList<string> ResolvedPackageFileNames { get; init; } = [];

    public IReadOnlyList<string> MissingPackageNames { get; init; } = [];

    public IReadOnlyList<string> AmbiguousPackageCandidates { get; init; } = [];

    public required string Explanation { get; init; }

    public bool IsEligibleForV7Policy { get; init; }

    public DeviceType DeviceType { get; init; }
}

public sealed class PackageCompatibilityDeviceReport
{
    public required PackageCompatibilityAuditDevice Device { get; init; }

    public required DeviceIdentityClassification Classification { get; init; }

    public required IReadOnlyList<PackageCompatibilityStageResult> Stages { get; init; }
}

public sealed class CatalogueCoverageArchitectureRow
{
    public required string Architecture { get; init; }

    public required string TargetVersion { get; init; }

    public string? MainPackageFileName { get; init; }

    public IReadOnlyList<string> IndependentOptionalPackageFileNames { get; init; } = [];

    public required string CatalogueStatus { get; init; }
}
