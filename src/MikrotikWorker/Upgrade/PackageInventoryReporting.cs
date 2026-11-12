// -----------------------------------------------------------------------------
// File: PackageInventoryReporting.cs
//
// Description:
// Splits installed package inventories into independent vs bundled components
// for logging and verification.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Helpers that summarize and format installed package inventories for upgrade logging.
/// </summary>
internal static class PackageInventoryReporting
{
    /// <summary>
    /// Partitions packages into independent installs versus bundled components.
    /// </summary>
    public static PackageInventorySummary Summarize(IReadOnlyList<InstalledPackageInfo> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);

        List<InstalledPackageInfo> independent = packages
            .Where(static p => !p.IsBundledComponent)
            .ToList();
        List<InstalledPackageInfo> bundled = packages
            .Where(static p => p.IsBundledComponent)
            .ToList();

        return new PackageInventorySummary(
            IndependentCount: independent.Count,
            IndependentPackages: independent,
            BundledCount: bundled.Count,
            BundledComponents: bundled);
    }

    /// <summary>
    /// Formats packages as a comma-separated <c>name/version</c> list for log messages.
    /// </summary>
    public static string FormatIndependentPackages(IReadOnlyList<InstalledPackageInfo> packages) =>
        string.Join(", ", packages.Select(static p => $"{p.Name}/{p.Version}"));
}

/// <summary>
/// Counts and lists of independent versus bundled installed packages.
/// </summary>
/// <param name="IndependentCount">Number of non-bundled installed packages.</param>
/// <param name="IndependentPackages">Non-bundled installed packages.</param>
/// <param name="BundledCount">Number of bundled component rows.</param>
/// <param name="BundledComponents">Bundled component rows.</param>
internal sealed record PackageInventorySummary(
    int IndependentCount,
    IReadOnlyList<InstalledPackageInfo> IndependentPackages,
    int BundledCount,
    IReadOnlyList<InstalledPackageInfo> BundledComponents);
