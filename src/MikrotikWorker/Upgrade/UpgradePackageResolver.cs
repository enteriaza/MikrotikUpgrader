using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

internal sealed record ResolvedUpgradePackage(
    string InstalledPackageName,
    FirmwarePackageInfo CatalogPackage,
    string RelativeUrlPath);

internal sealed class UpgradePackageResolution
{
    public required bool Succeeded { get; init; }

    public string? FailureReason { get; init; }

    public PackageResolutionFailureKind FailureKind { get; init; }

    /// <summary>
    /// Installed package name that could not be resolved (when applicable).
    /// </summary>
    public string? FailedInstalledPackageName { get; init; }

    /// <summary>
    /// Ambiguous catalogue filenames (when <see cref="FailureKind"/> is AmbiguousPackage).
    /// </summary>
    public IReadOnlyList<string> AmbiguousCandidates { get; init; } = [];

    public IReadOnlyList<ResolvedUpgradePackage> Packages { get; init; } = [];
}

internal interface IUpgradePackageResolver
{
    UpgradePackageResolution Resolve(
        string? deviceArchitecture,
        IReadOnlyList<InstalledPackageInfo> installedPackages,
        RouterOsVersion targetVersion);
}

internal sealed class UpgradePackageResolver(
    IFirmwarePackageCatalog catalog,
    Microsoft.Extensions.Options.IOptions<FirmwareFilesOptions> firmwareFilesOptions)
    : IUpgradePackageResolver
{
    public UpgradePackageResolution Resolve(
        string? deviceArchitecture,
        IReadOnlyList<InstalledPackageInfo> installedPackages,
        RouterOsVersion targetVersion)
    {
        if (string.IsNullOrWhiteSpace(deviceArchitecture))
        {
            return Fail(
                PackageResolutionFailureKind.ArchitectureMissing,
                "Device architecture is missing; package selection is blocked.");
        }

        if (installedPackages.Count == 0)
        {
            return Fail(
                PackageResolutionFailureKind.InventoryEmpty,
                "Installed package inventory is empty.");
        }

        string normalizedArch = FirmwarePackageFileNameParser.NormalizeArchitecture(deviceArchitecture);
        string firmwareRoot = firmwareFilesOptions.Value.GetAbsolutePath();

        List<FirmwarePackageInfo> policyCatalog = catalog.GetPackages()
            .Where(p => p.Version == targetVersion)
            .ToList();

        if (policyCatalog.Count == 0)
        {
            return Fail(
                PackageResolutionFailureKind.NoPackagesForTarget,
                $"No packages for target {targetVersion} were found in FirmwareFiles.");
        }

        // Only independently installed packages need their own .npk.
        // Bundled components (bundle=<main>) are upgraded by the combined routeros-* package.
        List<InstalledPackageInfo> independentlyInstalled = installedPackages
            .Where(static p => !p.IsBundledComponent)
            .ToList();

        if (independentlyInstalled.Count == 0)
        {
            return Fail(
                PackageResolutionFailureKind.NoIndependentPackages,
                "No independently installed packages were found (expected a main routeros-* package).");
        }

        List<InstalledPackageInfo> mainPackages = independentlyInstalled
            .Where(static p => IsMainRouterOsPackage(p.Name))
            .ToList();

        if (mainPackages.Count == 0)
        {
            return Fail(
                PackageResolutionFailureKind.MissingMainPackage,
                "No main combined RouterOS package was found. Expected an unbundled 'routeros' or 'routeros-{arch}' entry.");
        }

        if (mainPackages.Count > 1)
        {
            return Fail(
                PackageResolutionFailureKind.MultipleMainPackages,
                $"Multiple main RouterOS packages are installed: {string.Join(", ", mainPackages.Select(p => p.Name))}.",
                ambiguousCandidates: mainPackages.Select(static p => p.Name).ToArray());
        }

        List<ResolvedUpgradePackage> selected = [];
        foreach (InstalledPackageInfo installed in independentlyInstalled)
        {
            string catalogName = NormalizeInstalledPackageName(installed.Name, normalizedArch);
            List<FirmwarePackageInfo> matches = policyCatalog
                .Where(p => p.PackageName.Equals(catalogName, StringComparison.Ordinal)
                    && ArchitectureAcceptable(normalizedArch, p.Architecture))
                .ToList();

            if (matches.Count == 0)
            {
                return Fail(
                    PackageResolutionFailureKind.MissingPackage,
                    $"No target package found for independently installed package '{installed.Name}' (catalog name '{catalogName}', architecture '{normalizedArch}', version {targetVersion}).",
                    failedInstalledPackageName: installed.Name);
            }

            if (matches.Count > 1)
            {
                return Fail(
                    PackageResolutionFailureKind.AmbiguousPackage,
                    $"Ambiguous target packages for '{installed.Name}': {string.Join(", ", matches.Select(m => m.FileName))}.",
                    failedInstalledPackageName: installed.Name,
                    ambiguousCandidates: matches.Select(static m => m.FileName).ToArray());
            }

            FirmwarePackageInfo match = matches[0];
            selected.Add(new ResolvedUpgradePackage(
                installed.Name,
                match,
                FirmwarePackageCatalog.GetRelativeUrlPath(firmwareRoot, match.FullPath)));
        }

        return new UpgradePackageResolution
        {
            Succeeded = true,
            FailureKind = PackageResolutionFailureKind.None,
            Packages = selected,
        };
    }

    /// <summary>
    /// Maps live package names such as <c>routeros-mipsbe</c> to catalog identity <c>routeros</c>.
    /// </summary>
    internal static string NormalizeInstalledPackageName(string installedName, string normalizedDeviceArch)
    {
        string name = installedName.Trim().ToLowerInvariant();
        if (name.Equals("routeros", StringComparison.Ordinal))
        {
            return "routeros";
        }

        if (name.StartsWith("routeros-", StringComparison.Ordinal))
        {
            string suffix = name["routeros-".Length..];
            if (FirmwarePackageFileNameParser.IsArchitectureToken(suffix)
                || FirmwarePackageFileNameParser.NormalizeArchitecture(suffix) == normalizedDeviceArch)
            {
                return "routeros";
            }
        }

        return name;
    }

    internal static bool IsMainRouterOsPackage(string installedName)
    {
        string name = installedName.Trim().ToLowerInvariant();
        if (name.Equals("routeros", StringComparison.Ordinal))
        {
            return true;
        }

        if (!name.StartsWith("routeros-", StringComparison.Ordinal))
        {
            return false;
        }

        string suffix = name["routeros-".Length..];
        return FirmwarePackageFileNameParser.IsArchitectureToken(suffix);
    }

    internal static bool IsSystemPackage(string installedName) =>
        installedName.Trim().Equals("system", StringComparison.OrdinalIgnoreCase);

    internal static bool ArchitectureAcceptable(string deviceArch, string? packageArch)
    {
        if (string.IsNullOrWhiteSpace(packageArch))
        {
            return deviceArch.Equals("x86", StringComparison.Ordinal);
        }

        return FirmwarePackageFileNameParser.ArchitecturesMatch(deviceArch, packageArch);
    }

    private static UpgradePackageResolution Fail(
        PackageResolutionFailureKind kind,
        string reason,
        string? failedInstalledPackageName = null,
        IReadOnlyList<string>? ambiguousCandidates = null) =>
        new()
        {
            Succeeded = false,
            FailureKind = kind,
            FailureReason = reason,
            FailedInstalledPackageName = failedInstalledPackageName,
            AmbiguousCandidates = ambiguousCandidates ?? [],
        };
}
