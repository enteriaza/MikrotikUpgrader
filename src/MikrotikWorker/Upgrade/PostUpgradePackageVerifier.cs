using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

internal sealed record PostUpgradePackageVerification(
    bool Succeeded,
    UpgradeOutcome FailureOutcome,
    string? Detail);

/// <summary>
/// Strict post-upgrade inventory verification: version string alone is never sufficient.
/// Expected main package names are version-aware (v6 <c>routeros-{arch}</c>, v7+ <c>routeros</c>).
/// </summary>
internal static class PostUpgradePackageVerifier
{
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

        if (requiredPackages.Count == 0)
        {
            return Fail(
                UpgradeOutcome.UpgradePackageMismatch,
                "No required packages were resolved for post-upgrade verification.");
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

        return new PostUpgradePackageVerification(
            Succeeded: true,
            FailureOutcome: UpgradeOutcome.UpgradeSucceeded,
            Detail: null);
    }

    private static PostUpgradePackageVerification Fail(UpgradeOutcome outcome, string detail) =>
        new(Succeeded: false, FailureOutcome: outcome, Detail: detail);
}
