using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests;

public sealed class UpgradePackageResolverTests
{
    [Fact]
    public void Resolve_MainRouterOsOnly()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Pkg("routeros", "6.49.10")],
            "routeros-mipsbe-6.49.23.npk",
            "wireless-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded);
        Assert.Single(result.Packages);
        Assert.Equal("routeros-mipsbe-6.49.23.npk", result.Packages[0].CatalogPackage.FileName);
    }

    [Fact]
    public void Resolve_RouterOsPlusTr069()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Pkg("routeros", "6.49.10"), Pkg("tr069-client", "6.49.10")],
            "routeros-mipsbe-6.49.23.npk",
            "tr069-client-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Packages.Count);
    }

    [Fact]
    public void Resolve_RouterOsPlusUserManager()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Pkg("routeros", "6.49.10"), Pkg("user-manager", "6.49.10")],
            "routeros-mipsbe-6.49.23.npk",
            "user-manager-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded);
        Assert.Contains(result.Packages, static p => p.InstalledPackageName == "user-manager");
    }

    [Fact]
    public void Resolve_MissingOptionalPackage_Fails()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Pkg("routeros", "6.49.10"), Pkg("tr069-client", "6.49.10")],
            "routeros-mipsbe-6.49.23.npk");

        Assert.False(result.Succeeded);
        Assert.Contains("tr069-client", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_WrongArchitecture_Fails()
    {
        UpgradePackageResolution result = Resolve(
            "arm",
            [Pkg("routeros", "6.49.10")],
            "routeros-mipsbe-6.49.23.npk");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Resolve_DoesNotSelectUninstalledOptionalPackages()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Pkg("routeros", "6.49.10")],
            "routeros-mipsbe-6.49.23.npk",
            "wireless-6.49.23-mipsbe.npk",
            "tr069-client-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "wireless");
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "tr069-client");
    }

    [Fact]
    public void Resolve_RouterOsAndSystemTogether_SelectsBoth()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Pkg("routeros", "6.49.10"), Pkg("system", "6.49.10")],
            "routeros-mipsbe-6.49.23.npk",
            "system-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(2, result.Packages.Count);
    }

    [Fact]
    public void Resolve_RouterOsMipsbeAlias_MapsToMainRouterOsPackage()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Pkg("routeros-mipsbe", "6.49.19"), Pkg("system", "6.49.19"), Pkg("wireless", "6.49.19")],
            "routeros-mipsbe-6.49.23.npk",
            "system-6.49.23-mipsbe.npk",
            "wireless-6.49.23-mipsbe.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains(
            result.Packages,
            static p => p.InstalledPackageName == "routeros-mipsbe"
                && p.CatalogPackage.FileName == "routeros-mipsbe-6.49.23.npk");
        Assert.Contains(result.Packages, static p => p.InstalledPackageName == "system");
        Assert.Contains(result.Packages, static p => p.InstalledPackageName == "wireless");
    }

    private static InstalledPackageInfo Pkg(string name, string version) => new(name, version, Disabled: false);

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
