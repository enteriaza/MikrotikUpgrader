// -----------------------------------------------------------------------------
// File: PostUpgradePackageVerifierTests.cs
//
// Description:
// Regression coverage for the live FullUpgrade false failure on 198.18.240.11:
// post-upgrade inventory reported routeros/7.23.8 while the verifier expected
// the pre-upgrade v6 name routeros-mipsbe.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;
/// <summary>
/// Regression coverage for the live FullUpgrade false failure on 198.18.240.11:
/// post-upgrade inventory reported <c>routeros/7.23.8</c> while the verifier expected
/// the pre-upgrade v6 name <c>routeros-mipsbe</c>.
/// </summary>
public sealed class PostUpgradePackageVerifierTests
{
    /// <summary>RouterOS 7 target version used as the verified-upgrade expectation.</summary>
    private static readonly RouterOsVersion V7 = RouterOsVersion.Parse("7.23.8");
    /// <summary>RouterOS 6 source version used as the pre-upgrade baseline.</summary>
    private static readonly RouterOsVersion V6 = RouterOsVersion.Parse("6.49.23");

    /// <summary>Package names that ship inside the RouterOS 6 main bundle.</summary>
    private static readonly string[] V6Bundled =
    [
        "system", "ipv6", "wireless", "hotspot", "mpls",
        "routing", "ppp", "dhcp", "security", "advanced-tools",
    ];

    /// <summary>
    /// A RouterOS 7.23.8 mipsbe inventory holding a single independent <c>routeros</c> 7.23.8 package, checked
    /// against a requirement that maps <c>routeros-mipsbe</c> to <c>routeros</c>, must verify successfully.
    /// </summary>
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

    /// <summary>
    /// If the 7.23.8 router still lists <c>routeros-mipsbe</c> instead of <c>routeros</c>, verification fails with
    /// <c>UpgradePackageMismatch</c> and a detail that names <c>routeros</c>.
    /// </summary>
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

    /// <summary>
    /// A <c>routeros</c> package at 7.23.7 when 7.23.8 is required fails with <c>UpgradePackageMismatch</c>.
    /// </summary>
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

    /// <summary>
    /// An inventory containing only <c>user-manager</c> 7.23.8, with no main <c>routeros</c> package, fails with
    /// <c>UpgradePackageMismatch</c>.
    /// </summary>
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

    /// <summary>
    /// An inventory with both <c>routeros</c> and <c>routeros-mipsbe</c> at 7.23.8 fails with
    /// <c>UpgradePackageMismatch</c>, and the detail says 'multiple'.
    /// </summary>
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

    /// <summary>
    /// An empty installed-package list does not verify; the failure detail contains 'empty'.
    /// </summary>
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

    /// <summary>
    /// A correct <c>routeros</c> 7.23.8 inventory still fails when one package row could not be parsed
    /// (<c>unparsablePackageRows: 1</c>), and the detail says 'unparseable'.
    /// </summary>
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

    /// <summary>
    /// A lone <c>routeros</c> 7.23.8 package verifies on RouterOS 7, with no v6 bundled components expected.
    /// Summarising the same inventory reports a <c>BundledCount</c> of 0.
    /// </summary>
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

    /// <summary>
    /// A 6.49.23 inventory with <c>routeros-mipsbe</c> plus ten bundled components (system, ipv6, wireless,
    /// hotspot, mpls, routing, ppp, dhcp, security, advanced-tools) verifies successfully. The summary reports
    /// <c>BundledCount</c> 10, and every item in <c>BundledComponents</c> is flagged <c>IsBundledComponent</c>.
    /// </summary>
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

    /// <summary>
    /// A 6.49.23 router that lists <c>routeros</c> rather than <c>routeros-mipsbe</c> fails with
    /// <c>UpgradePackageMismatch</c>.
    /// </summary>
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

    /// <summary>
    /// <c>ExpectedInstalledPackageName</c> returns <c>routeros-mipsbe</c> for <c>routeros</c> on 6.49.23 mipsbe,
    /// <c>routeros-arm</c> on 6.49.23 arm, and plain <c>routeros</c> on 7.x. Other packages such as
    /// <c>user-manager</c> keep their own name.
    /// </summary>
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

    /// <summary>Creates an enabled, independently installed package.</summary>
    /// <param name="name">Package name.</param>
    /// <param name="version">Installed version text.</param>
    private static InstalledPackageInfo Indep(string name, string version) =>
        new(name, version, Disabled: false, Bundle: null);

    /// <summary>Creates an enabled package that is part of a bundle package.</summary>
    /// <param name="name">Package name.</param>
    /// <param name="version">Installed version text.</param>
    /// <param name="bundle">Name of the bundle package that contains it.</param>
    private static InstalledPackageInfo Bundled(string name, string version, string bundle) =>
        new(name, version, Disabled: false, Bundle: bundle);

    /// <summary>Creates a catalogue entry for a firmware file with placeholder size and checksum.</summary>
    /// <param name="packageName">Package name.</param>
    /// <param name="fileName">Firmware file name, also used as its path.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="version">Package version.</param>
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

    /// <summary>Creates a resolved upgrade package pairing a source package with its expected target.</summary>
    /// <param name="sourceInstalled">Installed package name on the router before upgrade.</param>
    /// <param name="expectedInstalled">Package name expected after upgrade.</param>
    /// <param name="catalog">Catalogue entry that supplies the target file.</param>
    private static ResolvedUpgradePackage Required(
        string sourceInstalled,
        string expectedInstalled,
        FirmwarePackageInfo catalog) =>
        new(sourceInstalled, expectedInstalled, catalog, RelativeUrlPath: catalog.FileName);
}
