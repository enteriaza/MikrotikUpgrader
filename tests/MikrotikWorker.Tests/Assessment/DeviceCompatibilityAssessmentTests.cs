// -----------------------------------------------------------------------------
// File: DeviceCompatibilityAssessmentTests.cs
//
// Description:
// Tests DeviceCompatibilityAssessor classifications, package matching, and overall
// status for v6 stepping / direct-v7 / already-at-target scenarios.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Assessment;
/// <summary>
/// Covers device compatibility assessment outcomes.
/// </summary>
public sealed class DeviceCompatibilityAssessmentTests
{
    /// <summary>RouterOS 7 target version assumed by the assessment scenarios.</summary>
    private const string V7Target = "7.23.8";

    /// <summary>
    /// Scenario: Version strings around the 6.49.23 stepping release (6.48.0 long-term through 6.50.1) are parsed and
    /// compared with <c>RouterOsUpgradeVersions.V6Stepping</c>.
    /// Expected: Every input parses, and the sign of the comparison matches the InlineData value: -1 below 6.49.23,
    /// 0 for 6.49.23 itself, +1 above it.
    /// Invariant: Ordering is numeric per component, so 6.49.24 is newer than 6.49.23 and the long-term suffix does
    /// not affect ordering.
    /// </summary>
    [Theory]
    [InlineData("6.49.18", -1)]
    [InlineData("6.49.19", -1)]
    [InlineData("6.49.20", -1)]
    [InlineData("6.49.23", 0)]
    [InlineData("6.49.24", 1)]
    [InlineData("6.48.0 (long-term)", -1)]
    [InlineData("6.50.1", 1)]
    public void RouterOsVersion_ComparesNumericallyAgainstV6Stepping(string text, int expectedSign)
    {
        Assert.True(RouterOsVersion.TryParse(text, out RouterOsVersion parsed));
        int cmp = parsed.CompareTo(RouterOsUpgradeVersions.V6Stepping);
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    /// <summary>
    /// Scenario: Versions 7.22.0, 7.23.8 and 7.24.0 are parsed and compared with the 7.23.8 target.
    /// Expected: Each input parses and the comparison sign is -1, 0 and +1 respectively.
    /// Invariant: Comparison against the RouterOS 7 target is numeric, never lexical.
    /// </summary>
    [Theory]
    [InlineData("7.22.0", -1)]
    [InlineData("7.23.8", 0)]
    [InlineData("7.24.0", 1)]
    public void RouterOsVersion_ComparesNumericallyAgainstV7Target(string text, int expectedSign)
    {
        Assert.True(RouterOsVersion.TryParse(text, out RouterOsVersion parsed));
        int cmp = parsed.CompareTo(RouterOsVersion.Parse(V7Target));
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    /// <summary>
    /// Scenario: A valid version (7.23.8), non-version text, and an empty string are passed to
    /// <c>RouterOsVersion.TryParse</c>.
    /// Expected: Only 7.23.8 parses successfully; the other inputs return false.
    /// Invariant: Malformed version text is rejected without throwing.
    /// </summary>
    [Theory]
    [InlineData("7.23.8", true)]
    [InlineData("not-a-version", false)]
    [InlineData("", false)]
    public void RouterOsVersion_ParseGuards(string text, bool expectSuccess)
    {
        Assert.Equal(expectSuccess, RouterOsVersion.TryParse(text, out _));
    }

    /// <summary>
    /// Scenario: Devices on a RouterOS 6 release older than the 6.49.23 stepping version (6.48beta40, 6.49rc1,
    /// 6.49.10 long-term, 6.49.18, 6.49.19, 6.49.20) are assessed as mipsbe boards against a catalog holding the
    /// 6.49.19 and 6.49.23 stepping packages plus the RouterOS 7.23.8 package.
    /// Expected: The device is ReadyForUpgrade on the EligibleSubjectToManualPathVerification path and classified
    /// OlderThanTargetV6. Package compatibility is Compatible and the match is routeros-mipsbe-6.49.23.npk, with
    /// 7.23.8 as the target version. The path.eligible_v6_stepping_then_v7 finding is present.
    /// Invariant: The 6.49.19 package is never reported as the compatible package, so stepping always targets
    /// 6.49.23.
    /// </summary>
    [Theory]
    [InlineData("6.49.10 (long-term)")]
    [InlineData("6.49rc1")]
    [InlineData("6.48beta40")]
    [InlineData("6.49.18")]
    [InlineData("6.49.19")]
    [InlineData("6.49.20")]
    public void Assessor_OlderV6_IsReadyForSteppingThenV7(string version)
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice(version, "mipsbe"),
            CreateCatalog(
                "routeros-mipsbe-6.49.19.npk",
                "routeros-mipsbe-6.49.23.npk",
                "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.EligibleSubjectToManualPathVerification, assessment.UpgradePath);
        Assert.Equal(RouterOsVersionClassification.OlderThanTargetV6, assessment.RouterOsVersionClassification);
        Assert.Equal(PackageCompatibilityStatus.Compatible, assessment.PackageCompatibility);
        Assert.Equal("routeros-mipsbe-6.49.23.npk", assessment.MatchingPackageFileName);
        Assert.Equal(V7Target, assessment.TargetRouterOsVersion);
        Assert.Contains(assessment.Findings, static f => f.Code == "path.eligible_v6_stepping_then_v7");
        Assert.DoesNotContain(
            assessment.Findings,
            static f => f.Message.Contains("6.49.19", StringComparison.Ordinal) && f.Code == "package.compatible");
    }

    /// <summary>
    /// Scenario: Devices already at or beyond the 6.49.23 stepping release (6.49.23, 6.49.24, 6.50.1) are assessed as
    /// mipsbe boards against a catalog holding the 6.49.23 and 7.23.8 packages.
    /// Expected: The device is ReadyForUpgrade via SkipV6SteppingProceedToV7, package compatibility is Compatible and
    /// the match is routeros-7.23.8-mipsbe.npk. The path.skip_v6_stepping finding is present and
    /// path.eligible_v6_stepping_then_v7 is absent.
    /// Invariant: A device that no longer needs v6 stepping is never routed through the stepping path.
    /// </summary>
    [Theory]
    [InlineData("6.49.23")]
    [InlineData("6.49.24")]
    [InlineData("6.50.1")]
    public void Assessor_AtOrNewerV6_SkipsSteppingAndProceedsToV7(string version)
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice(version, "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.SkipV6SteppingProceedToV7, assessment.UpgradePath);
        Assert.Equal(PackageCompatibilityStatus.Compatible, assessment.PackageCompatibility);
        Assert.Equal("routeros-7.23.8-mipsbe.npk", assessment.MatchingPackageFileName);
        Assert.DoesNotContain(
            assessment.Findings,
            static f => f.Code == "path.eligible_v6_stepping_then_v7");
        Assert.Contains(assessment.Findings, static f => f.Code == "path.skip_v6_stepping");
    }

    /// <summary>
    /// Scenario: A 6.49.24 mipsbe device is assessed against a catalog that holds only the 6.49.23 stepping package
    /// and no RouterOS 7 package. Package resolution for 6.49.23 and later targets the dynamic RouterOS 7 release,
    /// not 6.49.23.
    /// Expected: Package compatibility is Missing, overall status is Blocked, the path is SkipV6SteppingProceedToV7,
    /// and no matching package file name is reported.
    /// Invariant: The stepping package is never offered as a substitute for the missing RouterOS 7 package.
    /// </summary>
    [Fact]
    public void Assessor_NewerV6_DoesNotMatchSteppingPackage()
    {
        // Package resolution for ≥6.49.23 targets the dynamic v7 release, not 6.49.23.
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.24", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(PackageCompatibilityStatus.Missing, assessment.PackageCompatibility);
        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.SkipV6SteppingProceedToV7, assessment.UpgradePath);
        Assert.Null(assessment.MatchingPackageFileName);
    }

    /// <summary>
    /// Scenario: A 6.49.20 mipsbe device is assessed against a catalog holding only the 6.49.19 stepping package and
    /// the RouterOS 7.23.8 package.
    /// Expected: The path is EligibleSubjectToManualPathVerification, package compatibility is VersionMismatch, the
    /// overall status is Blocked, and no package.compatible finding is raised.
    /// Invariant: The 6.49.19 package is never accepted for the stepping stage, which requires 6.49.23.
    /// </summary>
    [Fact]
    public void Assessor_SteppingStage_NeverSelects64919Package()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.20", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.19.npk", "routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(UpgradePathStatus.EligibleSubjectToManualPathVerification, assessment.UpgradePath);
        Assert.Equal(PackageCompatibilityStatus.VersionMismatch, assessment.PackageCompatibility);
        Assert.NotEqual(PackageCompatibilityStatus.Compatible, assessment.PackageCompatibility);
        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
        Assert.DoesNotContain(
            assessment.Findings,
            static f => f.Code == "package.compatible");
    }

