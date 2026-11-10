using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Tests;

public sealed class DeviceCompatibilityAssessmentTests
{
    [Theory]
    [InlineData("6.49.10", -1)]
    [InlineData("6.49.23", 0)]
    [InlineData("6.49.24", 1)]
    [InlineData("6.48.0 (long-term)", -1)]
    public void RouterOsVersion_ComparesNumerically(string text, int expectedSign)
    {
        Assert.True(RouterOsVersion.TryParse(text, out RouterOsVersion parsed));
        RouterOsVersion target = RouterOsVersion.Parse("6.49.23");
        int cmp = parsed.CompareTo(target);
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    [Theory]
    [InlineData("7.23.8", true)]
    [InlineData("not-a-version", false)]
    [InlineData("", false)]
    public void RouterOsVersion_ParseGuards(string text, bool expectSuccess)
    {
        Assert.Equal(expectSuccess, RouterOsVersion.TryParse(text, out _));
    }

    [Fact]
    public void Validator_RejectsV7Target()
    {
        ValidateOptionsResult result = new FirmwareAssessmentOptionsValidator().Validate(
            null,
            new FirmwareAssessmentOptions { TargetRouterOsVersion = "7.23.8" });
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("6.49.10 (long-term)")]
    [InlineData("6.49rc1")]
    [InlineData("6.48beta40")]
    [InlineData("6.49.10rc1")]
    public void Assessor_OlderV6_IncludingChannelSuffixes_IsReadyForUpgrade(string version)
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice(version, "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.EligibleSubjectToManualPathVerification, assessment.UpgradePath);
        Assert.Equal(PackageCompatibilityStatus.Compatible, assessment.PackageCompatibility);
        Assert.Equal("routeros-mipsbe-6.49.23.npk", assessment.MatchingPackageFileName);
        Assert.Contains(assessment.Findings, static f => f.Code == "path.eligible_v6_to_target");
    }

    [Fact]
    public void Assessor_V7Packages_AreIgnored_UnderV6Target()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.10", "mipsbe"),
            CreateCatalog("routeros-7.23.8-mipsbe.npk", "routeros-7.23.8-arm.npk"));

