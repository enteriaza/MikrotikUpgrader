using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;
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

    [Fact]
    public void Normalize_RouterOsMipsbe_IsMainNotOptional()
    {
        Assert.True(UpgradePackageResolver.IsMainRouterOsPackage("routeros-mipsbe"));
        Assert.Equal("routeros", UpgradePackageResolver.NormalizeInstalledPackageName("routeros-mipsbe", "mipsbe"));
        Assert.True(new InstalledPackageInfo("system", "6.49.23", false, "routeros-mipsbe").IsBundledComponent);
        Assert.False(new InstalledPackageInfo("routeros-mipsbe", "6.49.23", false).IsBundledComponent);
    }

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

    private static InstalledPackageInfo Indep(string name, string version) =>
        new(name, version, Disabled: false, Bundle: null);

    private static InstalledPackageInfo Bundled(string name, string version, string bundle, bool disabled = false) =>
        new(name, version, disabled, bundle);

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
