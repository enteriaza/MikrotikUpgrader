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
            return Fail("Device architecture is missing; package selection is blocked.");
        }

        if (installedPackages.Count == 0)
        {
            return Fail("Installed package inventory is empty.");
        }

        string normalizedArch = FirmwarePackageFileNameParser.NormalizeArchitecture(deviceArchitecture);
        string firmwareRoot = firmwareFilesOptions.Value.GetAbsolutePath();

        List<FirmwarePackageInfo> policyCatalog = catalog.GetPackages()
            .Where(p => p.Version == targetVersion)
            .ToList();

        if (policyCatalog.Count == 0)
        {
            return Fail($"No packages for target {targetVersion} were found in FirmwareFiles.");
        }

        bool hasMain = installedPackages.Any(static p => IsMainRouterOsPackage(p.Name) || IsSystemPackage(p.Name));
        if (!hasMain)
        {
            return Fail("Installed packages do not include a main 'routeros'/'routeros-{arch}' or 'system' package.");
        }

        List<ResolvedUpgradePackage> selected = [];
        foreach (InstalledPackageInfo installed in installedPackages)
        {
            string catalogName = NormalizeInstalledPackageName(installed.Name, normalizedArch);
            List<FirmwarePackageInfo> matches = policyCatalog
                .Where(p => p.PackageName.Equals(catalogName, StringComparison.Ordinal)
                    && ArchitectureAcceptable(normalizedArch, p.Architecture))
                .ToList();

            if (matches.Count == 0)
            {
                return Fail(
                    $"No target package found for installed package '{installed.Name}' (catalog name '{catalogName}', architecture '{normalizedArch}', version {targetVersion}).");
            }

            if (matches.Count > 1)
            {
                return Fail(
                    $"Ambiguous target packages for '{installed.Name}': {string.Join(", ", matches.Select(m => m.FileName))}.");
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

    private static bool ArchitectureAcceptable(string deviceArch, string? packageArch)
    {
        if (string.IsNullOrWhiteSpace(packageArch))
        {
            return deviceArch.Equals("x86", StringComparison.Ordinal);
        }

        return FirmwarePackageFileNameParser.ArchitecturesMatch(deviceArch, packageArch);
    }

    private static UpgradePackageResolution Fail(string reason) =>
        new() { Succeeded = false, FailureReason = reason };
}
