using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests;

/// <summary>
/// Regression coverage for the live FullUpgrade false failure on 198.18.240.11:
/// post-upgrade inventory reported <c>routeros/7.23.8</c> while the verifier expected
/// the pre-upgrade v6 name <c>routeros-mipsbe</c>.
/// </summary>
public sealed class PostUpgradePackageVerifierTests
{
    private static readonly RouterOsVersion V7 = RouterOsVersion.Parse("7.23.8");
    private static readonly RouterOsVersion V6 = RouterOsVersion.Parse("6.49.23");

    private static readonly string[] V6Bundled =
    [
        "system", "ipv6", "wireless", "hotspot", "mpls",
        "routing", "ppp", "dhcp", "security", "advanced-tools",
    ];

    [Fact]
    public void V7_LiveObservedInventory_Passes()
    {
        FirmwarePackageInfo catalog = Catalog("routeros", "routeros-7.23.8-mipsbe.npk", "mipsbe", V7);
        ResolvedUpgradePackage required = Required(
            sourceInstalled: "routeros-mipsbe",
            expectedInstalled: "routeros",
            catalog);

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V7,
            "mipsbe",
            [Indep("routeros", "7.23.8")],
            [required]);

        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void V7_WrongMainPackageName_Fails()
    {
        ResolvedUpgradePackage required = Required(
            "routeros-mipsbe",
            "routeros",
            Catalog("routeros", "routeros-7.23.8-mipsbe.npk", "mipsbe", V7));

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V7,
            "mipsbe",
            [Indep("routeros-mipsbe", "7.23.8")],
            [required]);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeOutcome.UpgradePackageMismatch, result.FailureOutcome);
        Assert.Contains("routeros", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V7_WrongMainPackageVersion_Fails()
    {
        ResolvedUpgradePackage required = Required(
            "routeros-mipsbe",
            "routeros",
            Catalog("routeros", "routeros-7.23.8-mipsbe.npk", "mipsbe", V7));

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V7,
            "mipsbe",
            [Indep("routeros", "7.23.7")],
            [required]);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeOutcome.UpgradePackageMismatch, result.FailureOutcome);
    }

    [Fact]
    public void V7_MissingMainPackage_Fails()
    {
        ResolvedUpgradePackage required = Required(
            "routeros-mipsbe",
            "routeros",
            Catalog("routeros", "routeros-7.23.8-mipsbe.npk", "mipsbe", V7));

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V7,
            "mipsbe",
            [Indep("user-manager", "7.23.8")],
            [required]);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeOutcome.UpgradePackageMismatch, result.FailureOutcome);
    }

    [Fact]
    public void V7_DuplicateMainPackages_Fails()
    {
        ResolvedUpgradePackage required = Required(
            "routeros-mipsbe",
            "routeros",
            Catalog("routeros", "routeros-7.23.8-mipsbe.npk", "mipsbe", V7));

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V7,
            "mipsbe",
            [Indep("routeros", "7.23.8"), Indep("routeros-mipsbe", "7.23.8")],
            [required]);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeOutcome.UpgradePackageMismatch, result.FailureOutcome);
        Assert.Contains("multiple", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V7_EmptyInventory_Fails()
    {
        ResolvedUpgradePackage required = Required(
            "routeros-mipsbe",
            "routeros",
            Catalog("routeros", "routeros-7.23.8-mipsbe.npk", "mipsbe", V7));

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V7,
            "mipsbe",
            [],
            [required]);

        Assert.False(result.Succeeded);
        Assert.Contains("empty", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V7_UnparsableRows_Fails()
    {
        ResolvedUpgradePackage required = Required(
            "routeros-mipsbe",
            "routeros",
            Catalog("routeros", "routeros-7.23.8-mipsbe.npk", "mipsbe", V7));

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V7,
            "mipsbe",
            [Indep("routeros", "7.23.8")],
            [required],
            unparsablePackageRows: 1);

        Assert.False(result.Succeeded);
        Assert.Contains("unparseable", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V7_DoesNotRequireV6BundledComponents()
    {
        ResolvedUpgradePackage required = Required(
            "routeros-mipsbe",
            "routeros",
            Catalog("routeros", "routeros-7.23.8-mipsbe.npk", "mipsbe", V7));

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V7,
            "mipsbe",
            [Indep("routeros", "7.23.8")],
            [required]);

        Assert.True(result.Succeeded, result.Detail);
        PackageInventorySummary summary = PackageInventoryReporting.Summarize([Indep("routeros", "7.23.8")]);
        Assert.Equal(0, summary.BundledCount);
    }

    [Fact]
    public void V6_LabInventory_PassesWithBundledComponents()
    {
        List<InstalledPackageInfo> inventory =
        [
            Indep("routeros-mipsbe", "6.49.23"),
            .. V6Bundled.Select(n => Bundled(n, "6.49.23", "routeros-mipsbe")),
        ];

        ResolvedUpgradePackage required = Required(
            "routeros-mipsbe",
            "routeros-mipsbe",
            Catalog("routeros", "routeros-mipsbe-6.49.23.npk", "mipsbe", V6));

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V6,
            "mipsbe",
            inventory,
            [required]);

        Assert.True(result.Succeeded, result.Detail);
        PackageInventorySummary summary = PackageInventoryReporting.Summarize(inventory);
        Assert.Equal(10, summary.BundledCount);
        Assert.All(summary.BundledComponents, static p => Assert.True(p.IsBundledComponent));
    }

    [Fact]
    public void V6_DoesNotAcceptV7PackageName()
    {
        ResolvedUpgradePackage required = Required(
            "routeros-mipsbe",
            "routeros-mipsbe",
            Catalog("routeros", "routeros-mipsbe-6.49.23.npk", "mipsbe", V6));

        PostUpgradePackageVerification result = PostUpgradePackageVerifier.Verify(
            V6,
            "mipsbe",
            [Indep("routeros", "6.49.23")],
            [required]);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeOutcome.UpgradePackageMismatch, result.FailureOutcome);
    }

    [Fact]
    public void Naming_ExpectedMainPackage_IsVersionAware()
    {
        Assert.Equal(
            "routeros-mipsbe",
            RouterOsInstalledPackageNaming.ExpectedInstalledPackageName("routeros", V6, "mipsbe"));
        Assert.Equal(
            "routeros",
            RouterOsInstalledPackageNaming.ExpectedInstalledPackageName("routeros", V7, "mipsbe"));
        Assert.Equal(
            "routeros-arm",
            RouterOsInstalledPackageNaming.ExpectedInstalledPackageName("routeros", V6, "arm"));
        Assert.Equal(
            "user-manager",
            RouterOsInstalledPackageNaming.ExpectedInstalledPackageName("user-manager", V7, "mipsbe"));
    }

    private static InstalledPackageInfo Indep(string name, string version) =>
        new(name, version, Disabled: false, Bundle: null);

    private static InstalledPackageInfo Bundled(string name, string version, string bundle) =>
        new(name, version, Disabled: false, Bundle: bundle);

    private static FirmwarePackageInfo Catalog(
        string packageName,
        string fileName,
        string architecture,
        RouterOsVersion version) =>
        new(
            packageName,
            fileName,
            FullPath: fileName,
            Architecture: architecture,
            Version: version,
            SizeBytes: 100,
            Sha256Hex: "00");

    private static ResolvedUpgradePackage Required(
        string sourceInstalled,
        string expectedInstalled,
        FirmwarePackageInfo catalog) =>
        new(sourceInstalled, expectedInstalled, catalog, RelativeUrlPath: catalog.FileName);
}
