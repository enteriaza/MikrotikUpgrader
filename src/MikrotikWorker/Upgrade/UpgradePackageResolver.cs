// -----------------------------------------------------------------------------
// File: UpgradePackageResolver.cs
//
// Description:
// Selects catalogue .npk packages for an upgrade stage from installed independent
// packages and device architecture. Bundled components do not get their own .npk.
// Produces ResolvedUpgradePackage rows with version-aware expected installed names.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// A catalogue package selected for upgrade.
/// <see cref="InstalledPackageName"/> is the pre-upgrade independent package that drove selection.
/// <see cref="ExpectedInstalledPackageName"/> is the name RouterOS must report after the upgrade
/// (version-aware for the main package: v6 <c>routeros-{arch}</c>, v7+ <c>routeros</c>).
/// </summary>
/// <param name="InstalledPackageName">Pre-upgrade independent installed package name.</param>
/// <param name="ExpectedInstalledPackageName">Name expected in post-upgrade inventory.</param>
/// <param name="CatalogPackage">Matched catalogue package metadata.</param>
/// <param name="RelativeUrlPath">Path under the advertised firmware base URL used for fetch.</param>
internal sealed record ResolvedUpgradePackage(
    string InstalledPackageName,
    string ExpectedInstalledPackageName,
    FirmwarePackageInfo CatalogPackage,
    string RelativeUrlPath);

/// <summary>
/// Result of resolving required upgrade packages for one stage.
/// </summary>
internal sealed class UpgradePackageResolution
{
    /// <summary>True when every independent package resolved uniquely.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Failure detail when <see cref="Succeeded"/> is false.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Structured failure kind (or <see cref="PackageResolutionFailureKind.None"/>).</summary>
    public PackageResolutionFailureKind FailureKind { get; init; }

    /// <summary>
    /// Installed package name that could not be resolved (when applicable).
    /// </summary>
    public string? FailedInstalledPackageName { get; init; }

    /// <summary>
    /// Ambiguous catalogue filenames (when <see cref="FailureKind"/> is AmbiguousPackage).
    /// </summary>
    public IReadOnlyList<string> AmbiguousCandidates { get; init; } = [];

    /// <summary>Resolved packages when successful; empty on failure.</summary>
    public IReadOnlyList<ResolvedUpgradePackage> Packages { get; init; } = [];
}

/// <summary>
/// Resolves catalogue packages required to upgrade installed independent packages to a target version.
/// </summary>
internal interface IUpgradePackageResolver
{
    /// <summary>
    /// Resolves .npk packages for <paramref name="targetVersion"/> from the device architecture
    /// and independently installed package inventory.
    /// </summary>
    UpgradePackageResolution Resolve(
        string? deviceArchitecture,
        IReadOnlyList<InstalledPackageInfo> installedPackages,
        RouterOsVersion targetVersion);
}

/// <summary>
/// Default <see cref="IUpgradePackageResolver"/> over <see cref="IFirmwarePackageCatalog"/>.
/// </summary>
internal sealed class UpgradePackageResolver(
    IFirmwarePackageCatalog catalog,
    Microsoft.Extensions.Options.IOptions<FirmwareFilesOptions> firmwareFilesOptions)
    : IUpgradePackageResolver
{
    /// <inheritdoc />
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
            .Where(static p => RouterOsInstalledPackageNaming.IsMainRouterOsPackage(p.Name))
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
            string expectedInstalled = RouterOsInstalledPackageNaming.ExpectedInstalledPackageName(
                match.PackageName,
                targetVersion,
                normalizedArch,
                match.Architecture);
            selected.Add(new ResolvedUpgradePackage(
                installed.Name,
                expectedInstalled,
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

    /// <summary>
    /// Whether the installed name is a main combined RouterOS package.
    /// </summary>
    internal static bool IsMainRouterOsPackage(string installedName) =>
        RouterOsInstalledPackageNaming.IsMainRouterOsPackage(installedName);

    /// <summary>
    /// Whether the installed name is the legacy <c>system</c> package.
    /// </summary>
    internal static bool IsSystemPackage(string installedName) =>
        installedName.Trim().Equals("system", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a catalogue package architecture is acceptable for the device architecture.
    /// Arch-less packages are accepted only for <c>x86</c>.
    /// </summary>
    internal static bool ArchitectureAcceptable(string deviceArch, string? packageArch)
    {
        if (string.IsNullOrWhiteSpace(packageArch))
        {
            return deviceArch.Equals("x86", StringComparison.Ordinal);
        }

        return FirmwarePackageFileNameParser.ArchitecturesMatch(deviceArch, packageArch);
    }

    /// <summary>
    /// Builds a failed resolution result.
    /// </summary>
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
