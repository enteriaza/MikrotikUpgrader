// -----------------------------------------------------------------------------
// File: UpgradePackageResolverTests.cs
//
// Description:
// Unit tests for UpgradePackageResolver catalogue selection and failure kinds.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;
/// <summary>
/// Unit tests for UpgradePackageResolver selection and failure kinds.
/// </summary>
public sealed class UpgradePackageResolverTests
{
    /// <summary>
    /// Exact shape from 198.18.0.83 raw /system/package/print (bundle field present on children).
    /// </summary>
    private static InstalledPackageInfo[] LiveCombinedInventory() =>
    [
        Indep("routeros-mipsbe", "6.49.23"),
        Bundled("system", "6.49.23", "routeros-mipsbe"),
        Bundled("ipv6", "6.49.23", "routeros-mipsbe", disabled: true),
        Bundled("wireless", "6.49.23", "routeros-mipsbe"),
        Bundled("hotspot", "6.49.23", "routeros-mipsbe"),
        Bundled("mpls", "6.49.23", "routeros-mipsbe"),
        Bundled("routing", "6.49.23", "routeros-mipsbe"),
        Bundled("ppp", "6.49.23", "routeros-mipsbe"),
        Bundled("dhcp", "6.49.23", "routeros-mipsbe"),
        Bundled("security", "6.49.23", "routeros-mipsbe"),
        Bundled("advanced-tools", "6.49.23", "routeros-mipsbe"),
    ];

