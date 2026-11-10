using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

internal static class PackageInventoryReporting
{
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

    public static string FormatIndependentPackages(IReadOnlyList<InstalledPackageInfo> packages) =>
        string.Join(", ", packages.Select(static p => $"{p.Name}/{p.Version}"));
}

internal sealed record PackageInventorySummary(
    int IndependentCount,
    IReadOnlyList<InstalledPackageInfo> IndependentPackages,
    int BundledCount,
    IReadOnlyList<InstalledPackageInfo> BundledComponents);