    /// <summary>
    /// Scenario: A 6.49.10 mipsbe device is assessed against a catalog containing only RouterOS 7.23.8 packages
    /// (mipsbe and arm).
    /// Expected: Package compatibility is Missing, the overall status is Blocked, and no package.compatible finding
    /// mentions 7.23.8-mipsbe.
    /// Invariant: A RouterOS 7 package never satisfies the 6.49.23 stepping requirement of a v6 device.
    /// </summary>
    [Fact]
    public void Assessor_V7OnlyCatalog_DoesNotSatisfyOlderV6Stepping()
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

    /// <summary>
    /// Scenario: A 7.22.0 mipsbe device is assessed against a catalog holding routeros-7.23.8-mipsbe.npk.
    /// Expected: The device is ReadyForUpgrade on the EligibleDirectV7Upgrade path and the matching package is
    /// routeros-7.23.8-mipsbe.npk.
    /// Invariant: A RouterOS 7 device behind the target upgrades directly, with no v6 stepping.
    /// </summary>
    [Fact]
    public void Assessor_V7OlderThanTarget_IsReadyForDirectUpgrade()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("7.22.0", "mipsbe"),
            CreateCatalog("routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.EligibleDirectV7Upgrade, assessment.UpgradePath);
        Assert.Equal("routeros-7.23.8-mipsbe.npk", assessment.MatchingPackageFileName);
    }

