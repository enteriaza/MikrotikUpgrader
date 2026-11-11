namespace MikrotikWorker.Upgrade;

/// <summary>
/// Structured failure classification from <see cref="UpgradePackageResolver"/>.
/// </summary>
public enum PackageResolutionFailureKind
{
    None = 0,
    ArchitectureMissing = 1,
    InventoryEmpty = 2,
    NoPackagesForTarget = 3,
    NoIndependentPackages = 4,
    MissingMainPackage = 5,
    MultipleMainPackages = 6,
    MissingPackage = 7,
    AmbiguousPackage = 8,
}
