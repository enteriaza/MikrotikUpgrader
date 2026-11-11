using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

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
    IRouterOsV7TargetProvider v7TargetProvider) : IDeviceCompatibilityAssessor
{
    public DeviceCompatibilityAssessment Assess(
        string routerIp,
        MikrotikDeviceInfo deviceInfo,
        DateTimeOffset assessedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routerIp);
        ArgumentNullException.ThrowIfNull(deviceInfo);

        RouterOsVersion stepping = RouterOsUpgradeVersions.V6Stepping;
        RouterOsV7TargetSnapshot? v7Snapshot = v7TargetProvider.TryGetCurrent();
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
            classification = ClassifyAgainstStepping(parsedDevice, stepping);
        }

        UpgradePathStatus upgradePath = AssessUpgradePath(classification, deviceVersion, v7Snapshot, findings);
        RouterOsVersion packageTarget = ResolvePackageTarget(classification, deviceVersion, stepping, v7Snapshot);
        PackageMatchResult package = MatchPackage(deviceInfo.ArchitectureName, packageTarget, findings);
        RouterboardFirmwareStatus routerboard = AssessRouterboard(deviceInfo, findings);

        DeviceAssessmentStatus overall = DetermineOverall(
            classification,
            upgradePath,
            package.Status,
            v7Snapshot,
            findings);

        string reportedTarget = v7Snapshot?.VersionText
            ?? (packageTarget.IsV6 ? packageTarget.ToString() : RouterOsUpgradeVersions.V6SteppingVersionText);

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
            TargetRouterOsVersion = reportedTarget,
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

    private static RouterOsVersionClassification ClassifyAgainstStepping(
        RouterOsVersion device,
        RouterOsVersion stepping)
    {
        if (device.IsV7OrLater)
        {
            return RouterOsVersionClassification.RouterOsV7OrLater;
        }

        if (!device.IsV6)
        {
            return RouterOsVersionClassification.Unknown;
        }

        int cmp = device.CompareTo(stepping);
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
        RouterOsVersion? deviceVersion,
        RouterOsV7TargetSnapshot? v7Snapshot,
        List<AssessmentFinding> findings)
    {
        switch (classification)
        {
            case RouterOsVersionClassification.OlderThanTargetV6:
                findings.Add(new(
                    AssessmentFindingSeverity.Info,
                    "path.eligible_v6_stepping_then_v7",
                    $"Device reports RouterOS v6 older than fixed stepping {RouterOsUpgradeVersions.V6SteppingVersionText}; stepping upgrade then RouterOS 7 is required when eligible."));
                return UpgradePathStatus.EligibleSubjectToManualPathVerification;

            case RouterOsVersionClassification.AtTargetV6:
            case RouterOsVersionClassification.NewerThanTargetV6:
                findings.Add(new(
                    AssessmentFindingSeverity.Info,
                    "path.skip_v6_stepping",
                    $"Device already runs RouterOS {deviceVersion} (≥ {RouterOsUpgradeVersions.V6SteppingVersionText}); v6 stepping is skipped and the device proceeds directly to RouterOS 7 when eligible."));
                return UpgradePathStatus.SkipV6SteppingProceedToV7;

            case RouterOsVersionClassification.RouterOsV7OrLater:
                if (v7Snapshot is null)
                {
                    findings.Add(new(
                        AssessmentFindingSeverity.Blocker,
                        "path.v7_target_unavailable",
                        "Device runs RouterOS 7 but no validated FirmwareFiles/mikrotik/version target is available."));
                    return UpgradePathStatus.Blocked;
                }

                int cmp = deviceVersion!.Value.CompareTo(v7Snapshot.Version);
                if (cmp < 0)
                {
                    findings.Add(new(
                        AssessmentFindingSeverity.Info,
                        "path.eligible_direct_v7",
                        $"Device runs RouterOS {deviceVersion} which is older than target {v7Snapshot.VersionText}; direct RouterOS 7 upgrade is eligible."));
                    return UpgradePathStatus.EligibleDirectV7Upgrade;
                }

                if (cmp == 0)
                {
                    findings.Add(new(
                        AssessmentFindingSeverity.Info,
                        "path.already_at_v7_target",
                        $"Device already reports RouterOS 7 target {v7Snapshot.VersionText}."));
                    return UpgradePathStatus.AlreadyAtV7Target;
                }

                findings.Add(new(
                    AssessmentFindingSeverity.Info,
                    "path.newer_than_v7_target",
                    $"Device runs RouterOS {deviceVersion} which is newer than target {v7Snapshot.VersionText}; automatic downgrade is not performed."));
                return UpgradePathStatus.NewerThanV7Target;

            default:
                findings.Add(new(
                    AssessmentFindingSeverity.Blocker,
                    "path.unknown",
                    "Upgrade path cannot be assessed because the RouterOS version is unknown or unparseable."));
                return UpgradePathStatus.UnknownRequiresManualReview;
        }
    }

    private static RouterOsVersion ResolvePackageTarget(
        RouterOsVersionClassification classification,
        RouterOsVersion? deviceVersion,
        RouterOsVersion stepping,
        RouterOsV7TargetSnapshot? v7Snapshot)
    {
        if (classification == RouterOsVersionClassification.OlderThanTargetV6)
        {
            return stepping;
        }

        if (v7Snapshot is not null)
        {
            return v7Snapshot.Version;
        }

        // No v7 target yet — keep assessing against stepping packages for diagnostics.
        return stepping;
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

        List<FirmwarePackageInfo> policyPackages = all
            .Where(p => p.PackageName.Equals("routeros", StringComparison.Ordinal)
                && p.Version.Major == target.Major)
            .ToList();

        if (policyPackages.Count == 0)
        {
            findings.Add(new(
                AssessmentFindingSeverity.Blocker,
                "package.catalog_empty",
                $"No RouterOS v{target.Major} packages were found under FirmwareFiles for target {target}."));
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
                    $"RouterBOARD firmware upgrade appears available (current {current}, upgrade {upgrade}). Do not run /system/routerboard/upgrade."));
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
        RouterOsV7TargetSnapshot? v7Snapshot,
        List<AssessmentFinding> findings)
    {
        bool hasBlocker = findings.Any(static f => f.Severity == AssessmentFindingSeverity.Blocker);

        if (classification == RouterOsVersionClassification.Unknown)
        {
            return DeviceAssessmentStatus.Blocked;
        }

        if (upgradePath is UpgradePathStatus.AlreadyAtV7Target or UpgradePathStatus.NewerThanV7Target)
        {
            return DeviceAssessmentStatus.AlreadyAtTarget;
        }

        if (upgradePath == UpgradePathStatus.Blocked || hasBlocker)
        {
            return DeviceAssessmentStatus.Blocked;
        }

        bool needsPackages = upgradePath is UpgradePathStatus.EligibleSubjectToManualPathVerification
            or UpgradePathStatus.SkipV6SteppingProceedToV7
            or UpgradePathStatus.EligibleDirectV7Upgrade;

        if (needsPackages)
        {
            if (upgradePath is UpgradePathStatus.SkipV6SteppingProceedToV7 or UpgradePathStatus.EligibleDirectV7Upgrade
                && v7Snapshot is null)
            {
                return DeviceAssessmentStatus.Blocked;
            }

            if (packageStatus == PackageCompatibilityStatus.Compatible)
            {
                return DeviceAssessmentStatus.ReadyForUpgrade;
            }

            if (packageStatus is PackageCompatibilityStatus.Missing
                or PackageCompatibilityStatus.ArchitectureMismatch
                or PackageCompatibilityStatus.VersionMismatch
                or PackageCompatibilityStatus.Ambiguous
                or PackageCompatibilityStatus.Unidentifiable)
            {
                return DeviceAssessmentStatus.Blocked;
            }
        }

        return DeviceAssessmentStatus.Blocked;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private readonly record struct PackageMatchResult(
        PackageCompatibilityStatus Status,
        string? FileName,
        string? Architecture,
        RouterOsVersion? Version);
}
