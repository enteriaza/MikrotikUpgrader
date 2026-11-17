// -----------------------------------------------------------------------------
// File: UpgradePackageResolverTests.cs
//
// Description:
// Unit tests for UpgradePackageResolver catalogue selection and failure kinds.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Tests.Infrastructure.Helpers;
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
    /// If <c>user-manager</c> is installed but the catalogue holds only the main package, resolution still
    /// succeeds: the target release does not distribute that optional for this stage.
    /// </summary>
    [Fact]
    public void Resolve_MissingIndependentOptional_SucceedsWithDiagnostic()
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

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Single(result.Packages);
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "user-manager"
                 && d.Disposition == PackageResolutionDisposition.NotDistributedInTargetRelease);
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
    /// Individual (split) inventory like Koperfontein/Thirsty Falls: independent <c>system</c> plus peers,
    /// no combined <c>routeros</c>, zero bundled components.
    /// </summary>
    private static InstalledPackageInfo[] IndividualSystemInventory(string version = "6.47.9") =>
    [
        Indep("system", version),
        Indep("routing", version),
        Indep("wireless", version),
        Indep("security", version),
        Indep("dhcp", version),
        Indep("ppp", version),
        Indep("ipv6", version),
        Indep("hotspot", version),
        Indep("mpls", version),
        Indep("advanced-tools", version),
        Indep("user-manager", version),
        Indep("tr069-client", version),
    ];

    /// <summary>
    /// Scenario: individual <c>system</c> installation with no combined routeros; catalogue has matching
    /// per-package .npk files for 6.49.23/arm.
    /// Expected: layout Individual; every independent package is selected; no PackageResolutionFailed for
    /// missing combined routeros.
    /// </summary>
    [Fact]
    public void Resolve_IndividualSystemLayout_SelectsAllIndependentPackages()
    {
        UpgradePackageResolution result = Resolve(
            "arm",
            IndividualSystemInventory(),
            "system-6.49.23-arm.npk",
            "routing-6.49.23-arm.npk",
            "wireless-6.49.23-arm.npk",
            "security-6.49.23-arm.npk",
            "dhcp-6.49.23-arm.npk",
            "ppp-6.49.23-arm.npk",
            "ipv6-6.49.23-arm.npk",
            "hotspot-6.49.23-arm.npk",
            "mpls-6.49.23-arm.npk",
            "advanced-tools-6.49.23-arm.npk",
            "user-manager-6.49.23-arm.npk",
            "tr069-client-6.49.23-arm.npk",
            "routeros-arm-6.49.23.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(FirmwarePackageLayout.Individual, result.Layout);
        Assert.Equal(12, result.Packages.Count);
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "system-6.49.23-arm.npk");
        Assert.DoesNotContain(result.Packages, static p => p.CatalogPackage.PackageName == "routeros");
        Assert.All(result.Packages, static p => Assert.Equal("arm", p.CatalogPackage.Architecture));
    }

    /// <summary>
    /// Scenario: individual layout stays on system for v6; some source independents have no catalogue .npk.
    /// Expected: succeed with only target-distributed packages selected; discontinued source packages are diagnostics.
    /// </summary>
    [Fact]
    public void Resolve_IndividualSystemLayout_SourcePackageAbsentFromTarget_SucceedsWithDiagnostic()
    {
        UpgradePackageResolution result = Resolve(
            "arm",
            IndividualSystemInventory(),
            "system-6.49.23-arm.npk",
            "routing-6.49.23-arm.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(FirmwarePackageLayout.Individual, result.Layout);
        Assert.Equal(2, result.Packages.Count);
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "wireless"
                 && d.Disposition == PackageResolutionDisposition.NotDistributedInTargetRelease);
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "wireless");
    }

    /// <summary>
    /// Scenario: individual system inventory upgrading to RouterOS 7 where <c>system</c> is not distributed;
    /// catalogue has <c>routeros</c> plus remaining extras.
    /// Expected: core transitions to routeros; packages without a 7.x catalogue entry are absorbed; extras selected.
    /// </summary>
    [Fact]
    public void Resolve_IndividualSystem_ToV7_TransitionsCoreToRouterOs_AbsorbsMissing()
    {
        string root = Directory.CreateTempSubdirectory("fw-indiv-v7-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-arm.npk"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "wireless-7.23.8-arm.npk"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "user-manager-7.23.8-arm.npk"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "tr069-client-7.23.8-arm.npk"), [1, 2, 3]);

        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(root),
            Options.Create(new FirmwareFilesOptions { Path = root }));

        UpgradePackageResolution result = resolver.Resolve(
            "arm",
            IndividualSystemInventory("6.49.23"),
            RouterOsVersion.Parse("7.23.8"));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(FirmwarePackageLayout.Combined, result.Layout);
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "routeros-7.23.8-arm.npk");
        Assert.Contains(result.Packages, static p => p.InstalledPackageName == "system");
        Assert.Contains(result.Packages, static p => p.ExpectedInstalledPackageName == "routeros");
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "wireless-7.23.8-arm.npk");
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "routing");
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "system"
                 && d.Disposition == PackageResolutionDisposition.NoSeparateTargetCounterpart);
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "routing"
                 && d.Disposition == PackageResolutionDisposition.NotDistributedInTargetRelease);
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "system"
                 && d.Disposition == PackageResolutionDisposition.CorePackage);
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "wireless"
                 && d.Disposition == PackageResolutionDisposition.Selected);
    }

    /// <summary>
    /// Scenario: individual package for wrong architecture must not be selected.
    /// </summary>
    [Fact]
    public void Resolve_Individual_ArchitectureMismatch_Fails()
    {
        UpgradePackageResolution result = Resolve(
            "arm",
            [Indep("system", "6.47.9"), Indep("routing", "6.47.9")],
            "system-6.49.23-mipsbe.npk",
            "routing-6.49.23-mipsbe.npk");

        Assert.False(result.Succeeded, result.FailureReason);
        Assert.Equal(PackageResolutionFailureKind.MissingPackage, result.FailureKind);
        Assert.Contains("architecture mismatch", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arm", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mipsbe", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: unrelated .npk for another release must not be mixed into the selected set.
    /// </summary>
    [Fact]
    public void Resolve_Individual_IgnoresOtherReleaseFiles()
    {
        string root = Directory.CreateTempSubdirectory("fw-indiv-rel-").FullName;
        File.WriteAllBytes(Path.Combine(root, "system-6.49.23-arm.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "routing-6.49.23-arm.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "system-6.48.6-arm.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "routing-7.23.8-arm.npk"), [1]);

        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(root),
            Options.Create(new FirmwareFilesOptions { Path = root }));

        UpgradePackageResolution result = resolver.Resolve(
            "arm",
            [Indep("system", "6.47.9"), Indep("routing", "6.47.9")],
            RouterOsVersion.Parse("6.49.23"));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(2, result.Packages.Count);
        Assert.All(result.Packages, static p => Assert.Equal(RouterOsVersion.Parse("6.49.23"), p.CatalogPackage.Version));
    }

    /// <summary>
    /// Inventory with neither routeros nor system fails with a clear no-compatible-set message.
    /// </summary>
    [Fact]
    public void Resolve_NeitherRouterOsNorSystem_FailsClearly()
    {
        UpgradePackageResolution result = Resolve(
            "arm",
            [Indep("wireless", "6.47.9")],
            "wireless-6.49.23-arm.npk",
            "routeros-arm-6.49.23.npk");

        Assert.False(result.Succeeded);
        Assert.Equal(PackageResolutionFailureKind.MissingMainPackage, result.FailureKind);
        Assert.Contains("neither", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Expected an unbundled 'routeros'",
            result.FailureReason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Combined layout regression: layout is reported as Combined.
    /// </summary>
    [Fact]
    public void Resolve_CombinedLayout_ReportsCombined()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            LiveCombinedInventory(),
            "routeros-mipsbe-6.49.23.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(FirmwarePackageLayout.Combined, result.Layout);
    }

    /// <summary>
    /// Combined source with an independent optional absent from the target release: resolve the core only.
    /// </summary>
    [Fact]
    public void Resolve_Combined_DiscontinuedIndependent_DoesNotFail()
    {
        UpgradePackageResolution result = Resolve(
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10"), Indep("lora", "6.49.10")],
            "routeros-mipsbe-6.49.23.npk");

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Single(result.Packages);
        Assert.Equal("routeros-mipsbe", result.Packages[0].InstalledPackageName);
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "lora"
                 && d.Disposition == PackageResolutionDisposition.NotDistributedInTargetRelease);
    }

    /// <summary>
    /// Combined → v7: discontinued independents are skipped; core becomes plain routeros.
    /// </summary>
    [Fact]
    public void Resolve_CombinedToV7_DiscontinuedIndependent_DoesNotFail()
    {
        string root = Directory.CreateTempSubdirectory("fw-combined-v7-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "wireless-7.23.8-mipsbe.npk"), [1, 2, 3]);

        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(root),
            Options.Create(new FirmwareFilesOptions { Path = root }));

        UpgradePackageResolution result = resolver.Resolve(
            "mipsbe",
            [
                Indep("routeros-mipsbe", "6.49.23"),
                Indep("wireless", "6.49.23"),
                Indep("routing", "6.49.23"),
            ],
            RouterOsVersion.Parse("7.23.8"));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(FirmwarePackageLayout.Combined, result.Layout);
        Assert.Equal(2, result.Packages.Count);
        Assert.Contains(result.Packages, static p => p.ExpectedInstalledPackageName == "routeros");
        Assert.Contains(result.Packages, static p => p.InstalledPackageName == "wireless");
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "routing"
                 && d.Disposition == PackageResolutionDisposition.NotDistributedInTargetRelease);
    }

    /// <summary>
    /// During system→routeros transition, an extra present only for another architecture (MikroTik ships
    /// some packages for a subset of arches) is absorbed with an explicit other-arch diagnostic.
    /// Outside that transition, the same catalogue shape remains an architecture-mismatch failure.
    /// </summary>
    [Fact]
    public void Resolve_IndividualToV7_ExtraPresentOnlyForOtherArch_IsAbsorbedWithDiagnostic()
    {
        string root = Directory.CreateTempSubdirectory("fw-indiv-v7-arch-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-arm.npk"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "hotspot-7.23.8-smips.npk"), [1, 2, 3]);

        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(root),
            Options.Create(new FirmwareFilesOptions { Path = root }));

        UpgradePackageResolution transition = resolver.Resolve(
            "arm",
            [Indep("system", "6.49.23"), Indep("hotspot", "6.49.23")],
            RouterOsVersion.Parse("7.23.8"));

        Assert.True(transition.Succeeded, transition.FailureReason);
        Assert.Equal(FirmwarePackageLayout.Combined, transition.Layout);
        Assert.DoesNotContain(transition.Packages, static p => p.InstalledPackageName == "hotspot");
        Assert.Contains(
            transition.PackageDiagnostics,
            d => d.PackageIdentity == "hotspot"
                 && d.Disposition == PackageResolutionDisposition.NotDistributedForTargetArchitecture
                 && d.OtherArchitectures.Contains("smips"));

        // Same-family individual target: architecture-exclusive extras are skipped, not fatal.
        UpgradePackageResolution nonTransition = Resolve(
            "arm",
            [Indep("system", "6.47.9"), Indep("hotspot", "6.47.9")],
            "system-6.49.23-arm.npk",
            "hotspot-6.49.23-smips.npk");

        Assert.True(nonTransition.Succeeded, nonTransition.FailureReason);
        Assert.Equal(FirmwarePackageLayout.Individual, nonTransition.Layout);
        Assert.Single(nonTransition.Packages);
        Assert.Contains(
            nonTransition.PackageDiagnostics,
            d => d.PackageIdentity == "hotspot"
                 && d.Disposition == PackageResolutionDisposition.NotDistributedForTargetArchitecture);
    }

    /// <summary>
    /// Independent routeros and system together are a mixed layout and must fail closed.
    /// </summary>
    [Fact]
    public void Resolve_MixedCombinedAndIndividualCores_FailsClosed()
    {
        UpgradePackageResolution result = Resolve(
            "arm",
            [Indep("routeros-arm", "6.47.9"), Indep("system", "6.47.9")],
            "routeros-arm-6.49.23.npk",
            "system-6.49.23-arm.npk");

        Assert.False(result.Succeeded);
        Assert.Equal(PackageResolutionFailureKind.MultipleMainPackages, result.FailureKind);
        Assert.Contains("mixes combined and individual", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Missing both combined and individual cores fails clearly without the legacy combined-only wording.
    /// </summary>
    [Fact]
    public void Resolve_MissingBothCores_FailsClearly()
    {
        UpgradePackageResolution result = Resolve(
            "arm",
            [Indep("wireless", "6.47.9"), Indep("routing", "6.47.9")],
            "wireless-6.49.23-arm.npk",
            "routing-6.49.23-arm.npk",
            "routeros-arm-6.49.23.npk",
            "system-6.49.23-arm.npk");

        Assert.False(result.Succeeded);
        Assert.Equal(PackageResolutionFailureKind.MissingMainPackage, result.FailureKind);
        Assert.Contains("neither", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ambiguous duplicate catalogue filenames for the same package identity fail closed.
    /// </summary>
    [Fact]
    public void Resolve_AmbiguousDuplicateCatalogueFiles_Fails()
    {
        string root = Directory.CreateTempSubdirectory("fw-ambig-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        File.WriteAllBytes(Path.Combine(root, "a", "system-6.49.23-arm.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "b", "system-6.49.23-arm.npk"), [2]);

        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(root),
            Options.Create(new FirmwareFilesOptions { Path = root }));

        UpgradePackageResolution result = resolver.Resolve(
            "arm",
            [Indep("system", "6.47.9")],
            RouterOsVersion.Parse("6.49.23"));

        Assert.False(result.Succeeded);
        Assert.Equal(PackageResolutionFailureKind.AmbiguousPackage, result.FailureKind);
    }

    /// <summary>
    /// Catalogue enumeration order must not change the selected individual package set.
    /// </summary>
    [Fact]
    public void Resolve_Individual_IsDeterministicAcrossRepeatedCalls()
    {
        string root = Directory.CreateTempSubdirectory("fw-det-").FullName;
        File.WriteAllBytes(Path.Combine(root, "wireless-6.49.23-arm.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "system-6.49.23-arm.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "routing-6.49.23-arm.npk"), [1]);

        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(root),
            Options.Create(new FirmwareFilesOptions { Path = root }));
        InstalledPackageInfo[] inventory =
        [
            Indep("routing", "6.47.9"),
            Indep("system", "6.47.9"),
            Indep("wireless", "6.47.9"),
        ];

        UpgradePackageResolution first = resolver.Resolve("arm", inventory, RouterOsVersion.Parse("6.49.23"));
        UpgradePackageResolution second = resolver.Resolve("arm", inventory, RouterOsVersion.Parse("6.49.23"));

        Assert.True(first.Succeeded, first.FailureReason);
        Assert.True(second.Succeeded, second.FailureReason);
        Assert.Equal(
            first.Packages.Select(static p => p.CatalogPackage.FileName).OrderBy(static s => s, StringComparer.Ordinal),
            second.Packages.Select(static p => p.CatalogPackage.FileName).OrderBy(static s => s, StringComparer.Ordinal));
        Assert.Equal(3, first.Packages.Count);
    }

    /// <summary>
    /// Production Koperfontein-shaped inventory against the checked-in FirmwareFiles tree for v6 stepping.
    /// </summary>
    [Fact]
    public void Resolve_KoperfonteinInventory_AgainstRealFirmwareFiles_V6Stepping()
    {
        string firmwareRoot = RepoPaths.GetSourceFirmwareRoot();
        Assert.True(Directory.Exists(firmwareRoot), $"FirmwareFiles missing at {firmwareRoot}");

        UpgradePackageResolver resolver = new(
            new FirmwarePackageCatalog(Options.Create(new FirmwareFilesOptions { Path = firmwareRoot })),
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot }));

        UpgradePackageResolution result = resolver.Resolve(
            "arm",
            KoperfonteinIndividualInventory("6.47.9"),
            RouterOsVersion.Parse("6.49.23"));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(FirmwarePackageLayout.Individual, result.Layout);
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "system-6.49.23-arm.npk");
        Assert.DoesNotContain(result.Packages, static p => p.CatalogPackage.PackageName == "routeros");
        Assert.Equal(19, result.Packages.Count);
        Assert.All(result.Packages, static p =>
        {
            Assert.Equal(RouterOsVersion.Parse("6.49.23"), p.CatalogPackage.Version);
            Assert.Equal("arm", p.CatalogPackage.Architecture);
        });
    }

    /// <summary>
    /// Production Koperfontein-shaped 6.49.23 inventory against real FirmwareFiles for RouterOS 7.
    /// </summary>
    [Fact]
    public void Resolve_KoperfonteinInventory_AgainstRealFirmwareFiles_V7Transition()
    {
        string firmwareRoot = RepoPaths.GetSourceFirmwareRoot();
        Assert.True(Directory.Exists(firmwareRoot), $"FirmwareFiles missing at {firmwareRoot}");

        UpgradePackageResolver resolver = new(
            new FirmwarePackageCatalog(Options.Create(new FirmwareFilesOptions { Path = firmwareRoot })),
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot }));

        // Second production capture had a reduced independent set still anchored on system.
        InstalledPackageInfo[] inventory =
        [
            Indep("gps", "6.49.23"),
            Indep("ntp", "6.49.23"),
            Indep("ppp", "6.49.23"),
            Indep("ups", "6.49.23"),
            Indep("dhcp", "6.49.23"),
            Indep("ipv6", "6.49.23"),
            Indep("lora", "6.49.23"),
            Indep("mpls", "6.49.23"),
            Indep("hotspot", "6.49.23"),
            Indep("security", "6.49.23"),
            Indep("wireless", "6.49.23"),
            Indep("system", "6.49.23"),
            Indep("routing", "6.49.23"),
        ];

        UpgradePackageResolution result = resolver.Resolve(
            "arm",
            inventory,
            RouterOsVersion.Parse("7.23.8"));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(FirmwarePackageLayout.Combined, result.Layout);
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "routeros-7.23.8-arm.npk");
        Assert.Contains(result.Packages, static p => p.ExpectedInstalledPackageName == "routeros");
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "wireless-7.23.8-arm.npk");
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "gps-7.23.8-arm.npk");
        Assert.Contains(result.Packages, static p => p.CatalogPackage.FileName == "ups-7.23.8-arm.npk");
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "routing");
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "dhcp");
        Assert.DoesNotContain(result.Packages, static p => p.InstalledPackageName == "hotspot");
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "hotspot"
                 && d.Disposition == PackageResolutionDisposition.NotDistributedForTargetArchitecture
                 && d.OtherArchitectures.Contains("smips"));
        Assert.Contains(
            result.PackageDiagnostics,
            d => d.PackageIdentity == "routing"
                 && d.Disposition == PackageResolutionDisposition.NotDistributedInTargetRelease);
        Assert.All(result.Packages, static p =>
        {
            Assert.Equal(RouterOsVersion.Parse("7.23.8"), p.CatalogPackage.Version);
            Assert.Equal("arm", p.CatalogPackage.Architecture);
        });
    }

    /// <summary>
    /// Full Koperfontein 19-package inventory from the first production failure for v6 stepping.
    /// </summary>
    private static InstalledPackageInfo[] KoperfonteinIndividualInventory(string version) =>
    [
        Indep("gps", version),
        Indep("ntp", version),
        Indep("ppp", version),
        Indep("ups", version),
        Indep("dhcp", version),
        Indep("ipv6", version),
        Indep("lora", version),
        Indep("mpls", version),
        Indep("hotspot", version),
        Indep("security", version),
        Indep("advanced-tools", version),
        Indep("tr069-client", version),
        Indep("calea", version),
        Indep("user-manager", version),
        Indep("wireless", version),
        Indep("system", version),
        Indep("openflow", version),
        Indep("multicast", version),
        Indep("routing", version),
    ];

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
