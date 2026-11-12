// -----------------------------------------------------------------------------
// File: PackageCompatibilityAuditorTests.cs
//
// Description:
// Unit tests for PackageCompatibilityAuditor against synthetic inventories and
// catalogue fixtures.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;
    /// <summary>
    /// Scenario: write catalogue coverage and a synthetic device report into a temp output directory.
    /// Expected: both CSV and JSON artefacts exist for catalogue and device compatibility, containing fixture markers.
    /// Invariant: report writer emits paired CSV/JSON without inventing NotApplicable rows for compatible fixtures.
    /// </summary>
public sealed class PackageCompatibilityAuditorTests
{
    /// <summary>
    /// A mipsbe device whose catalogue has the exact <c>routeros-mipsbe-6.49.23.npk</c> gets a v6 stepping status
    /// of <c>Compatible</c>. That file is the resolved package and nothing is reported missing.
    /// </summary>
    [Fact]
    public void ExactArchitectureAndVersion_IsCompatible()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.MainOnlyMipsbeOlderV6(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.Compatible, v6.Status);
        Assert.Contains("routeros-mipsbe-6.49.23.npk", v6.ResolvedPackageFileNames);
        Assert.Empty(v6.MissingPackageNames);
    }

    /// <summary>
    /// A mipsbe device against a catalogue holding only <c>routeros-arm-6.49.23.npk</c> gets
    /// <c>ArchitectureMismatch</c> at the v6 stage, and <c>routeros</c> is listed in the missing package names.
    /// </summary>
    [Fact]
    public void MissingMainPackage_IsReported()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.MainOnlyMipsbeOlderV6(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-arm-6.49.23.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.ArchitectureMismatch, v6.Status);
        Assert.Contains("routeros", v6.MissingPackageNames);
    }

    /// <summary>
    /// A mipsbe device with installed <c>user-manager</c> and <c>tr069-client</c> against a catalogue containing
    /// only the main package and <c>user-manager</c> gets <c>MissingOptionalPackage</c>, with <c>tr069-client</c>
    /// named as missing. Bundled components such as <c>system</c> and <c>ipv6</c> are not listed among the
    /// independent packages.
    /// </summary>
    [Fact]
    public void MissingInstalledIndependentOptional_IsReported()
    {
        PackageCompatibilityAuditDevice device = SyntheticPackageInventoryFixtures.MainPlusIndependentOptionalsMipsbe();
        PackageCompatibilityStageResult v6 = AssessStage(
            device,
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "user-manager-6.49.23-mipsbe.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.MissingOptionalPackage, v6.Status);
        Assert.Contains("tr069-client", v6.MissingPackageNames);
        Assert.DoesNotContain(v6.IndependentPackageNames, static n => n is "system" or "ipv6");
    }

    /// <summary>
    /// A main-only mipsbe device stays <c>Compatible</c> when the catalogue also offers <c>user-manager</c>,
    /// <c>tr069-client</c> and a RouterOS 7 file it has not installed. Only the main package is resolved.
    /// </summary>
    [Fact]
    public void ExtraCatalogueOptionals_NotRequiredWhenNotInstalled()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.MainOnlyMipsbeOlderV6(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog(
                "routeros-mipsbe-6.49.23.npk",
                "user-manager-6.49.23-mipsbe.npk",
                "tr069-client-6.49.23-mipsbe.npk",
                "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.Compatible, v6.Status);
        Assert.Single(v6.ResolvedPackageFileNames);
    }

    /// <summary>
    /// A device with three bundled components (<c>system</c>, <c>wireless</c>, <c>hotspot</c>) is <c>Compatible</c>
    /// although the catalogue offers separate files for them. The resolved list is just
    /// <c>routeros-mipsbe-6.49.23.npk</c> and <c>BundledComponentCount</c> is 3.
    /// </summary>
    [Fact]
    public void BundledComponents_AreNotResolvedIndependently()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.BundledComponentsMipsbe(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog(
                "routeros-mipsbe-6.49.23.npk",
                "system-6.49.23-mipsbe.npk",
                "wireless-6.49.23-mipsbe.npk",
                "hotspot-6.49.23-mipsbe.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.Compatible, v6.Status);
        Assert.Equal(["routeros-mipsbe-6.49.23.npk"], v6.ResolvedPackageFileNames);
        Assert.Equal(3, v6.BundledComponentCount);
    }

    /// <summary>
    /// The same <c>routeros-mipsbe-6.49.23.npk</c> in the root and in a <c>dup</c> subfolder makes the v6 stage
    /// <c>AmbiguousPackage</c>, with both files listed in <c>AmbiguousPackageCandidates</c>.
    /// </summary>
    [Fact]
    public void DuplicateCandidatePackages_AreAmbiguous()
    {
        string root = Directory.CreateTempSubdirectory("compat-amb-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1]);
            Directory.CreateDirectory(Path.Combine(root, "dup"));
            File.WriteAllBytes(Path.Combine(root, "dup", "routeros-mipsbe-6.49.23.npk"), [1]);

            PackageCompatibilityStageResult v6 = AssessStage(
                SyntheticPackageInventoryFixtures.AmbiguousDuplicateMainCandidates(),
                PackageCompatibilityUpgradeStage.V6Stepping,
                new DirectoryCatalog(root));

            Assert.Equal(CatalogueCompatibilityStatus.AmbiguousPackage, v6.Status);
            Assert.Equal(2, v6.AmbiguousPackageCandidates.Count);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// A device with an unsupported architecture, against mipsbe-only catalogue files, gets
    /// <c>ArchitectureMismatch</c> at the v6 stage.
    /// </summary>
    [Fact]
    public void WrongArchitecture_IsArchitectureMismatch()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.UnsupportedArchitecture(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.ArchitectureMismatch, v6.Status);
    }

    /// <summary>
    /// A mipsbe device whose catalogue has only <c>routeros-mipsbe-6.48.0.npk</c>, not 6.49.23, gets
    /// <c>VersionMismatch</c>.
    /// </summary>
    [Fact]
    public void WrongVersionOnly_IsVersionMismatch()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.MainOnlyMipsbeOlderV6(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-mipsbe-6.48.0.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.VersionMismatch, v6.Status);
    }

    /// <summary>
    /// An x86 device on 7.22.0 with target 7.23.8 is <c>Compatible</c> at the RouterOS 7 stage using
    /// <c>routeros-7.23.8.npk</c>, a file name with no architecture suffix.
    /// </summary>
    [Fact]
    public void X86ArchlessPackage_IsAcceptedForV7()
    {
        PackageCompatibilityStageResult v7 = AssessStage(
            SyntheticPackageInventoryFixtures.X86ArchlessMain() with { RouterOsVersionRaw = "7.22.0" },
            PackageCompatibilityUpgradeStage.RouterOs7,
            CreateCatalog("routeros-x86-6.49.23.npk", "routeros-7.23.8.npk"),
            v7Target: "7.23.8");

        Assert.Equal(CatalogueCompatibilityStatus.Compatible, v7.Status);
        Assert.Contains("routeros-7.23.8.npk", v7.ResolvedPackageFileNames);
    }

    /// <summary>
    /// A device reporting the powerpc architecture alias is <c>Compatible</c> at the v6 stage and resolves
    /// <c>routeros-powerpc-6.49.23.npk</c>.
    /// </summary>
    [Fact]
    public void PowerPcAlias_MatchesPpcPackage()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.PowerPcAlias(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-powerpc-6.49.23.npk", "routeros-7.23.8-ppc.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.Compatible, v6.Status);
        Assert.Contains("routeros-powerpc-6.49.23.npk", v6.ResolvedPackageFileNames);
    }

    /// <summary>
    /// An smips device with installed <c>user-manager</c> and <c>tr069-client</c>, where the catalogue only has
    /// <c>tr069-client</c>, gets <c>MissingOptionalPackage</c> and <c>user-manager</c> is named as missing.
    /// </summary>
    [Fact]
    public void SparseOptionalCoverage_FailsClosed()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.MissingOptionalInCatalogueSmips(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog(
                "routeros-smips-6.49.23.npk",
                "tr069-client-6.49.23-smips.npk",
                "routeros-7.23.8-smips.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.MissingOptionalPackage, v6.Status);
        Assert.Contains("user-manager", v6.MissingPackageNames);
    }

    /// <summary>
    /// Filename looks valid but does not satisfy the installed independent optional.
    /// A catalogue file named <c>wireless-6.49.23-mipsbe.npk</c> does not stand in for the installed independent
    /// optionals. The v6 stage is still <c>MissingOptionalPackage</c>.
    /// </summary>
    [Fact]
    public void UnambiguousFilename_StillRequiresExactInstalledMapping()
    {
        // Filename looks valid but does not satisfy the installed independent optional.
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.MainPlusIndependentOptionalsMipsbe(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog(
                "routeros-mipsbe-6.49.23.npk",
                "wireless-6.49.23-mipsbe.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.MissingOptionalPackage, v6.Status);
    }

    /// <summary>
    /// A device with an empty package inventory gets <c>InvalidInventory</c> at the v6 stage even when a matching
    /// main package exists in the catalogue.
    /// </summary>
    [Fact]
    public void InvalidEmptyInventory_IsInvalid()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.InvalidEmptyInventory(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.InvalidInventory, v6.Status);
    }

    /// <summary>
    /// For each InlineData fixture (ACE, BR, AP, DCL, an empty identity and an unknown identity) the classification
    /// gives the expected <c>DeviceType</c> with <c>ShouldUpgradeToV7</c> true. The RouterOS 7 stage is
    /// <c>Compatible</c> with at least one resolved package file.
    /// </summary>
    [Theory]
    [InlineData("ace-identity", DeviceType.Ace)]
    [InlineData("br-identity", DeviceType.Br)]
    [InlineData("ap-identity", DeviceType.Ap)]
    [InlineData("dcl-identity", DeviceType.Dcl)]
    [InlineData("empty-identity", DeviceType.Unknown)]
    [InlineData("unknown-identity", DeviceType.Unknown)]
    public void IdentityPolicy_AllTypesAssessRouterOs7Compatibility(
        string fixtureName,
        DeviceType expectedType)
    {
        PackageCompatibilityAuditDevice device = SyntheticPackageInventoryFixtures.All()
            .Single(d => d.FixtureName == fixtureName);
        PackageCompatibilityDeviceReport report = AssessDevice(
            device,
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(expectedType, report.Classification.DeviceType);
        Assert.True(report.Classification.ShouldUpgradeToV7);
        PackageCompatibilityStageResult v7 = report.Stages.Single(
            static s => s.Stage == PackageCompatibilityUpgradeStage.RouterOs7);
        Assert.Equal(CatalogueCompatibilityStatus.Compatible, v7.Status);
        Assert.NotEmpty(v7.ResolvedPackageFileNames);
    }

    /// <summary>
    /// An AP identity with no RouterOS 7 target, even though a 7.23.8 file is in the catalogue, gets
    /// <c>TargetUnavailable</c> at the RouterOS 7 stage.
    /// </summary>
    [Fact]
    public void MissingV7Target_IsTargetUnavailable_ForEligibleDevice()
    {
        PackageCompatibilityStageResult v7 = AssessStage(
            SyntheticPackageInventoryFixtures.ApIdentity(),
            PackageCompatibilityUpgradeStage.RouterOs7,
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"),
            v7Target: null);

        Assert.Equal(CatalogueCompatibilityStatus.TargetUnavailable, v7.Status);
    }

    /// <summary>
    /// With 7.23.8 and 7.23.9 files both in the catalogue and a 7.23.8 target, the RouterOS 7 stage is
    /// <c>Compatible</c> with <c>TargetRouterOsVersion</c> 7.23.8. It resolves the 7.23.8 file and never the 7.23.9
    /// one.
    /// </summary>
    [Fact]
    public void DynamicV7Snapshot_IsUsedExactly()
    {
        PackageCompatibilityStageResult v7 = AssessStage(
            SyntheticPackageInventoryFixtures.ApIdentity() with { RouterOsVersionRaw = "7.22.0" },
            PackageCompatibilityUpgradeStage.RouterOs7,
            CreateCatalog("routeros-7.23.8-mipsbe.npk", "routeros-7.23.9-mipsbe.npk"),
            v7Target: "7.23.8");

        Assert.Equal(CatalogueCompatibilityStatus.Compatible, v7.Status);
        Assert.Equal("7.23.8", v7.TargetRouterOsVersion);
        Assert.Contains("routeros-7.23.8-mipsbe.npk", v7.ResolvedPackageFileNames);
        Assert.DoesNotContain(v7.ResolvedPackageFileNames, static f => f.Contains("7.23.9", StringComparison.Ordinal));
    }

    /// <summary>
    /// tr069-client missing for v7.
    /// The v6 stage is <c>Compatible</c> but the RouterOS 7 stage is <c>MissingOptionalPackage</c>, because the
    /// catalogue has v7 files for <c>routeros</c> and <c>user-manager</c> but not <c>tr069-client</c>. That package
    /// is named in the v7 missing list.
    /// </summary>
    [Fact]
    public void SuccessfulV6_DoesNotImplyV7WhenOptionalMissingForV7()
    {
        PackageCompatibilityAuditDevice device = SyntheticPackageInventoryFixtures.MainPlusIndependentOptionalsMipsbe();
        PackageCompatibilityDeviceReport report = AssessDevice(
            device,
            CreateCatalog(
                "routeros-mipsbe-6.49.23.npk",
                "user-manager-6.49.23-mipsbe.npk",
                "tr069-client-6.49.23-mipsbe.npk",
                "routeros-7.23.8-mipsbe.npk",
                "user-manager-7.23.8-mipsbe.npk"));
        // tr069-client missing for v7

        PackageCompatibilityStageResult v6 = report.Stages.Single(
            static s => s.Stage == PackageCompatibilityUpgradeStage.V6Stepping);
        PackageCompatibilityStageResult v7 = report.Stages.Single(
            static s => s.Stage == PackageCompatibilityUpgradeStage.RouterOs7);

        Assert.Equal(CatalogueCompatibilityStatus.Compatible, v6.Status);
        Assert.Equal(CatalogueCompatibilityStatus.MissingOptionalPackage, v7.Status);
        Assert.Contains("tr069-client", v7.MissingPackageNames);
    }

    /// <summary>
    /// A device already on 6.49.23 gets <c>NotApplicable</c> at the v6 stepping stage instead of being checked
    /// against the catalogue.
    /// </summary>
    [Fact]
    public void DeviceAlreadyAtOrNewerThanStepping_MarksV6NotApplicable()
    {
        PackageCompatibilityAuditDevice device = SyntheticPackageInventoryFixtures.MainOnlyMipsbeOlderV6()
            with { RouterOsVersionRaw = "6.49.23" };
        PackageCompatibilityStageResult v6 = AssessStage(
            device,
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.NotApplicable, v6.Status);
    }

    /// <summary>
    /// The coverage report built for the 6.49.23 stepping version has eight rows, each with a main package file
    /// name. The mipsbe row lists exactly one independent optional package and the smips row has status
    /// <c>MainOnly</c>.
    /// </summary>
    [Fact]
    public void CatalogueCoverage_ReportsAllEightArchitectures()
    {
        IFirmwarePackageCatalog catalog = CreateCatalog(
            "routeros-mipsbe-6.49.23.npk",
            "routeros-arm-6.49.23.npk",
            "routeros-arm64-6.49.23.npk",
            "routeros-mmips-6.49.23.npk",
            "routeros-smips-6.49.23.npk",
            "routeros-tile-6.49.23.npk",
            "routeros-powerpc-6.49.23.npk",
            "routeros-x86-6.49.23.npk",
            "user-manager-6.49.23-mipsbe.npk");

        IReadOnlyList<CatalogueCoverageArchitectureRow> rows =
            new CatalogueCoverageReporter(catalog).BuildCoverage(RouterOsUpgradeVersions.V6Stepping);

        Assert.Equal(8, rows.Count);
        Assert.All(rows, static r => Assert.NotNull(r.MainPackageFileName));
        Assert.Contains(rows, static r => r.Architecture == "mipsbe" && r.IndependentOptionalPackageFileNames.Count == 1);
        Assert.Contains(rows, static r => r.Architecture == "smips" && r.CatalogueStatus == "MainOnly");
    }

    /// <summary>
    /// Scenario: write catalogue coverage and a synthetic device report to a temp directory.
    /// Expected: paired CSV/JSON artefacts for catalogue and device compatibility with fixture markers.
    /// Invariant: report writer emits CSV/JSON without inventing NotApplicable rows for compatible fixtures.
    /// </summary>
    [Fact]
    public async Task ReportWriter_EmitsCsvAndJson()
    {
        string output = Directory.CreateTempSubdirectory("compat-report-").FullName;
        try
        {
            PackageCompatibilityDeviceReport report = AssessDevice(
                SyntheticPackageInventoryFixtures.AceIdentity(),
                CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"));
            CatalogueCoverageArchitectureRow coverage = new()
            {
                Architecture = "mipsbe",
                TargetVersion = "6.49.23",
                MainPackageFileName = "routeros-mipsbe-6.49.23.npk",
                CatalogueStatus = "MainOnly",
            };

            await PackageCompatibilityAuditReportWriter.WriteAsync(output, [coverage], [report]);

            Assert.True(File.Exists(Path.Combine(output, "catalogue-coverage.csv")));
            Assert.True(File.Exists(Path.Combine(output, "catalogue-coverage.json")));
            Assert.True(File.Exists(Path.Combine(output, "device-compatibility.csv")));
            Assert.True(File.Exists(Path.Combine(output, "device-compatibility.json")));
            string csv = await File.ReadAllTextAsync(Path.Combine(output, "device-compatibility.csv"));
            Assert.Contains("SyntheticFixture", csv, StringComparison.Ordinal);
            Assert.Contains("Compatible", csv, StringComparison.Ordinal);
            Assert.Contains("ace-identity", csv, StringComparison.Ordinal);
            Assert.DoesNotContain("NotApplicable", csv, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(output, true);
        }
    }

    /// <summary>
    /// Assesses the device and returns the result for the requested upgrade stage.
    /// </summary>
    private static PackageCompatibilityStageResult AssessStage(
        PackageCompatibilityAuditDevice device,
        PackageCompatibilityUpgradeStage stage,
        IFirmwarePackageCatalog catalog,
        string? v7Target = "7.23.8") =>
        AssessDevice(device, catalog, v7Target).Stages.Single(s => s.Stage == stage);

    /// <summary>
    /// Runs a <c>PackageCompatibilityAuditor</c> over the catalogue with the given v7 target and returns the report
    /// for the device.
    /// </summary>
    private static PackageCompatibilityDeviceReport AssessDevice(
        PackageCompatibilityAuditDevice device,
        IFirmwarePackageCatalog catalog,
        string? v7Target = "7.23.8")
    {
        PackageCompatibilityAuditor auditor = new(
            new UpgradePackageResolver(
                catalog,
                Options.Create(new FirmwareFilesOptions { Path = GetRoot(catalog) })),
            catalog,
            new FixedRouterOsV7TargetProvider(v7Target));
        return auditor.AssessDevice(device);
    }

    /// <summary>
    /// Returns the catalogue's root directory when it is a <c>DirectoryCatalog</c>, otherwise a new temp directory.
    /// </summary>
    private static string GetRoot(IFirmwarePackageCatalog catalog) =>
        catalog is DirectoryCatalog dir ? dir.Root : Directory.CreateTempSubdirectory("compat-root-").FullName;

    /// <summary>
    /// Creates a temp directory containing placeholder files with the given names and returns a catalogue over it.
    /// </summary>
    private static IFirmwarePackageCatalog CreateCatalog(params string[] fileNames)
    {
        string root = Directory.CreateTempSubdirectory("compat-cat-").FullName;
        foreach (string name in fileNames)
        {
            File.WriteAllBytes(Path.Combine(root, name), [1, 2, 3, 4]);
        }

        return new DirectoryCatalog(root);
    }

    /// <summary>
    /// Firmware package catalogue fake that enumerates <c>*.npk</c> files under its root directory and parses each
    /// file name into a package entry.
    /// </summary>
    private sealed class DirectoryCatalog : IFirmwarePackageCatalog
    {
        /// <summary>Creates a catalogue over <paramref name="root"/>.</summary>
        /// <param name="root">Directory scanned for <c>*.npk</c> files.</param>
        public DirectoryCatalog(string root) => Root = root;

        /// <summary>Directory scanned for <c>*.npk</c> files.</summary>
        public string Root { get; }

        /// <summary>Gets only the main <c>routeros</c> packages from <see cref="GetPackages"/>.</summary>
        public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
            GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

        /// <summary>Enumerates <c>*.npk</c> files under <see cref="Root"/> and parses each name into a firmware package.</summary>
        public IReadOnlyList<FirmwarePackageInfo> GetPackages()
        {
            List<FirmwarePackageInfo> list = [];
            foreach (string path in Directory.EnumerateFiles(Root, "*.npk", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(path);
                if (FirmwarePackageFileNameParser.TryParse(
                        fileName,
                        out string packageName,
                        out string? arch,
                        out RouterOsVersion version))
                {
                    FileInfo info = new(path);
                    list.Add(new FirmwarePackageInfo(
                        packageName,
                        fileName,
                        path,
                        arch,
                        version,
                        info.Length,
                        "00"));
                }
            }

            return list;
        }
    }
}
