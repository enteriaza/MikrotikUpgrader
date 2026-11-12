// -----------------------------------------------------------------------------
// File: RouterOsInstalledPackageNaming.cs
//
// Description:
// Maps catalogue package identities to the installed package names RouterOS
// reports after an upgrade. Main package naming differs by major version: v6
// routeros-{arch}, v7+ routeros.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Maps catalogue package identities to the installed package names RouterOS reports
/// after an upgrade. Main package naming differs by major version:
/// v6 <c>routeros-{arch}</c>, v7+ <c>routeros</c>.
/// </summary>
internal static class RouterOsInstalledPackageNaming
{
    /// <summary>
    /// Expected independent installed package name after installing
    /// <paramref name="catalogPackageName"/> for <paramref name="targetVersion"/>.
    /// </summary>
    public static string ExpectedInstalledPackageName(
        string catalogPackageName,
        RouterOsVersion targetVersion,
        string deviceArchitecture,
        string? catalogArchitecture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPackageName);

        string catalog = catalogPackageName.Trim().ToLowerInvariant();
        if (catalog.Equals("routeros", StringComparison.Ordinal))
        {
            if (targetVersion.IsV6)
            {
                string archSource = !string.IsNullOrWhiteSpace(catalogArchitecture)
                    ? catalogArchitecture
                    : deviceArchitecture;
                string arch = FirmwarePackageFileNameParser.NormalizeArchitecture(archSource);
                return $"routeros-{arch}";
            }

            return "routeros";
        }

        return catalog;
    }

    /// <summary>
    /// Whether <paramref name="installedName"/> is a main combined RouterOS package
    /// (<c>routeros</c> or <c>routeros-{arch}</c>).
    /// </summary>
    public static bool IsMainRouterOsPackage(string installedName)
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
}
