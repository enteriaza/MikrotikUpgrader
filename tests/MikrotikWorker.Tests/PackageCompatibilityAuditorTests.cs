using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests;

public sealed class PackageCompatibilityAuditorTests
{
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

    [Fact]
    public void WrongArchitecture_IsArchitectureMismatch()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.UnsupportedArchitecture(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.ArchitectureMismatch, v6.Status);
    }

    [Fact]
    public void WrongVersionOnly_IsVersionMismatch()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.MainOnlyMipsbeOlderV6(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-mipsbe-6.48.0.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.VersionMismatch, v6.Status);
    }

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

    [Fact]
    public void InvalidEmptyInventory_IsInvalid()
    {
        PackageCompatibilityStageResult v6 = AssessStage(
            SyntheticPackageInventoryFixtures.InvalidEmptyInventory(),
            PackageCompatibilityUpgradeStage.V6Stepping,
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(CatalogueCompatibilityStatus.InvalidInventory, v6.Status);
    }

    [Theory]
    [InlineData("ace-identity", DeviceType.Ace, true)]
    [InlineData("br-identity", DeviceType.Br, true)]
    [InlineData("ap-identity", DeviceType.Ap, false)]
    [InlineData("dcl-identity", DeviceType.Dcl, false)]
    [InlineData("empty-identity", DeviceType.Unknown, false)]
    [InlineData("unknown-identity", DeviceType.Unknown, false)]
    public void IdentityPolicy_V7NotApplicableOnlyForAceBr(
        string fixtureName,
        DeviceType expectedType,
        bool v7NotApplicable)
    {
        PackageCompatibilityAuditDevice device = SyntheticPackageInventoryFixtures.All()
            .Single(d => d.FixtureName == fixtureName);
        PackageCompatibilityDeviceReport report = AssessDevice(
            device,
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(expectedType, report.Classification.DeviceType);
        PackageCompatibilityStageResult v7 = report.Stages.Single(
            static s => s.Stage == PackageCompatibilityUpgradeStage.RouterOs7);
        if (v7NotApplicable)
        {
            Assert.Equal(CatalogueCompatibilityStatus.NotApplicable, v7.Status);
            Assert.Empty(v7.ResolvedPackageFileNames);
        }
        else
        {
            Assert.Equal(CatalogueCompatibilityStatus.Compatible, v7.Status);
        }
    }

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
            Assert.Contains("NotApplicable", csv, StringComparison.Ordinal);
            Assert.Contains("ace-identity", csv, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(output, true);
        }
    }

    private static PackageCompatibilityStageResult AssessStage(
        PackageCompatibilityAuditDevice device,
        PackageCompatibilityUpgradeStage stage,
        IFirmwarePackageCatalog catalog,
        string? v7Target = "7.23.8") =>
        AssessDevice(device, catalog, v7Target).Stages.Single(s => s.Stage == stage);

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

    private static string GetRoot(IFirmwarePackageCatalog catalog) =>
        catalog is DirectoryCatalog dir ? dir.Root : Directory.CreateTempSubdirectory("compat-root-").FullName;

    private static IFirmwarePackageCatalog CreateCatalog(params string[] fileNames)
    {
        string root = Directory.CreateTempSubdirectory("compat-cat-").FullName;
        foreach (string name in fileNames)
        {
            File.WriteAllBytes(Path.Combine(root, name), [1, 2, 3, 4]);
        }

        return new DirectoryCatalog(root);
    }

    private sealed class DirectoryCatalog : IFirmwarePackageCatalog
    {
        public DirectoryCatalog(string root) => Root = root;

        public string Root { get; }

        public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
            GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

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
