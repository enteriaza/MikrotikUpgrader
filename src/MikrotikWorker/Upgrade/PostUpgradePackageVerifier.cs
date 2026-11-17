// -----------------------------------------------------------------------------
// File: PostUpgradePackageVerifier.cs
//
// Description:
// Strict post-reboot package inventory checks used by RouterOsV6UpgradeExecutor
// after a package stage. Confirms independent installs, the expected core package
// (combined routeros or individual system), each resolved package present at the
// target version, and main-package architecture alignment. RouterOS version string
// alone is never sufficient.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Result of <see cref="PostUpgradePackageVerifier.Verify"/>.
/// </summary>
/// <param name="Succeeded">True when inventory matches every required package at the target version.</param>
/// <param name="FailureOutcome">Outcome to report on failure; <see cref="UpgradeOutcome.UpgradeSucceeded"/> when Succeeded.</param>
/// <param name="Detail">Human-readable failure detail, or null on success.</param>
internal sealed record PostUpgradePackageVerification(
    bool Succeeded,
    UpgradeOutcome FailureOutcome,
    string? Detail);

/// <summary>
/// Strict post-upgrade inventory verification: version string alone is never sufficient.
/// Expected core package names are layout-aware (combined <c>routeros</c>/<c>routeros-{arch}</c>
/// or individual <c>system</c>).
/// </summary>
internal static class PostUpgradePackageVerifier
{
    /// <summary>
    /// Verifies post-upgrade installed packages against the packages that were resolved and staged.
    /// Fails closed on empty inventory, missing/ambiguous core packages, version mismatches, or arch mismatch.
    /// Absence of a combined <c>routeros</c> package is not a failure when the resolved set is an
    /// individual <c>system</c> installation.
    /// </summary>
    /// <param name="targetVersion">Exact RouterOS version the stage targeted.</param>
    /// <param name="expectedArchitecture">Device architecture before upgrade (normalized when compared).</param>
    /// <param name="postPackages">Installed packages read after reconnect.</param>
    /// <param name="requiredPackages">Packages previously resolved for this stage.</param>
    /// <param name="unparsablePackageRows">Count of inventory rows that could not be parsed; any positive value fails.</param>
    public static PostUpgradePackageVerification Verify(
        RouterOsVersion targetVersion,
        string? expectedArchitecture,
        IReadOnlyList<InstalledPackageInfo> postPackages,
        IReadOnlyList<ResolvedUpgradePackage> requiredPackages,
        int unparsablePackageRows = 0)
    {
        ArgumentNullException.ThrowIfNull(postPackages);
        ArgumentNullException.ThrowIfNull(requiredPackages);

        if (unparsablePackageRows > 0)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                $"Package inventory contains {unparsablePackageRows} unparseable row(s).");
        }

        if (postPackages.Count == 0)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                "Post-upgrade package inventory is empty.");
        }

        PackageInventorySummary inventory = PackageInventoryReporting.Summarize(postPackages);
        if (inventory.IndependentCount == 0)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                "Post-upgrade inventory has no independently installed packages.");
        }

        if (requiredPackages.Count == 0)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                "No required packages were resolved for post-upgrade verification.");
        }

        bool requiresRouterOsMain = requiredPackages.Any(static p =>
            RouterOsInstalledPackageNaming.IsMainRouterOsPackage(p.ExpectedInstalledPackageName)
            || p.CatalogPackage.PackageName.Equals("routeros", StringComparison.OrdinalIgnoreCase));

        bool requiresSystemCore = !requiresRouterOsMain
            && requiredPackages.Any(static p =>
                p.ExpectedInstalledPackageName.Equals("system", StringComparison.OrdinalIgnoreCase)
                || (p.InstalledPackageName.Equals("system", StringComparison.OrdinalIgnoreCase)
                    && p.CatalogPackage.PackageName.Equals("system", StringComparison.OrdinalIgnoreCase)));

        if (requiresRouterOsMain)
        {
            PostUpgradePackageVerification? mainFailure = VerifyCombinedMain(
                inventory,
                requiredPackages,
                targetVersion);
            if (mainFailure is not null)
            {
                return mainFailure;
            }
        }
        else if (requiresSystemCore)
        {
            PostUpgradePackageVerification? systemFailure = VerifyIndividualSystemCore(
                inventory,
                targetVersion);
            if (systemFailure is not null)
            {
                return systemFailure;
            }
        }
        else
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                "Resolved package set does not identify a core 'routeros' or 'system' package for verification.");
        }

        string? normalizedExpectedArch = string.IsNullOrWhiteSpace(expectedArchitecture)
            ? null
            : FirmwarePackageFileNameParser.NormalizeArchitecture(expectedArchitecture);

        foreach (ResolvedUpgradePackage required in requiredPackages)
        {
            string expectedName = required.ExpectedInstalledPackageName;
            List<InstalledPackageInfo> matches = postPackages
                .Where(p =>
                    !p.IsBundledComponent
                    && p.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                return Fail(
                    UpgradeOutcome.UpgradePackageMismatch,
                    $"Installed package '{expectedName}' was missing or not at {targetVersion} after upgrade.");
            }

            if (matches.Count > 1)
            {
                return Fail(
                    UpgradeOutcome.UpgradePackageMismatch,
                    $"Ambiguous independent installs of '{expectedName}' after upgrade ({matches.Count} rows).");
            }

            InstalledPackageInfo found = matches[0];
            if (!RouterOsVersion.TryParse(found.Version, out RouterOsVersion pkgVersion)
                || pkgVersion != targetVersion)
            {
                return Fail(
                    UpgradeOutcome.UpgradePackageMismatch,
                    $"Installed package '{expectedName}' was missing or not at {targetVersion} after upgrade (observed '{found.Version}').");
            }

            if (RouterOsInstalledPackageNaming.IsMainRouterOsPackage(expectedName)
                && normalizedExpectedArch is not null
                && required.CatalogPackage.Architecture is string catalogArch
                && !FirmwarePackageFileNameParser.ArchitecturesMatch(normalizedExpectedArch, catalogArch))
            {
                return Fail(
                    UpgradeOutcome.UpgradeFailed,
                    $"Selected package architecture '{catalogArch}' does not match device architecture '{normalizedExpectedArch}'.");
            }
        }

        return new PostUpgradePackageVerification(
            Succeeded: true,
            FailureOutcome: UpgradeOutcome.UpgradeSucceeded,
            Detail: null);
    }

    /// <summary>
    /// Combined-layout core check: exactly one independent main routeros package at the target version.
    /// </summary>
    private static PostUpgradePackageVerification? VerifyCombinedMain(
        PackageInventorySummary inventory,
        IReadOnlyList<ResolvedUpgradePackage> requiredPackages,
        RouterOsVersion targetVersion)
    {
        List<InstalledPackageInfo> mains = inventory.IndependentPackages
            .Where(static p => RouterOsInstalledPackageNaming.IsMainRouterOsPackage(p.Name))
            .ToList();

        if (mains.Count == 0)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                "Post-upgrade inventory is missing an independent main RouterOS package.");
        }

        if (mains.Count > 1)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                $"Post-upgrade inventory has multiple independent main RouterOS packages: {string.Join(", ", mains.Select(static p => p.Name))}.");
        }

        InstalledPackageInfo main = mains[0];
        ResolvedUpgradePackage? requiredMain = requiredPackages.FirstOrDefault(static p =>
            RouterOsInstalledPackageNaming.IsMainRouterOsPackage(p.ExpectedInstalledPackageName)
            || p.CatalogPackage.PackageName.Equals("routeros", StringComparison.OrdinalIgnoreCase));

        if (requiredMain is not null
            && !main.Name.Equals(requiredMain.ExpectedInstalledPackageName, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                $"Expected main package '{requiredMain.ExpectedInstalledPackageName}' after upgrade to {targetVersion}; observed '{main.Name}/{main.Version}'.");
        }

        if (!RouterOsVersion.TryParse(main.Version, out RouterOsVersion mainVersion)
            || mainVersion != targetVersion)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                $"Main package '{main.Name}' version '{main.Version}' does not match target {targetVersion}.");
        }

        return null;
    }

    /// <summary>
    /// Individual-layout core check: exactly one independent <c>system</c> package at the target version.
    /// Combined <c>routeros</c> is not required.
    /// </summary>
    private static PostUpgradePackageVerification? VerifyIndividualSystemCore(
        PackageInventorySummary inventory,
        RouterOsVersion targetVersion)
    {
        List<InstalledPackageInfo> systems = inventory.IndependentPackages
            .Where(static p => UpgradePackageResolver.IsSystemPackage(p.Name))
            .ToList();

        if (systems.Count == 0)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                "Post-upgrade inventory is missing an independent 'system' package "
                + "(individual package layout; combined 'routeros' is not required).");
        }

        if (systems.Count > 1)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                $"Post-upgrade inventory has multiple independent 'system' packages ({systems.Count}).");
        }

        InstalledPackageInfo system = systems[0];
        if (!RouterOsVersion.TryParse(system.Version, out RouterOsVersion systemVersion)
            || systemVersion != targetVersion)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                $"System package version '{system.Version}' does not match target {targetVersion}.");
        }

        return null;
    }

    /// <summary>
    /// Builds a failed verification result with the given outcome and detail.
    /// </summary>
    private static PostUpgradePackageVerification Fail(UpgradeOutcome outcome, string detail) =>
        new(Succeeded: false, FailureOutcome: outcome, Detail: detail);
}
