// -----------------------------------------------------------------------------
// File: UpgradePackageResolver.cs
//
// Description:
// Selects catalogue .npk packages for an upgrade stage. The target RouterOS
// release and architecture are authoritative: source inventory identifies the
// installation layout and which independently installed packages may still have
// a separate target artefact, but discontinued or architecture-exclusive source
// packages never fail resolution. Bundled components never get their own .npk.
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
    /// <summary>True when the target core and selected package set resolved uniquely.</summary>
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

    /// <summary>
    /// Target package layout after resolution (combined <c>routeros</c> or individual <c>system</c>).
    /// </summary>
    public FirmwarePackageLayout Layout { get; init; }

    /// <summary>Structured per-package diagnostics (selected, discontinued, arch-exclusive, …).</summary>
    public IReadOnlyList<PackageResolutionDiagnostic> PackageDiagnostics { get; init; } = [];

    /// <summary>
    /// Human-readable diagnostics derived from <see cref="PackageDiagnostics"/> for logging.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
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
/// Target catalogue presence is authoritative; source inventory is not a mandatory target package list.
/// </summary>
internal sealed class UpgradePackageResolver(
    IFirmwarePackageCatalog catalog,
    Microsoft.Extensions.Options.IOptions<FirmwareFilesOptions> firmwareFilesOptions)
    : IUpgradePackageResolver
{
    /// <summary>
    /// Result of looking up a candidate package name in the on-disk target catalogue for the
    /// selected architecture and version.
    /// </summary>
    private enum CatalogMatchKind
    {
        /// <summary>Exactly one catalogue entry matched the requested identity/architecture/version.</summary>
        Matched,

        /// <summary>No package of that name/version exists in the target release catalogue at all.</summary>
        AbsentFromRelease,

        /// <summary>
        /// The package exists for the target version but not for the device architecture
        /// (discontinued for this arch or never shipped).
        /// </summary>
        AbsentForArchitecture,

        /// <summary>Multiple catalogue entries matched; resolution fails closed.</summary>
        Ambiguous,
    }

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
                $"No compatible RouterOS firmware package set was found for architecture '{normalizedArch}' "
                + $"and target version '{targetVersion}' (catalogue has no packages for that version).");
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
                "No independently installed packages were found "
                + "(expected a combined 'routeros'/'routeros-{arch}' package or an individual 'system' package).");
        }

        List<InstalledPackageInfo> mainPackages = independentlyInstalled
            .Where(static p => RouterOsInstalledPackageNaming.IsMainRouterOsPackage(p.Name))
            .ToList();
        List<InstalledPackageInfo> systemPackages = independentlyInstalled
            .Where(static p => IsSystemPackage(p.Name))
            .ToList();

        if (mainPackages.Count > 1)
        {
            return Fail(
                PackageResolutionFailureKind.MultipleMainPackages,
                $"Multiple main RouterOS packages are installed: {string.Join(", ", mainPackages.Select(p => p.Name))}.",
                ambiguousCandidates: mainPackages.Select(static p => p.Name).ToArray());
        }

        if (systemPackages.Count > 1)
        {
            return Fail(
                PackageResolutionFailureKind.MultipleMainPackages,
                $"Multiple independent 'system' packages are installed: {string.Join(", ", systemPackages.Select(p => p.Name))}.",
                ambiguousCandidates: systemPackages.Select(static p => p.Name).ToArray());
        }

        if (mainPackages.Count == 1 && systemPackages.Count == 1)
        {
            return Fail(
                PackageResolutionFailureKind.MultipleMainPackages,
                "Inventory mixes combined and individual RouterOS layouts: both an independent "
                + $"'{mainPackages[0].Name}' package and an independent 'system' package are installed.",
                ambiguousCandidates: [mainPackages[0].Name, systemPackages[0].Name]);
        }

        if (mainPackages.Count == 1)
        {
            return ResolveCombined(
                normalizedArch,
                firmwareRoot,
                policyCatalog,
                independentlyInstalled,
                targetVersion);
        }

        if (systemPackages.Count == 1)
        {
            return ResolveIndividual(
                normalizedArch,
                firmwareRoot,
                policyCatalog,
                independentlyInstalled,
                systemPackages[0],
                targetVersion);
        }

        return Fail(
            PackageResolutionFailureKind.MissingMainPackage,
            $"No compatible RouterOS firmware package set was found for architecture '{normalizedArch}' "
            + $"and target version '{targetVersion}'. "
            + "Inventory has neither an independent combined 'routeros'/'routeros-{arch}' package "
            + "nor an individual 'system' package.");
    }

    /// <summary>
    /// Combined source layout: require the target combined <c>routeros</c> core; select only independents
    /// that the target catalogue still distributes for this architecture.
    /// </summary>
    private static UpgradePackageResolution ResolveCombined(
        string normalizedArch,
        string firmwareRoot,
        IReadOnlyList<FirmwarePackageInfo> policyCatalog,
        IReadOnlyList<InstalledPackageInfo> independentlyInstalled,
        RouterOsVersion targetVersion)
    {
        List<PackageResolutionDiagnostic> diagnostics = [];
        List<ResolvedUpgradePackage> selected = [];

        foreach (InstalledPackageInfo installed in independentlyInstalled)
        {
            bool isCore = IsMainRouterOsPackage(installed.Name);
            UpgradePackageResolution? failure = TryResolveInstalledPackage(
                installed,
                normalizedArch,
                firmwareRoot,
                policyCatalog,
                targetVersion,
                catalogNameOverride: null,
                requireMatch: isCore,
                isCore,
                selected,
                diagnostics);
            if (failure is not null)
            {
                return WithLayout(failure, FirmwarePackageLayout.Combined, diagnostics);
            }
        }

        UpgradePackageResolution? duplicateFailure = FailOnDuplicateSelections(selected);
        if (duplicateFailure is not null)
        {
            return WithLayout(duplicateFailure, FirmwarePackageLayout.Combined, diagnostics);
        }

        return Success(FirmwarePackageLayout.Combined, selected, diagnostics);
    }

    /// <summary>
    /// Individual source layout: target catalogue chooses <c>system</c> or <c>routeros</c> core.
    /// Source independents without a separate target artefact are skipped with diagnostics.
    /// </summary>
    private static UpgradePackageResolution ResolveIndividual(
        string normalizedArch,
        string firmwareRoot,
        IReadOnlyList<FirmwarePackageInfo> policyCatalog,
        IReadOnlyList<InstalledPackageInfo> independentlyInstalled,
        InstalledPackageInfo systemPackage,
        RouterOsVersion targetVersion)
    {
        List<PackageResolutionDiagnostic> diagnostics = [];
        List<ResolvedUpgradePackage> selected = [];

        List<FirmwarePackageInfo> systemAnyArch = policyCatalog
            .Where(p => p.PackageName.Equals("system", StringComparison.Ordinal))
            .ToList();
        List<FirmwarePackageInfo> routerosAnyArch = policyCatalog
            .Where(p => p.PackageName.Equals("routeros", StringComparison.Ordinal))
            .ToList();
        bool systemAvailable = systemAnyArch.Any(p => ArchitectureAcceptable(normalizedArch, p.Architecture));
        bool routerosAvailable = routerosAnyArch.Any(p => ArchitectureAcceptable(normalizedArch, p.Architecture));

        string? coreCatalogOverride = null;
        FirmwarePackageLayout targetLayout = FirmwarePackageLayout.Individual;
        if (!systemAvailable)
        {
            if (systemAnyArch.Count > 0 && !routerosAvailable)
            {
                string found = string.Join(
                    ", ",
                    systemAnyArch.Select(static p => p.Architecture ?? "<none>").Distinct(StringComparer.Ordinal));
                return Fail(
                    PackageResolutionFailureKind.MissingPackage,
                    $"Firmware package architecture mismatch for core 'system'. "
                    + $"Expected: {normalizedArch}. Found: {found}.",
                    failedInstalledPackageName: systemPackage.Name,
                    layout: FirmwarePackageLayout.Individual);
            }

            if (!routerosAvailable)
            {
                string detail = routerosAnyArch.Count > 0
                    ? $"Firmware package architecture mismatch for core 'routeros'. Expected: {normalizedArch}. Found: "
                      + string.Join(
                          ", ",
                          routerosAnyArch.Select(static p => p.Architecture ?? "<none>")
                              .Distinct(StringComparer.Ordinal))
                      + "."
                    : $"Individual package layout detected, but the required core package set is incomplete for "
                      + $"architecture '{normalizedArch}' and target version '{targetVersion}'. "
                      + "Neither 'system' nor 'routeros' catalogue packages are available.";
                return Fail(
                    PackageResolutionFailureKind.MissingPackage,
                    detail,
                    failedInstalledPackageName: systemPackage.Name,
                    layout: FirmwarePackageLayout.Individual);
            }

            coreCatalogOverride = "routeros";
            targetLayout = FirmwarePackageLayout.Combined;
            diagnostics.Add(new PackageResolutionDiagnostic
            {
                PackageIdentity = "system",
                SourceVersion = systemPackage.Version,
                TargetVersion = targetVersion.ToString(),
                TargetArchitecture = normalizedArch,
                Disposition = PackageResolutionDisposition.NoSeparateTargetCounterpart,
                Detail =
                    $"Source individual 'system' has no separate target counterpart; target core is combined "
                    + $"'routeros' for {targetVersion}/{normalizedArch}.",
            });
        }

        UpgradePackageResolution? coreFailure = TryResolveInstalledPackage(
            systemPackage,
            normalizedArch,
            firmwareRoot,
            policyCatalog,
            targetVersion,
            coreCatalogOverride,
            requireMatch: true,
            isCore: true,
            selected,
            diagnostics);
        if (coreFailure is not null)
        {
            return WithLayout(
                coreFailure,
                targetLayout,
                diagnostics,
                coreFailure.FailureReason is null
                    ? null
                    : "Individual package layout detected, but the required package set is incomplete. "
                      + coreFailure.FailureReason);
        }

        foreach (InstalledPackageInfo installed in independentlyInstalled)
        {
            if (IsSystemPackage(installed.Name))
            {
                continue;
            }

            UpgradePackageResolution? failure = TryResolveInstalledPackage(
                installed,
                normalizedArch,
                firmwareRoot,
                policyCatalog,
                targetVersion,
                catalogNameOverride: null,
                requireMatch: false,
                isCore: false,
                selected,
                diagnostics);
            if (failure is not null)
            {
                return WithLayout(failure, targetLayout, diagnostics, failure.FailureReason);
            }
        }

        if (selected.Count == 0)
        {
            return Fail(
                PackageResolutionFailureKind.MissingPackage,
                $"Individual package layout detected, but no catalogue packages were selected for "
                + $"architecture '{normalizedArch}' and target version '{targetVersion}'.",
                layout: targetLayout);
        }

        UpgradePackageResolution? duplicateFailure = FailOnDuplicateSelections(selected);
        if (duplicateFailure is not null)
        {
            return WithLayout(duplicateFailure, targetLayout, diagnostics);
        }

        return Success(targetLayout, selected, diagnostics);
    }

    /// <summary>
    /// Resolves one independent source package against the target catalogue.
    /// When <paramref name="requireMatch"/> is false, absence is a diagnostic rather than a failure.
    /// </summary>
    private static UpgradePackageResolution? TryResolveInstalledPackage(
        InstalledPackageInfo installed,
        string normalizedArch,
        string firmwareRoot,
        IReadOnlyList<FirmwarePackageInfo> policyCatalog,
        RouterOsVersion targetVersion,
        string? catalogNameOverride,
        bool requireMatch,
        bool isCore,
        List<ResolvedUpgradePackage> selected,
        List<PackageResolutionDiagnostic> diagnostics)
    {
        string catalogName = catalogNameOverride
            ?? NormalizeInstalledPackageName(installed.Name, normalizedArch);
        CatalogMatchKind kind = MatchCatalogPackage(
            catalogName,
            normalizedArch,
            policyCatalog,
            out List<FirmwarePackageInfo> matches,
            out IReadOnlyList<string> otherArchitectures);

        string targetVersionText = targetVersion.ToString();

        switch (kind)
        {
            case CatalogMatchKind.Matched:
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
                diagnostics.Add(new PackageResolutionDiagnostic
                {
                    PackageIdentity = installed.Name,
                    SourceVersion = installed.Version,
                    TargetVersion = targetVersionText,
                    TargetArchitecture = normalizedArch,
                    Disposition = isCore
                        ? PackageResolutionDisposition.CorePackage
                        : PackageResolutionDisposition.Selected,
                    Detail = $"Selected target package '{match.FileName}'.",
                });
                return null;

            case CatalogMatchKind.Ambiguous:
                diagnostics.Add(new PackageResolutionDiagnostic
                {
                    PackageIdentity = installed.Name,
                    SourceVersion = installed.Version,
                    TargetVersion = targetVersionText,
                    TargetArchitecture = normalizedArch,
                    Disposition = PackageResolutionDisposition.Rejected,
                    Detail = $"Ambiguous target packages: {string.Join(", ", matches.Select(static m => m.FileName))}.",
                    OtherArchitectures = otherArchitectures,
                });
                return Fail(
                    PackageResolutionFailureKind.AmbiguousPackage,
                    $"Ambiguous target packages for '{installed.Name}': {string.Join(", ", matches.Select(m => m.FileName))}.",
                    failedInstalledPackageName: installed.Name,
                    ambiguousCandidates: matches.Select(static m => m.FileName).ToArray());

            case CatalogMatchKind.AbsentForArchitecture:
                diagnostics.Add(new PackageResolutionDiagnostic
                {
                    PackageIdentity = installed.Name,
                    SourceVersion = installed.Version,
                    TargetVersion = targetVersionText,
                    TargetArchitecture = normalizedArch,
                    Disposition = PackageResolutionDisposition.NotDistributedForTargetArchitecture,
                    Detail =
                        $"No separate target counterpart for architecture '{normalizedArch}' "
                        + $"(present for: {string.Join(", ", otherArchitectures)}).",
                    OtherArchitectures = otherArchitectures,
                });
                if (requireMatch)
                {
                    return Fail(
                        PackageResolutionFailureKind.MissingPackage,
                        $"Firmware package architecture mismatch for '{installed.Name}'. "
                        + $"Expected: {normalizedArch}. Found: {string.Join(", ", otherArchitectures)}.",
                        failedInstalledPackageName: installed.Name);
                }

                return null;

            case CatalogMatchKind.AbsentFromRelease:
                diagnostics.Add(new PackageResolutionDiagnostic
                {
                    PackageIdentity = installed.Name,
                    SourceVersion = installed.Version,
                    TargetVersion = targetVersionText,
                    TargetArchitecture = normalizedArch,
                    Disposition = PackageResolutionDisposition.NotDistributedInTargetRelease,
                    Detail =
                        $"No separate target counterpart in release {targetVersionText} "
                        + $"(not distributed for any architecture).",
                });
                if (requireMatch)
                {
                    return Fail(
                        PackageResolutionFailureKind.MissingPackage,
                        $"No target package found for independently installed package '{installed.Name}' "
                        + $"(catalog name '{catalogName}', architecture '{normalizedArch}', version {targetVersion}).",
                        failedInstalledPackageName: installed.Name);
                }

                return null;

            default:
                return Fail(
                    PackageResolutionFailureKind.MissingPackage,
                    $"Unexpected catalogue match state for '{installed.Name}'.",
                    failedInstalledPackageName: installed.Name);
        }
    }

    /// <summary>
    /// Classifies catalogue matches for a package identity against the device architecture.
    /// </summary>
    private static CatalogMatchKind MatchCatalogPackage(
        string catalogName,
        string normalizedArch,
        IReadOnlyList<FirmwarePackageInfo> policyCatalog,
        out List<FirmwarePackageInfo> matches,
        out IReadOnlyList<string> otherArchitectures)
    {
        matches = policyCatalog
            .Where(p => p.PackageName.Equals(catalogName, StringComparison.Ordinal)
                && ArchitectureAcceptable(normalizedArch, p.Architecture))
            .ToList();

        List<FirmwarePackageInfo> sameName = policyCatalog
            .Where(p => p.PackageName.Equals(catalogName, StringComparison.Ordinal))
            .ToList();
        otherArchitectures = sameName
            .Select(static p => p.Architecture ?? "<none>")
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static a => a, StringComparer.Ordinal)
            .ToArray();

        if (matches.Count == 1)
        {
            return CatalogMatchKind.Matched;
        }

        if (matches.Count > 1)
        {
            return CatalogMatchKind.Ambiguous;
        }

        if (sameName.Count > 0)
        {
            return CatalogMatchKind.AbsentForArchitecture;
        }

        return CatalogMatchKind.AbsentFromRelease;
    }

    /// <summary>
    /// Fails when two selected rows resolve to the same post-upgrade installed identity.
    /// </summary>
    private static UpgradePackageResolution? FailOnDuplicateSelections(
        IReadOnlyList<ResolvedUpgradePackage> selected)
    {
        List<IGrouping<string, ResolvedUpgradePackage>> duplicates = selected
            .GroupBy(static p => p.ExpectedInstalledPackageName, StringComparer.OrdinalIgnoreCase)
            .Where(static g => g.Count() > 1)
            .ToList();
        if (duplicates.Count == 0)
        {
            return null;
        }

        IGrouping<string, ResolvedUpgradePackage> first = duplicates[0];
        return Fail(
            PackageResolutionFailureKind.AmbiguousPackage,
            $"Duplicate resolved package identity '{first.Key}' from installed packages: "
            + string.Join(", ", first.Select(static p => p.InstalledPackageName)) + ".",
            ambiguousCandidates: first.Select(static p => p.CatalogPackage.FileName).Distinct(StringComparer.Ordinal).ToArray());
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
    /// Builds a successful resolution result with structured diagnostics.
    /// </summary>
    private static UpgradePackageResolution Success(
        FirmwarePackageLayout layout,
        IReadOnlyList<ResolvedUpgradePackage> packages,
        IReadOnlyList<PackageResolutionDiagnostic> diagnostics) =>
        new()
        {
            Succeeded = true,
            FailureKind = PackageResolutionFailureKind.None,
            Packages = packages,
            Layout = layout,
            PackageDiagnostics = diagnostics,
            Diagnostics = diagnostics.Select(static d => d.ToString()).ToArray(),
        };

    /// <summary>
    /// Builds a failed resolution result.
    /// </summary>
    private static UpgradePackageResolution Fail(
        PackageResolutionFailureKind kind,
        string reason,
        string? failedInstalledPackageName = null,
        IReadOnlyList<string>? ambiguousCandidates = null,
        FirmwarePackageLayout layout = FirmwarePackageLayout.Unknown,
        IReadOnlyList<PackageResolutionDiagnostic>? diagnostics = null) =>
        new()
        {
            Succeeded = false,
            FailureKind = kind,
            FailureReason = reason,
            FailedInstalledPackageName = failedInstalledPackageName,
            AmbiguousCandidates = ambiguousCandidates ?? [],
            Layout = layout,
            PackageDiagnostics = diagnostics ?? [],
            Diagnostics = diagnostics?.Select(static d => d.ToString()).ToArray() ?? [],
        };

    /// <summary>
    /// Copies a failed resolution while attaching layout, diagnostics and an optional reason override.
    /// </summary>
    private static UpgradePackageResolution WithLayout(
        UpgradePackageResolution source,
        FirmwarePackageLayout layout,
        IReadOnlyList<PackageResolutionDiagnostic>? diagnostics = null,
        string? failureReasonOverride = null)
    {
        IReadOnlyList<PackageResolutionDiagnostic> merged = diagnostics ?? source.PackageDiagnostics;
        return new UpgradePackageResolution
        {
            Succeeded = source.Succeeded,
            FailureKind = source.FailureKind,
            FailureReason = failureReasonOverride ?? source.FailureReason,
            FailedInstalledPackageName = source.FailedInstalledPackageName,
            AmbiguousCandidates = source.AmbiguousCandidates,
            Packages = source.Packages,
            Layout = layout,
            PackageDiagnostics = merged,
            Diagnostics = merged.Select(static d => d.ToString()).ToArray(),
        };
    }
}