    /// <summary>
    /// Given the live combined 6.49.23 inventory (<c>routeros-mipsbe</c> plus bundled components) and catalogue
    /// files for the main package, <c>system</c>, <c>wireless</c> and <c>ipv6</c>, resolution succeeds with exactly
    /// one package. It is <c>routeros-mipsbe</c> backed by <c>routeros-mipsbe-6.49.23.npk</c>; none of the bundled
    /// components is selected.
    /// </summary>
    [Fact]
    public void Resolve_LiveCombinedInventory_SelectsOnlyMainRouterOsPackage()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            LiveCombinedInventory(),
            "routeros-mipsbe-6.49.23.npk",
            "system-6.49.23-mipsbe.npk",
            "wireless-6.49.23-mipsbe.npk",
            "ipv6-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Single(result.Packages);
        Assert.Equal("routeros-mipsbe", result.Packages[0].InstalledPackageName);
        Assert.Equal("routeros-mipsbe-6.49.23.npk", result.Packages[0].CatalogPackage.FileName);
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "system");
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "wireless");
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "ipv6");
    }

    /// <summary>
    /// Even when the catalogue offers <c>ipv6-6.49.23-mipsbe.npk</c> and ipv6 is a bundled component in the live
    /// inventory, resolution succeeds without selecting an <c>ipv6</c> package.
    /// </summary>
    [Fact]
    public void Resolve_DisabledBundledIpv6_DoesNotTriggerSeparateDownload()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            LiveCombinedInventory(),
            "routeros-mipsbe-6.49.23.npk",
            "ipv6-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "ipv6");
    }

    /// <summary>
    /// A router with only <c>routeros-mipsbe</c> 6.49.23 resolves to a single package,
    /// <c>routeros-mipsbe-6.49.23.npk</c>, even though a <c>wireless</c> file is in the catalogue.
    /// </summary>
    [Fact]
    public void Resolve_MainCombinedPackageOnly()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.23")],
            "routeros-mipsbe-6.49.23.npk",
            "wireless-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Single(result.Packages);
        Assert.Equal("routeros-mipsbe-6.49.23.npk", result.Packages[0].CatalogPackage.FileName);
    }

    /// <summary>
    /// The live inventory plus an independent <c>user-manager</c> 6.49.23 resolves to two packages,
    /// <c>routeros-mipsbe-6.49.23.npk</c> and <c>user-manager-6.49.23-mipsbe.npk</c>. The bundled <c>system</c>
    /// component is not selected despite being in the catalogue.
    /// </summary>
    [Fact]
    public void Resolve_MainPlusIndependentUserManager()
    {
        InstalledPackageInfo[] inventory =
        [
            .. LiveCombinedInventory(),
            Indep("user-manager", "6.49.23"),
        ];

        UpgradePackageResolution result = Resolve(
            "mipsbe",
            inventory,
            "routeros-mipsbe-6.49.23.npk",
            "user-manager-6.49.23-mipsbe.npk",
            "system-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(2, result.Packages.Count);
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "routeros-mipsbe-6.49.23.npk");
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "user-manager-6.49.23-mipsbe.npk");
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "system");
    }

    /// <summary>
    /// The live inventory plus an independent <c>tr069-client</c> resolves to two packages, one of which is the
    /// installed <c>tr069-client</c>.
    /// </summary>
    [Fact]
    public void Resolve_MainPlusIndependentTr069()
    {
        InstalledPackageInfo[] inventory =
        [
            .. LiveCombinedInventory(),
            Indep("tr069-client", "6.49.23"),
        ];

        UpgradePackageResolution result = Resolve(
            "mipsbe",
            inventory,
            "routeros-mipsbe-6.49.23.npk",
            "tr069-client-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(2, result.Packages.Count);
        Assert.Contains(result.Packages, static p => p.InstalledPackageName == "tr069-client");
    }

    /// <summary>
    /// The live inventory plus independent <c>user-manager</c> and <c>tr069-client</c> packages resolves to three
    /// packages (main plus both optionals).
    /// </summary>
    [Fact]
    public void Resolve_MainPlusMultipleIndependentOptionals()
    {
        InstalledPackageInfo[] inventory =
        [
            .. LiveCombinedInventory(),
            Indep("user-manager", "6.49.23"),
            Indep("tr069-client", "6.49.23"),
        ];

        UpgradePackageResolution result = Resolve(
            "mipsbe",
            inventory,
            "routeros-mipsbe-6.49.23.npk",
            "user-manager-6.49.23-mipsbe.npk",
            "tr069-client-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(3, result.Packages.Count);
    }

    /// <summary>
    /// If <c>user-manager</c> is installed but the catalogue holds only the main package, resolution fails and the
    /// failure reason names <c>user-manager</c>.
    /// </summary>
    [Fact]
    public void Resolve_MissingIndependentOptional_Fails()
    {
        InstalledPackageInfo[] inventory =
        [
            .. LiveCombinedInventory(),
            Indep("user-manager", "6.49.23"),
        ];

        UpgradePackageResolution result = Resolve(
            "mipsbe",
            inventory,
            "routeros-mipsbe-6.49.23.npk");

        Assert.False(result.Succeeded);
        Assert.Contains("user-manager", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two identical <c>user-manager-6.49.23-mipsbe.npk</c> files in separate subdirectories, alongside the main
    /// package, make resolution fail with a reason containing 'Ambiguous'.
    /// </summary>
    [Fact]
    public void Resolve_AmbiguousIndependentPackage_Fails()
    {
        string root = Directory.CreateTempSubdirectory("fw-amb-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        File.WriteAllBytes(Path.Combine(root, "a", "user-manager-6.49.23-mipsbe.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "b", "user-manager-6.49.23-mipsbe.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1]);

        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(root),
            Options.Create(new FirmwareFilesOptions { Path = root }));

        UpgradePackageResolution result = resolver.Resolve(
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.23"), Indep("user-manager", "6.49.23")],
            RouterOsVersion.Parse("6.49.23"));

        Assert.False(result.Succeeded);
        Assert.Contains("Ambiguous", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>routeros-mipsbe</c> is recognised as the main RouterOS package and normalises to <c>routeros</c> for
    /// mipsbe. A <c>system</c> package whose bundle is <c>routeros-mipsbe</c> reports <c>IsBundledComponent</c>,
    /// and <c>routeros-mipsbe</c> itself does not.
    /// </summary>
    [Fact]
    public void Normalize_RouterOsMipsbe_IsMainNotOptional()
    {
        Assert.True(UpgradePackageResolver.IsMainRouterOsPackage("routeros-mipsbe"));
        Assert.Equal("routeros", UpgradePackageResolver.NormalizeInstalledPackageName("routeros-mipsbe", "mipsbe"));
        Assert.True(new InstalledPackageInfo("system", "6.49.23", false, "routeros-mipsbe").IsBundledComponent);
        Assert.False(new InstalledPackageInfo("routeros-mipsbe", "6.49.23", false).IsBundledComponent);
    }

    /// <summary>
    /// Moving a 6.49.19 router to the 6.49.23 package keeps the name <c>routeros-mipsbe</c> for both
    /// <c>InstalledPackageName</c> and <c>ExpectedInstalledPackageName</c>.
    /// </summary>
    [Fact]
    public void Resolve_V6Target_ExpectedInstalledRemainsRouterOsMipsbe()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.19")],
            "routeros-mipsbe-6.49.23.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("routeros-mipsbe", result.Packages[0].InstalledPackageName);
        Assert.Equal("routeros-mipsbe", result.Packages[0].ExpectedInstalledPackageName);
    }

    /// <summary>
    /// Resolving the RouterOS 7.23.8 target for a v6 router that has <c>routeros-mipsbe</c> installed selects
    /// <c>routeros-7.23.8-mipsbe.npk</c>. <c>InstalledPackageName</c> stays <c>routeros-mipsbe</c> while
    /// <c>ExpectedInstalledPackageName</c> becomes <c>routeros</c>.
    /// </summary>
    [Fact]
    public void Resolve_V7TargetFromV6Inventory_ExpectedInstalledIsRouterOs()
    {
        string root = Directory.CreateTempSubdirectory("fw-v7-resolve-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [1, 2, 3]);
        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(root),
            Options.Create(new FirmwareFilesOptions { Path = root }));

        UpgradePackageResolution result = resolver.Resolve(
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.23")],
            RouterOsVersion.Parse("7.23.8"));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("routeros-mipsbe", result.Packages[0].InstalledPackageName);
        Assert.Equal("routeros", result.Packages[0].ExpectedInstalledPackageName);
        Assert.Equal("routeros-7.23.8-mipsbe.npk", result.Packages[0].CatalogPackage.FileName);
    }

    /// <summary>
    /// Creates an enabled installed package entry that is not part of any bundle.
    /// </summary>
    private static InstalledPackageInfo Indep(string name, string version) =>
        new(name, version, Disabled: false, Bundle: null);

    /// <summary>
    /// Creates an installed package entry that belongs to the named bundle, optionally disabled.
    /// </summary>
    private static InstalledPackageInfo Bundled(string name, string version, string bundle, bool disabled = false) =>
        new(name, version, disabled, bundle);

    /// <summary>Resolves an upgrade over a temp catalogue containing the named placeholder files.</summary>
    /// <param name="architecture">Router architecture.</param>
    /// <param name="installed">Packages currently installed on the router.</param>
    /// <param name="catalogFiles">File names created in the temp firmware catalogue.</param>
    /// <returns>The resolver outcome for the supplied inventory and catalogue.</returns>
    private static UpgradePackageResolution Resolve(
        string architecture,
        InstalledPackageInfo[] installed,
        params string[] catalogFiles)
    {
        string root = Directory.CreateTempSubdirectory("fw-resolve-").FullName;
        foreach (string file in catalogFiles)
        {
            File.WriteAllBytes(Path.Combine(root, file), [1, 2, 3]);
        }

        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(root),
            Options.Create(new FirmwareFilesOptions { Path = root }));
        return resolver.Resolve(architecture, installed, RouterOsVersion.Parse("6.49.23"));
    }
}
