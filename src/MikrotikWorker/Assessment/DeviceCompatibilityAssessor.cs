using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Assessment;

internal interface IDeviceCompatibilityAssessor
{
    DeviceCompatibilityAssessment Assess(
        string routerIp,
        MikrotikDeviceInfo deviceInfo,
        DateTimeOffset assessedAtUtc);
}

internal sealed class DeviceCompatibilityAssessor(
    IFirmwarePackageCatalog packageCatalog,
    IOptions<FirmwareAssessmentOptions> assessmentOptions) : IDeviceCompatibilityAssessor
{
    public DeviceCompatibilityAssessment Assess(
        string routerIp,
        MikrotikDeviceInfo deviceInfo,
        DateTimeOffset assessedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routerIp);
        ArgumentNullException.ThrowIfNull(deviceInfo);

        RouterOsVersion target = RouterOsVersion.Parse(assessmentOptions.Value.TargetRouterOsVersion);
        List<AssessmentFinding> findings = [];

        RouterOsVersion? deviceVersion = null;
        RouterOsVersionClassification classification;
        if (!RouterOsVersion.TryParse(deviceInfo.Version, out RouterOsVersion parsedDevice))
        {
            classification = RouterOsVersionClassification.Unknown;
            findings.Add(new(
                AssessmentFindingSeverity.Blocker,
                "version.unparseable",
                $"RouterOS version '{deviceInfo.Version}' could not be parsed."));
        }
        else
        {
            deviceVersion = parsedDevice;
            classification = Classify(parsedDevice, target);
        }

        UpgradePathStatus upgradePath = AssessUpgradePath(classification, findings);
        PackageMatchResult package = MatchPackage(deviceInfo.ArchitectureName, target, findings);
        RouterboardFirmwareStatus routerboard = AssessRouterboard(deviceInfo, findings);

        DeviceAssessmentStatus overall = DetermineOverall(
            classification,
            upgradePath,
            package.Status,
            findings);

        return new DeviceCompatibilityAssessment
        {
            RouterIp = routerIp,
            RouterIdentity = NullIfWhiteSpace(deviceInfo.Identity),
            RouterOsVersionRaw = deviceInfo.Version,
            RouterOsVersion = deviceVersion,
            RouterOsVersionClassification = classification,
            Architecture = deviceInfo.ArchitectureName,
            BoardName = deviceInfo.BoardName,
            Platform = deviceInfo.Platform,
            RouterboardModel = deviceInfo.RouterboardModel,
            RouterboardFirmwareType = deviceInfo.RouterboardFirmwareType,
            RouterboardCurrentFirmware = deviceInfo.RouterboardCurrentFirmware,
            RouterboardUpgradeFirmware = deviceInfo.RouterboardUpgradeFirmware,
            RouterboardFactoryFirmware = deviceInfo.RouterboardFactoryFirmware,
            TargetRouterOsVersion = target.ToString(),
            MatchingPackageFileName = package.FileName,
            PackageArchitecture = package.Architecture,
            PackageVersion = package.Version?.ToString(),
            PackageCompatibility = package.Status,
            UpgradePath = upgradePath,
            RouterboardAssessment = routerboard,
            OverallStatus = overall,
            AssessedAtUtc = assessedAtUtc,
            Findings = findings,
        };
    }

    private static RouterOsVersionClassification Classify(RouterOsVersion device, RouterOsVersion target)
    {
        if (device.IsV7OrLater)
        {
            return RouterOsVersionClassification.RouterOsV7OrLater;
        }

        if (!device.IsV6)
        {
            return RouterOsVersionClassification.Unknown;
        }

        int cmp = device.CompareTo(target);
        if (cmp < 0)
        {
            return RouterOsVersionClassification.OlderThanTargetV6;
        }

        if (cmp == 0)
        {
            return RouterOsVersionClassification.AtTargetV6;
        }

        return RouterOsVersionClassification.NewerThanTargetV6;
    }

    private static UpgradePathStatus AssessUpgradePath(
        RouterOsVersionClassification classification,
        List<AssessmentFinding> findings)
    {
        switch (classification)
        {
            case RouterOsVersionClassification.OlderThanTargetV6:
                findings.Add(new(
                    AssessmentFindingSeverity.Info,
                    "path.eligible_v6_to_target",
                    "Device reports an older RouterOS v6 release and is eligible for automated upgrade to the configured v6 target."));
                return UpgradePathStatus.EligibleSubjectToManualPathVerification;

            case RouterOsVersionClassification.AtTargetV6:
                findings.Add(new(
                    AssessmentFindingSeverity.Info,
                    "path.already_at_target",
                    "Device already reports the configured RouterOS v6 target version."));
                return UpgradePathStatus.AlreadyAtTarget;

            case RouterOsVersionClassification.NewerThanTargetV6:
                findings.Add(new(
                    AssessmentFindingSeverity.Warning,
                    "path.newer_v6",
                    "Device reports a RouterOS v6 version newer than the configured target. Automatic downgrade is not performed."));
                return UpgradePathStatus.NewerV6RequiresReview;

            case RouterOsVersionClassification.RouterOsV7OrLater:
                findings.Add(new(
                    AssessmentFindingSeverity.Blocker,
                    "path.v7_out_of_scope",
                    "RouterOS v7 (and later) is out of scope for the current upgrade policy. No v7 upgrade is proposed."));
                return UpgradePathStatus.OutOfScopeV7;

            default:
                findings.Add(new(
                    AssessmentFindingSeverity.Blocker,
                    "path.unknown",
                    "Upgrade path cannot be assessed because the RouterOS version is unknown or unparseable."));
                return UpgradePathStatus.UnknownRequiresManualReview;
        }
    }

    private PackageMatchResult MatchPackage(
        string? deviceArchitecture,
        RouterOsVersion target,
        List<AssessmentFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(deviceArchitecture))
        {
            findings.Add(new(
                AssessmentFindingSeverity.Blocker,
                "package.architecture_missing",
                "Device architecture is missing; firmware package selection is blocked."));
            return new(PackageCompatibilityStatus.Unidentifiable, null, null, null);
        }

        string normalizedDeviceArch = FirmwarePackageFileNameParser.NormalizeArchitecture(deviceArchitecture);
        IReadOnlyList<FirmwarePackageInfo> all = packageCatalog.GetPackages();
        if (all.Count == 0)
        {
            findings.Add(new(
                AssessmentFindingSeverity.Blocker,
                "package.catalog_empty",
                "No parseable *.npk packages were found under FirmwareFiles."));
            return new(PackageCompatibilityStatus.Missing, null, null, null);
        }

        // Policy packages only: main routeros packages with same major as the configured target.
        List<FirmwarePackageInfo> policyPackages = all
            .Where(p => p.PackageName.Equals("routeros", StringComparison.Ordinal)
                && p.Version.Major == target.Major)
            .ToList();

        if (policyPackages.Count == 0)
        {
            findings.Add(new(
                AssessmentFindingSeverity.Blocker,
                "package.catalog_empty",
                $"No RouterOS v{target.Major} packages were found under FirmwareFiles (v7 packages are ignored under the current policy)."));
            return new(PackageCompatibilityStatus.Missing, null, null, null);
        }

        List<FirmwarePackageInfo> archMatches = policyPackages
            .Where(p => FirmwarePackageFileNameParser.ArchitecturesMatch(normalizedDeviceArch, p.Architecture))
            .ToList();

        if (archMatches.Count == 0)
        {
            findings.Add(new(
                AssessmentFindingSeverity.Blocker,
                "package.architecture_mismatch",
                $"No RouterOS v{target.Major} package architecture matches device architecture '{normalizedDeviceArch}'."));
            return new(PackageCompatibilityStatus.ArchitectureMismatch, null, null, null);
        }

        List<FirmwarePackageInfo> versionMatches = archMatches
            .Where(p => p.Version == target)
            .ToList();

        if (versionMatches.Count == 0)
        {
            string seen = string.Join(", ", archMatches.Select(p => p.Version.ToString()).Distinct().Order());
            findings.Add(new(
                AssessmentFindingSeverity.Blocker,
                "package.version_mismatch",
                $"Architecture '{normalizedDeviceArch}' packages were found, but none match target {target}. Observed: {seen}."));
            return new(
                PackageCompatibilityStatus.VersionMismatch,
                archMatches[0].FileName,
                archMatches[0].Architecture,
                archMatches[0].Version);
        }

        if (versionMatches.Count > 1)
        {
            findings.Add(new(
                AssessmentFindingSeverity.Blocker,
                "package.ambiguous",
                $"Multiple packages match architecture '{normalizedDeviceArch}' and target {target}: {string.Join(", ", versionMatches.Select(p => p.FileName))}."));
            return new(
                PackageCompatibilityStatus.Ambiguous,
                versionMatches[0].FileName,
                versionMatches[0].Architecture,
                versionMatches[0].Version);
        }

        FirmwarePackageInfo match = versionMatches[0];
        findings.Add(new(
            AssessmentFindingSeverity.Info,
            "package.compatible",
            $"Compatible package '{match.FileName}' matches architecture '{match.Architecture}' and target {match.Version}."));
        return new(PackageCompatibilityStatus.Compatible, match.FileName, match.Architecture, match.Version);
    }

    private static RouterboardFirmwareStatus AssessRouterboard(
        MikrotikDeviceInfo deviceInfo,
        List<AssessmentFinding> findings)
    {
        bool anyPresent = !string.IsNullOrWhiteSpace(deviceInfo.RouterboardModel)
            || !string.IsNullOrWhiteSpace(deviceInfo.RouterboardFirmwareType)
            || !string.IsNullOrWhiteSpace(deviceInfo.RouterboardCurrentFirmware)
            || !string.IsNullOrWhiteSpace(deviceInfo.RouterboardUpgradeFirmware)
            || !string.IsNullOrWhiteSpace(deviceInfo.RouterboardFactoryFirmware);

        if (!anyPresent)
        {
            findings.Add(new(
                AssessmentFindingSeverity.Info,
                "routerboard.unavailable",
                "RouterBOARD firmware information is unavailable for this device."));
            return RouterboardFirmwareStatus.Unavailable;
        }

        bool hasCurrent = RouterOsVersion.TryParse(deviceInfo.RouterboardCurrentFirmware, out RouterOsVersion current);
        bool hasUpgrade = RouterOsVersion.TryParse(deviceInfo.RouterboardUpgradeFirmware, out RouterOsVersion upgrade);

        if (!hasCurrent && !hasUpgrade)
        {
            findings.Add(new(
                AssessmentFindingSeverity.Warning,
                "routerboard.inconclusive",
                "RouterBOARD firmware fields are present but version comparison is inconclusive."));
            return RouterboardFirmwareStatus.Inconclusive;
        }

        if (hasCurrent && hasUpgrade)
        {
            int cmp = current.CompareTo(upgrade);
            if (cmp == 0)
            {
                findings.Add(new(
                    AssessmentFindingSeverity.Info,
                    "routerboard.current",
                    "RouterBOARD current firmware matches upgrade firmware."));
                return RouterboardFirmwareStatus.Current;
            }

            if (cmp < 0)
            {
                findings.Add(new(
                    AssessmentFindingSeverity.Info,
                    "routerboard.upgrade_available",
                    $"RouterBOARD firmware upgrade appears available (current {current}, upgrade {upgrade}). Do not run /system/routerboard/upgrade in this milestone. Recommended future sequence: complete RouterOS package upgrade planning first, then RouterBOARD firmware, then reboot — actual execution is out of scope."));
                return RouterboardFirmwareStatus.UpgradeAvailable;
            }

            findings.Add(new(
                AssessmentFindingSeverity.Info,
                "routerboard.current_ahead",
                $"RouterBOARD current firmware ({current}) is newer than the reported upgrade firmware ({upgrade}). No RouterBOARD upgrade is indicated."));
            return RouterboardFirmwareStatus.Current;
        }

        findings.Add(new(
            AssessmentFindingSeverity.Warning,
            "routerboard.inconclusive",
            "Only one of RouterBOARD current/upgrade firmware versions could be parsed."));
        return RouterboardFirmwareStatus.Inconclusive;
    }

    private static DeviceAssessmentStatus DetermineOverall(
        RouterOsVersionClassification classification,
        UpgradePathStatus upgradePath,
        PackageCompatibilityStatus packageStatus,
        List<AssessmentFinding> findings)
    {
        bool hasBlocker = findings.Any(static f => f.Severity == AssessmentFindingSeverity.Blocker);

        return classification switch
        {
            RouterOsVersionClassification.RouterOsV7OrLater => DeviceAssessmentStatus.Unsupported,
            RouterOsVersionClassification.Unknown => DeviceAssessmentStatus.Blocked,
            RouterOsVersionClassification.AtTargetV6 => DeviceAssessmentStatus.AlreadyAtTarget,
            RouterOsVersionClassification.NewerThanTargetV6 => DeviceAssessmentStatus.ManualReviewRequired,
            // Older v6 + matching main package is ready for the automated 6.49.23 upgrade stage.
            RouterOsVersionClassification.OlderThanTargetV6 when packageStatus == PackageCompatibilityStatus.Compatible
                && upgradePath == UpgradePathStatus.EligibleSubjectToManualPathVerification
                && !hasBlocker
                => DeviceAssessmentStatus.ReadyForUpgrade,
            RouterOsVersionClassification.OlderThanTargetV6 when hasBlocker
                || packageStatus is PackageCompatibilityStatus.Missing
                    or PackageCompatibilityStatus.ArchitectureMismatch
                    or PackageCompatibilityStatus.VersionMismatch
                    or PackageCompatibilityStatus.Ambiguous
                    or PackageCompatibilityStatus.Unidentifiable
                => DeviceAssessmentStatus.Blocked,
            _ => DeviceAssessmentStatus.ManualReviewRequired,
        };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private readonly record struct PackageMatchResult(
        PackageCompatibilityStatus Status,
        string? FileName,
        string? Architecture,
        RouterOsVersion? Version);
}
