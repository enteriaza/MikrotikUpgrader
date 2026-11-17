// -----------------------------------------------------------------------------
// File: PackageResolutionFailureKind.cs
//
// Description:
// Structured failure classification returned by UpgradePackageResolver when
// catalogue selection fails.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Structured failure classification from <see cref="UpgradePackageResolver"/>.
/// </summary>
public enum PackageResolutionFailureKind
{
    /// <summary>Resolution succeeded.</summary>
    None = 0,

    /// <summary>Device architecture was missing or blank.</summary>
    ArchitectureMissing = 1,

    /// <summary>Installed package inventory was empty.</summary>
    InventoryEmpty = 2,

    /// <summary>Catalogue had no packages for the requested target version.</summary>
    NoPackagesForTarget = 3,

    /// <summary>Inventory had no independently installed packages.</summary>
    NoIndependentPackages = 4,

    /// <summary>
    /// Inventory has neither a combined <c>routeros</c>/<c>routeros-{arch}</c> package nor an
    /// individual <c>system</c> package that can anchor resolution.
    /// </summary>
    MissingMainPackage = 5,

    /// <summary>More than one main RouterOS package was installed.</summary>
    MultipleMainPackages = 6,

    /// <summary>A required independent package had no matching catalogue .npk.</summary>
    MissingPackage = 7,

    /// <summary>Multiple catalogue .npk candidates matched a single installed package.</summary>
    AmbiguousPackage = 8,
}