    /// <summary>
    /// Scenario: A 7.23.8 mipsbe device is assessed when the RouterOS 7 target is also 7.23.8.
    /// Expected: The overall status is AlreadyAtTarget and the path is AlreadyAtV7Target.
    /// Invariant: A device already on the target version is never scheduled for an upgrade.
    /// </summary>
    [Fact]
    public void Assessor_V7ExactlyTarget_IsAlreadyAtTarget()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("7.23.8", "mipsbe"),
            CreateCatalog("routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(DeviceAssessmentStatus.AlreadyAtTarget, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.AlreadyAtV7Target, assessment.UpgradePath);
    }

    /// <summary>
    /// Scenario: A 7.24.0 mipsbe device is assessed against a 7.23.8 target.
    /// Expected: The overall status is AlreadyAtTarget and the path is NewerThanV7Target.
    /// Invariant: A device newer than the target is never downgraded.
    /// </summary>
    [Fact]
    public void Assessor_V7NewerThanTarget_DoesNotDowngrade()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("7.24.0", "mipsbe"),
            CreateCatalog("routeros-7.23.8-mipsbe.npk"));

        Assert.Equal(DeviceAssessmentStatus.AlreadyAtTarget, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.NewerThanV7Target, assessment.UpgradePath);
    }

    /// <summary>
    /// Scenario: A 7.22.0 mipsbe device is assessed with a matching catalog but the RouterOS 7 target provider
    /// returns no validated target (<c>null</c>).
    /// Expected: The overall status and the path are Blocked, and a path.v7_target_unavailable finding is raised.
    /// Invariant: Without a validated target version the assessor never reports a device as ready to upgrade.
    /// </summary>
    [Fact]
    public void Assessor_V7WithoutValidatedTarget_IsBlocked()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("7.22.0", "mipsbe"),
            CreateCatalog("routeros-7.23.8-mipsbe.npk"),
            v7Target: null);

        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
        Assert.Equal(UpgradePathStatus.Blocked, assessment.UpgradePath);
        Assert.Contains(assessment.Findings, static f => f.Code == "path.v7_target_unavailable");
    }

    /// <summary>
    /// Scenario: A device reporting the unparseable version string <c>bogus</c> is assessed against a catalog holding
    /// a valid 6.49.23 package.
    /// Expected: The overall status is Blocked.
    /// Invariant: An unparseable device version blocks the upgrade instead of being guessed.
    /// </summary>
    [Fact]
    public void Assessor_InvalidVersion_IsBlocked()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("bogus", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
    }

    /// <summary>
    /// Scenario: A 6.49.10 mipsbe device is assessed against an empty package catalog.
    /// Expected: Package compatibility is Missing and the overall status is Blocked.
    /// Invariant: An upgrade is never offered when no package exists for the device.
    /// </summary>
    [Fact]
    public void Assessor_MissingPackage_IsBlocked()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.10", "mipsbe"),
            CreateCatalog());

        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
        Assert.Equal(PackageCompatibilityStatus.Missing, assessment.PackageCompatibility);
    }

    /// <summary>
    /// Scenario: A 6.49.10 arm device is assessed against a catalog that only holds a mipsbe 6.49.23 package.
    /// Expected: Package compatibility is ArchitectureMismatch and the overall status is Blocked.
    /// Invariant: A package built for another architecture is never treated as compatible.
    /// </summary>
    [Fact]
    public void Assessor_WrongArchitecture_IsBlocked()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.10", "arm"),
            CreateCatalog("routeros-mipsbe-6.49.23.npk"));

        Assert.Equal(PackageCompatibilityStatus.ArchitectureMismatch, assessment.PackageCompatibility);
        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
    }

    /// <summary>
    /// Scenario: A 6.49.10 mipsbe device is assessed against a catalog that only holds a 6.48.0 mipsbe package.
    /// Expected: Package compatibility is VersionMismatch and the overall status is Blocked.
    /// Invariant: Only the exact required package version is accepted; an older package does not qualify.
    /// </summary>
    [Fact]
    public void Assessor_WrongPackageVersion_IsBlocked()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.10", "mipsbe"),
            CreateCatalog("routeros-mipsbe-6.48.0.npk"));

        Assert.Equal(PackageCompatibilityStatus.VersionMismatch, assessment.PackageCompatibility);
        Assert.Equal(DeviceAssessmentStatus.Blocked, assessment.OverallStatus);
    }

    /// <summary>
    /// Scenario: A temp directory holds two copies of routeros-mipsbe-6.49.23.npk (one in a subfolder), and a
    /// 6.49.10 mipsbe device is assessed against it.
    /// Expected: Package compatibility is Ambiguous and the overall status is Blocked.
    /// Invariant: Duplicate matching packages are never resolved by picking one arbitrarily.
    /// </summary>
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

    /// <summary>
    /// Scenario: A catalog directory contains only <c>routeros-not-a-package.npk</c> and a non-routeros package
    /// (<c>advanced-tools-6.49.23-mipsbe.npk</c>), and a 6.49.10 mipsbe device is assessed.
    /// Expected: Package compatibility is Missing.
    /// Invariant: Unparseable file names and non-routeros packages are ignored rather than matched or reported as errors.
    /// </summary>
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

    /// <summary>
    /// Scenario: A 6.49.10 device reporting architecture <c>ppc</c> is assessed against powerpc 6.49.23 and
    /// 7.23.8 packages.
    /// Expected: Package compatibility is Compatible and the overall status is ReadyForUpgrade.
    /// Invariant: The <c>ppc</c> architecture name is treated as an alias of <c>powerpc</c> when matching packages.
    /// </summary>
    [Fact]
    public void Assessor_PpcAlias_MatchesPowerpcPackage()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("6.49.10", "ppc"),
            CreateCatalog("routeros-powerpc-6.49.23.npk", "routeros-7.23.8-powerpc.npk"));

        Assert.Equal(PackageCompatibilityStatus.Compatible, assessment.PackageCompatibility);
        Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);
    }

    /// <summary>
    /// Scenario: A 7.23.8 device whose RouterBOARD current and upgrade firmware are both 7.23.8 is assessed.
    /// Expected: The RouterBOARD assessment is Current and the overall status is AlreadyAtTarget.
    /// Invariant: Matching RouterBOARD firmware versions never flag a firmware upgrade.
    /// </summary>
    [Fact]
    public void Routerboard_CurrentMatchesUpgrade()
    {
        MikrotikDeviceInfo device = CreateDevice("7.23.8", "mipsbe");
        device = new MikrotikDeviceInfo
        {
            Identity = device.Identity,
            Version = device.Version,
            ArchitectureName = device.ArchitectureName,
            BoardName = device.BoardName,
            Platform = device.Platform,
            Uptime = device.Uptime,
            RouterboardModel = "960PGS",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "7.23.8",
        };

        DeviceCompatibilityAssessment assessment = Assess(
            device,
            CreateCatalog("routeros-7.23.8-mipsbe.npk"));
        Assert.Equal(RouterboardFirmwareStatus.Current, assessment.RouterboardAssessment);
        Assert.Equal(DeviceAssessmentStatus.AlreadyAtTarget, assessment.OverallStatus);
    }

    /// <summary>
    /// Scenario: A device already on RouterOS 7.23.8 reports RouterBOARD current firmware 6.49.10 and upgrade
    /// firmware 7.23.8.
    /// Expected: The RouterBOARD assessment is UpgradeAvailable while the overall status remains AlreadyAtTarget.
    /// Invariant: Pending RouterBOARD firmware is assessed separately and does not change the RouterOS status.
    /// </summary>
    [Fact]
    public void Routerboard_UpgradeAvailable_IsIndependentOfRouterOs()
    {
        MikrotikDeviceInfo device = new()
        {
            Identity = "MikroTik",
            Version = "7.23.8",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            Platform = "MikroTik",
            Uptime = "1h",
            RouterboardModel = "960PGS",
            RouterboardCurrentFirmware = "6.49.10",
            RouterboardUpgradeFirmware = "7.23.8",
        };

        DeviceCompatibilityAssessment assessment = Assess(
            device,
            CreateCatalog("routeros-7.23.8-mipsbe.npk"));
        Assert.Equal(RouterboardFirmwareStatus.UpgradeAvailable, assessment.RouterboardAssessment);
        Assert.Equal(DeviceAssessmentStatus.AlreadyAtTarget, assessment.OverallStatus);
    }

    /// <summary>
    /// Scenario: A 6.49.18 device reports RouterBOARD current firmware 7.23.8, which is newer than its upgrade
    /// firmware 6.49.23.
    /// Expected: The RouterBOARD assessment is Current, a routerboard.current_ahead finding is raised, and the
    /// overall status stays ReadyForUpgrade.
    /// Invariant: Current firmware ahead of the bundled upgrade firmware is never offered as an upgrade.
    /// </summary>
    [Fact]
    public void Routerboard_CurrentNewerThanUpgrade_IsNotUpgradeAvailable()
    {
        MikrotikDeviceInfo device = new()
        {
            Identity = "MikroTik",
            Version = "6.49.18",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            Platform = "MikroTik",
            Uptime = "1h",
            RouterboardModel = "960PGS",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "6.49.23",
        };

        DeviceCompatibilityAssessment assessment = Assess(
            device,
            CreateCatalog("routeros-mipsbe-6.49.23.npk", "routeros-7.23.8-mipsbe.npk"));
        Assert.Equal(RouterboardFirmwareStatus.Current, assessment.RouterboardAssessment);
        Assert.Contains(assessment.Findings, static f => f.Code == "routerboard.current_ahead");
        Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);
    }

    /// <summary>
    /// Scenario: A 7.23.8 device that reports no RouterBOARD firmware information is assessed.
    /// Expected: The RouterBOARD assessment is Unavailable.
    /// Invariant: Missing RouterBOARD data is reported as unavailable rather than assumed current.
    /// </summary>
    [Fact]
    public void Routerboard_Missing_IsUnavailable()
    {
        DeviceCompatibilityAssessment assessment = Assess(
            CreateDevice("7.23.8", "mipsbe"),
            CreateCatalog("routeros-7.23.8-mipsbe.npk"));
        Assert.Equal(RouterboardFirmwareStatus.Unavailable, assessment.RouterboardAssessment);
    }

    /// <summary>
    /// Scenario: <c>FirmwarePackageFileNameParser.TryParse</c> is given v6 (<c>routeros-mipsbe-6.49.23.npk</c>), v7
    /// (<c>routeros-7.23.8-arm.npk</c>) and extra-package (<c>tr069-client</c>, <c>user-manager</c>) file names.
    /// Expected: Each name parses into its package name, architecture and version (6.49.23 mipsbe, 7.23.8 arm,
    /// tr069-client mipsbe, user-manager mipsbe).
    /// Invariant: Both the v6 name-arch-version and v7 name-version-arch layouts parse, including hyphenated
    /// package names.
    /// </summary>
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

    /// <summary>
    /// Builds a hEX PoE <c>MikrotikDeviceInfo</c> with the given RouterOS version and architecture, using fixed
    /// identity, platform and uptime values.
    /// </summary>
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

    /// <summary>
    /// Private helper <c>Assess</c> supporting the scenarios in this file.
    /// </summary>
    private static DeviceCompatibilityAssessment Assess(
        MikrotikDeviceInfo device,
        IFirmwarePackageCatalog catalog,
        string? v7Target = V7Target)
    {
        DeviceCompatibilityAssessor assessor = new(
            catalog,
            new FixedRouterOsV7TargetProvider(v7Target));
        return assessor.Assess("198.18.0.83", device, DateTimeOffset.Parse("2026-11-10T17:00:00Z"));
    }

    /// <summary>
    /// Private helper <c>CreateCatalog</c> supporting the scenarios in this file.
    /// </summary>
    private static IFirmwarePackageCatalog CreateCatalog(params string[] fileNames)
    {
        string root = Directory.CreateTempSubdirectory("fw-cat-").FullName;
        foreach (string name in fileNames)
        {
            File.WriteAllBytes(Path.Combine(root, name), [1]);
        }

        return new DirectoryCatalog(root, deleteOnEnumerate: false);
    }

    /// <summary>
    /// Firmware package catalogue fake that scans <c>*.npk</c> files under a temp directory; when
    /// <c>deleteOnEnumerate</c> is set it deletes the root first so the catalogue appears empty.
    /// </summary>
    private sealed class DirectoryCatalog(string root, bool deleteOnEnumerate = false) : IFirmwarePackageCatalog
    {
        /// <summary>Gets only the main <c>routeros</c> packages from <see cref="GetPackages"/>.</summary>
        public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
            GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

        /// <summary>
        /// Enumerates <c>*.npk</c> files under the fixture root; when configured to delete on enumerate,
        /// the root is removed first so the catalogue appears empty.
        /// </summary>
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