        Assert.Equal(PackageCompatibilityStatus.Missing, assessment.PackageCompatibility);
        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
        Assert.DoesNotContain(
            assessment.Findings,
            static f => f.Message.Contains("7.23.8-mipsbe", StringComparison.Ordinal)
                && f.Code == "package.compatible");
    }

    [Fact]
    public void Assessor_ExactlyTarget_IsAlreadyAtTarget()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.23", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(DeviceAssessmentStatus.AlreadyAtTarget, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.AlreadyAtTarget, assessment.UpgradePath);
    }

    [Fact]
    public void Assessor_LaterV6_RequiresManualReview()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.24", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(DeviceAssessmentStatus.ManualReviewRequired, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.NewerV6RequiresReview, assessment.UpgradePath);
    }

    [Fact]
    public void Assessor_V7_IsUnsupported()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("7.23.8", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(DeviceAssessmentStatus.Unsupported, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.OutOfScopeV7, assessment.UpgradePath);
    }

    [Fact]
    public void Assessor_InvalidVersion_IsBlocked()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("bogus", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
    }

    [Fact]
    public void Assessor_MissingPackage_IsBlocked()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.10", "mipsbe"),
            CreateCatalog());

        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
        Assert.Equal(PackageCompatibilityStatus.Missing, assessment.PackageCompatibility);
    }

    [Fact]
    public void Assessor_WrongArchitecture_IsBlocked()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.10", "arm"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(PackageCompatibilityStatus.ArchitectureMismatch, assessment.PackageCompatibility);
        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
    }

    [Fact]
    public void Assessor_WrongPackageVersion_IsBlocked()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.10", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.48.0.npk"));

        Assert.Equal(PackageCompatibilityStatus.VersionMismatch, assessment.PackageCompatibility);
        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
    }

    [Fact]
    public void Assessor_AmbiguousPackages_IsBlocked()
    {
        string root = Directory.CreateTempSubdirectory("fw-amb-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1]);
            Directory.CreateDirectory(Path.Combine(root, "alt"));
            File.WriteAllBytes(Path.Combine(root, "alt", "routeros-mipsbe-6.49.23.npk"), [1]);

            DeviceCompatibilityAssessment assessment = Assess(
                CreateDevice("6.49.10", "mipsbe"),
                new DirectoryCatalog(root));

            Assert.Equal(PackageCompatibilityStatus.Ambiguous, assessment.PackageCompatibility);
            Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Assessor_MalformedFilenames_AreIgnored_MissingResult()
    {
        string root = Directory.CreateTempSubdirectory("fw-bad-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "routeros-not-a-package.npk"), [1]);
            File.WriteAllBytes(Path.Combine(root, "advanced-tools-6.49.23-mipsbe.npk"), [1]);

            DeviceCompatibilityAssessment assessment = Assess(
                CreateDevice("6.49.10", "mipsbe"),
                new DirectoryCatalog(root));

            Assert.Equal(PackageCompatibilityStatus.Missing, assessment.PackageCompatibility);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Assessor_PpcAlias_MatchesPowerpcPackage()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.10", "ppc"),
            CreateCatalog("routeros-powerpc-6.49.23.npk"));

        Assert.Equal(PackageCompatibilityStatus.Compatible, assessment.PackageCompatibility);
        Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);
    }

    [Fact]
    public void Routerboard_CurrentMatchesUpgrade()
    {
        MikrotikDeviceInfo device = CreateDevice("6.49.23", "mipsbe");
        device = new MikrotikDeviceInfo
        {
            Identity = device.Identity,
            Version = device.Version,
            ArchitectureName = device.ArchitectureName,
            BoardName = device.BoardName,
            Platform = device.Platform,
            Uptime = device.Uptime,
            RouterboardModel = "960PGS",
            RouterboardCurrentFirmware = "6.49.23",
            RouterboardUpgradeFirmware = "6.49.23",
        };

        DeviceCompatibilityAssessment assessment = Assess(device, CreateCatalog("routeros-mipsbe-6.49.23.npk"));
        Assert.Equal(RouterboardFirmwareStatus.Current, assessment.RouterboardAssessment);
    }

    [Fact]
    public void Routerboard_UpgradeAvailable_IsIndependentOfRouterOs()
    {
        MikrotikDeviceInfo device = new()
        {
            Identity = "MikroTik",
            Version = "6.49.23",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            Platform = "MikroTik",
            Uptime = "1h",
            RouterboardModel = "960PGS",
            RouterboardCurrentFirmware = "6.49.10",
            RouterboardUpgradeFirmware = "6.49.23",
        };

        DeviceCompatibilityAssessment assessment = Assess(device, CreateCatalog("routeros-mipsbe-6.49.23.npk"));
        Assert.Equal(RouterboardFirmwareStatus.UpgradeAvailable, assessment.RouterboardAssessment);
        Assert.Equal(DeviceAssessmentStatus.AlreadyAtTarget, assessment.OverallStatus);
    }

    [Fact]
    public void Routerboard_CurrentNewerThanUpgrade_IsNotUpgradeAvailable()
    {
        MikrotikDeviceInfo device = new()
        {
            Identity = "MikroTik",
            Version = "6.49.19",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            Platform = "MikroTik",
            Uptime = "1h",
            RouterboardModel = "960PGS",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "6.49.19",
        };

        DeviceCompatibilityAssessment assessment = Assess(device, CreateCatalog("routeros-mipsbe-6.49.23.npk"));
        Assert.Equal(RouterboardFirmwareStatus.Current, assessment.RouterboardAssessment);
        Assert.Contains(assessment.Findings, static f => f.Code == "routerboard.current_ahead");
        Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);
    }

    [Fact]
    public void Routerboard_Missing_IsUnavailable()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.23", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));
        Assert.Equal(RouterboardFirmwareStatus.Unavailable, assessment.RouterboardAssessment);
    }

    [Fact]
    public void PackageParser_ParsesV6AndV7FileNames()
    {
        Assert.True(FirmwarePackageFileNameParser.TryParse(
            "routeros-mipsbe-6.49.23.npk", out string name6, out string? arch6, out RouterOsVersion v6));
        Assert.Equal("routeros", name6);
        Assert.Equal("mipsbe", arch6);
        Assert.Equal(RouterOsVersion.Parse("6.49.23"), v6);

        Assert.True(FirmwarePackageFileNameParser.TryParse(
            "routeros-7.23.8-arm.npk", out string name7, out string? arch7, out RouterOsVersion v7));
        Assert.Equal("routeros", name7);
        Assert.Equal("arm", arch7);
        Assert.Equal(RouterOsVersion.Parse("7.23.8"), v7);

        Assert.True(FirmwarePackageFileNameParser.TryParse(
            "tr069-client-6.49.23-mipsbe.npk", out string tr, out string? trArch, out RouterOsVersion trVer));
        Assert.Equal("tr069-client", tr);
        Assert.Equal("mipsbe", trArch);
        Assert.Equal(RouterOsVersion.Parse("6.49.23"), trVer);

        Assert.True(FirmwarePackageFileNameParser.TryParse(
            "user-manager-6.49.23-mipsbe.npk", out string um, out string? umArch, out _));
        Assert.Equal("user-manager", um);
        Assert.Equal("mipsbe", umArch);
    }

    private static MikrotikDeviceInfo CreateDevice(string version, string architecture) =>
        new()
        {
            Identity = "MikroTik",
            Version = version,
            ArchitectureName = architecture,
            BoardName = "hEX PoE",
            Platform = "MikroTik",
            Uptime = "1h",
        };

    private static DeviceCompatibilityAssessment Assess(
        MikrotikDeviceInfo device,
        IFirmwarePackageCatalog catalog)
    {
        DeviceCompatibilityAssessor assessor = new(
            catalog,
            Options.Create(new FirmwareAssessmentOptions { TargetRouterOsVersion = "6.49.23" }));
        return assessor.Assess("198.18.0.83", device, DateTimeOffset.Parse("2026-11-10T17:00:00Z"));
    }

    private static IFirmwarePackageCatalog CreateCatalog(params string[] fileNames)
    {
        string root = Directory.CreateTempSubdirectory("fw-cat-").FullName;
        foreach (string name in fileNames)
        {
            File.WriteAllBytes(Path.Combine(root, name), [1]);
        }

        return new DirectoryCatalog(root, deleteOnEnumerate: false);
    }

    private sealed class DirectoryCatalog(string root, bool deleteOnEnumerate = false) : IFirmwarePackageCatalog
    {
        public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
            GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

        public IReadOnlyList<FirmwarePackageInfo> GetPackages()
        {
            List<FirmwarePackageInfo> list = [];
            foreach (string path in Directory.EnumerateFiles(root, "*.npk", SearchOption.AllDirectories))
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

            if (deleteOnEnumerate)
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                }
            }

            return list;
        }
    }
}
