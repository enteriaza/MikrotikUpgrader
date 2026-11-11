using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

internal interface IPackageCompatibilityAuditor
{
    PackageCompatibilityDeviceReport AssessDevice(PackageCompatibilityAuditDevice device);

    IReadOnlyList<PackageCompatibilityDeviceReport> AssessDevices(
        IEnumerable<PackageCompatibilityAuditDevice> devices);
}

/// <summary>
/// Non-invasive package compatibility audit. Reuses <see cref="IUpgradePackageResolver"/>
/// and the raw RouterOS <c>bundle</c> field via <see cref="InstalledPackageInfo.IsBundledComponent"/>.
/// Does not upload packages, reboot, or mutate routers.
/// </summary>
internal sealed class PackageCompatibilityAuditor(
    IUpgradePackageResolver packageResolver,
    IFirmwarePackageCatalog packageCatalog,
    IRouterOsV7TargetProvider v7TargetProvider) : IPackageCompatibilityAuditor
{
    public IReadOnlyList<PackageCompatibilityDeviceReport> AssessDevices(
        IEnumerable<PackageCompatibilityAuditDevice> devices) =>
        devices.Select(AssessDevice).ToList();

    public PackageCompatibilityDeviceReport AssessDevice(PackageCompatibilityAuditDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        DeviceIdentityClassification classification = DeviceIdentityClassifier.Classify(device.RouterIdentity);
        List<PackageCompatibilityStageResult> stages = [];

        stages.Add(AssessV6Stepping(device, classification));
        stages.Add(AssessRouterOs7(device, classification));

        return new PackageCompatibilityDeviceReport
        {
            Device = device,
            Classification = classification,
            Stages = stages,
        };
    }

    private PackageCompatibilityStageResult AssessV6Stepping(
        PackageCompatibilityAuditDevice device,
        DeviceIdentityClassification classification)
    {
        if (!RouterOsVersion.TryParse(device.RouterOsVersionRaw, out RouterOsVersion installed))
        {
            return BaseResult(
                device,
                classification,
                PackageCompatibilityUpgradeStage.V6Stepping,
                RouterOsUpgradeVersions.V6SteppingVersionText,
                CatalogueCompatibilityStatus.InvalidInventory,
                "Installed RouterOS version could not be parsed; v6 stepping compatibility cannot be assessed.");
        }

        if (!installed.IsV6)
        {
            return BaseResult(
                device,
                classification,
                PackageCompatibilityUpgradeStage.V6Stepping,
                RouterOsUpgradeVersions.V6SteppingVersionText,
                CatalogueCompatibilityStatus.NotApplicable,
                $"Device runs RouterOS {installed}; v6 stepping to {RouterOsUpgradeVersions.V6SteppingVersionText} is not applicable.");
        }

        if (installed >= RouterOsUpgradeVersions.V6Stepping)
        {
            return BaseResult(
                device,
                classification,
                PackageCompatibilityUpgradeStage.V6Stepping,
                RouterOsUpgradeVersions.V6SteppingVersionText,
                CatalogueCompatibilityStatus.NotApplicable,
                $"Device already runs RouterOS {installed} (≥ {RouterOsUpgradeVersions.V6SteppingVersionText}); v6 stepping upgrade is skipped.");
        }

        return ResolveStage(
            device,
            classification,
            PackageCompatibilityUpgradeStage.V6Stepping,
            RouterOsUpgradeVersions.V6Stepping);
    }

    private PackageCompatibilityStageResult AssessRouterOs7(
        PackageCompatibilityAuditDevice device,
        DeviceIdentityClassification classification)
    {
        if (!classification.ShouldUpgradeToV7)
        {
            return BaseResult(
                device,
                classification,
                PackageCompatibilityUpgradeStage.RouterOs7,
                targetVersion: null,
                CatalogueCompatibilityStatus.NotApplicable,
                classification.PolicyReason);
        }

        RouterOsV7TargetSnapshot? snapshot = v7TargetProvider.CaptureSnapshot();
        if (snapshot is null)
        {
            return BaseResult(
                device,
                classification,
                PackageCompatibilityUpgradeStage.RouterOs7,
                targetVersion: null,
                CatalogueCompatibilityStatus.TargetUnavailable,
                $"No validated RouterOS 7 target is available from {v7TargetProvider.VersionFilePath}.");
        }

        if (!RouterOsVersion.TryParse(device.RouterOsVersionRaw, out RouterOsVersion installed))
        {
            return BaseResult(
                device,
                classification,
                PackageCompatibilityUpgradeStage.RouterOs7,
                snapshot.VersionText,
                CatalogueCompatibilityStatus.InvalidInventory,
                "Installed RouterOS version could not be parsed; RouterOS 7 compatibility cannot be assessed.");
        }

        if (installed.IsV7OrLater && installed >= snapshot.Version)
        {
            return BaseResult(
                device,
                classification,
                PackageCompatibilityUpgradeStage.RouterOs7,
                snapshot.VersionText,
                CatalogueCompatibilityStatus.NotApplicable,
                installed == snapshot.Version
                    ? $"Device already reports RouterOS 7 target {snapshot.VersionText}; no package upgrade is required."
                    : $"Device runs RouterOS {installed} which is newer than target {snapshot.VersionText}; automatic downgrade is not performed.");
        }

        return ResolveStage(
            device,
            classification,
            PackageCompatibilityUpgradeStage.RouterOs7,
            snapshot.Version);
    }

    private PackageCompatibilityStageResult ResolveStage(
        PackageCompatibilityAuditDevice device,
        DeviceIdentityClassification classification,
        PackageCompatibilityUpgradeStage stage,
        RouterOsVersion target)
    {
        PackageInventorySummary inventory = PackageInventoryReporting.Summarize(device.Packages);
        List<string> independentNames = inventory.IndependentPackages.Select(static p => p.Name).ToList();

        PackageCompatibilityStageResult? precheck = PrecheckArchitectureAndVersion(
            device,
            classification,
            stage,
            target,
            inventory,
            independentNames);
        if (precheck is not null)
        {
            return precheck;
        }

        UpgradePackageResolution resolution = packageResolver.Resolve(
            device.Architecture,
            device.Packages,
            target);

        if (resolution.Succeeded)
        {
            return new PackageCompatibilityStageResult
            {
                RouterIp = device.RouterIp,
                RouterIdentity = device.RouterIdentity,
                InstalledRouterOsVersion = device.RouterOsVersionRaw,
                Architecture = device.Architecture,
                Stage = stage,
                TargetRouterOsVersion = target.ToString(),
                Status = CatalogueCompatibilityStatus.Compatible,
                InventorySource = device.InventorySource,
                FixtureName = device.FixtureName,
                IndependentPackageNames = independentNames,
                BundledComponentCount = inventory.BundledCount,
                ResolvedPackageFileNames = resolution.Packages
                    .Select(static p => p.CatalogPackage.FileName)
                    .ToList(),
                Explanation =
                    $"Catalogue can satisfy {resolution.Packages.Count} independently installed package(s) for target {target}.",
                IsEligibleForV7Policy = classification.ShouldUpgradeToV7,
                DeviceType = classification.DeviceType,
            };
        }

        return MapFailure(
            device,
            classification,
            stage,
            target,
            inventory,
            independentNames,
            resolution);
    }

    private PackageCompatibilityStageResult? PrecheckArchitectureAndVersion(
        PackageCompatibilityAuditDevice device,
        DeviceIdentityClassification classification,
        PackageCompatibilityUpgradeStage stage,
        RouterOsVersion target,
        PackageInventorySummary inventory,
        IReadOnlyList<string> independentNames)
    {
        if (string.IsNullOrWhiteSpace(device.Architecture))
        {
            return BaseResult(
                device,
                classification,
                stage,
                target.ToString(),
                CatalogueCompatibilityStatus.InvalidInventory,
                "Device architecture is missing.",
                independentNames,
                inventory.BundledCount);
        }

        if (device.Packages.Count == 0)
        {
            return BaseResult(
                device,
                classification,
                stage,
                target.ToString(),
                CatalogueCompatibilityStatus.InvalidInventory,
                "Installed package inventory is empty.",
                independentNames,
                inventory.BundledCount);
        }

        string normalizedArch = FirmwarePackageFileNameParser.NormalizeArchitecture(device.Architecture);
        IReadOnlyList<FirmwarePackageInfo> all = packageCatalog.GetPackages();
        List<FirmwarePackageInfo> mainAtAnyVersion = all
            .Where(p => p.PackageName.Equals("routeros", StringComparison.Ordinal)
                && UpgradePackageResolver.ArchitectureAcceptable(normalizedArch, p.Architecture))
            .ToList();

        List<FirmwarePackageInfo> mainAtTarget = mainAtAnyVersion
            .Where(p => p.Version == target)
            .ToList();

        if (mainAtAnyVersion.Count > 0 && mainAtTarget.Count == 0)
        {
            string observed = string.Join(
                ", ",
                mainAtAnyVersion.Select(static p => p.Version.ToString()).Distinct().Order());
            return new PackageCompatibilityStageResult
            {
                RouterIp = device.RouterIp,
                RouterIdentity = device.RouterIdentity,
                InstalledRouterOsVersion = device.RouterOsVersionRaw,
                Architecture = device.Architecture,
                Stage = stage,
                TargetRouterOsVersion = target.ToString(),
                Status = CatalogueCompatibilityStatus.VersionMismatch,
                InventorySource = device.InventorySource,
                FixtureName = device.FixtureName,
                IndependentPackageNames = independentNames,
                BundledComponentCount = inventory.BundledCount,
                MissingPackageNames = ["routeros"],
                Explanation =
                    $"Architecture '{normalizedArch}' has RouterOS main packages, but none match target {target}. Observed versions: {observed}.",
                IsEligibleForV7Policy = classification.ShouldUpgradeToV7,
                DeviceType = classification.DeviceType,
            };
        }

        List<FirmwarePackageInfo> anyMainAtTarget = all
            .Where(p => p.PackageName.Equals("routeros", StringComparison.Ordinal) && p.Version == target)
            .ToList();

        if (anyMainAtTarget.Count > 0 && mainAtTarget.Count == 0)
        {
            return new PackageCompatibilityStageResult
            {
                RouterIp = device.RouterIp,
                RouterIdentity = device.RouterIdentity,
                InstalledRouterOsVersion = device.RouterOsVersionRaw,
                Architecture = device.Architecture,
                Stage = stage,
                TargetRouterOsVersion = target.ToString(),
                Status = CatalogueCompatibilityStatus.ArchitectureMismatch,
                InventorySource = device.InventorySource,
                FixtureName = device.FixtureName,
                IndependentPackageNames = independentNames,
                BundledComponentCount = inventory.BundledCount,
                MissingPackageNames = ["routeros"],
                Explanation =
                    $"Target {target} main packages exist, but none match architecture '{normalizedArch}'.",
                IsEligibleForV7Policy = classification.ShouldUpgradeToV7,
                DeviceType = classification.DeviceType,
            };
        }

        return null;
    }

    private static PackageCompatibilityStageResult MapFailure(
        PackageCompatibilityAuditDevice device,
        DeviceIdentityClassification classification,
        PackageCompatibilityUpgradeStage stage,
        RouterOsVersion target,
        PackageInventorySummary inventory,
        IReadOnlyList<string> independentNames,
        UpgradePackageResolution resolution)
    {
        CatalogueCompatibilityStatus status = resolution.FailureKind switch
        {
            PackageResolutionFailureKind.ArchitectureMissing
                or PackageResolutionFailureKind.InventoryEmpty
                or PackageResolutionFailureKind.NoIndependentPackages
                or PackageResolutionFailureKind.MultipleMainPackages =>
                    CatalogueCompatibilityStatus.InvalidInventory,
            PackageResolutionFailureKind.NoPackagesForTarget
                or PackageResolutionFailureKind.MissingMainPackage =>
                    CatalogueCompatibilityStatus.MissingMainPackage,
            PackageResolutionFailureKind.AmbiguousPackage =>
                CatalogueCompatibilityStatus.AmbiguousPackage,
            PackageResolutionFailureKind.MissingPackage when IsMainFailure(resolution.FailedInstalledPackageName) =>
                CatalogueCompatibilityStatus.MissingMainPackage,
            PackageResolutionFailureKind.MissingPackage =>
                CatalogueCompatibilityStatus.MissingOptionalPackage,
            _ => CatalogueCompatibilityStatus.InvalidInventory,
        };

        if (resolution.FailureKind == PackageResolutionFailureKind.MultipleMainPackages)
        {
            status = CatalogueCompatibilityStatus.AmbiguousPackage;
        }

        List<string> missing = [];
        if (resolution.FailedInstalledPackageName is not null
            && status is CatalogueCompatibilityStatus.MissingMainPackage
                or CatalogueCompatibilityStatus.MissingOptionalPackage)
        {
            missing.Add(resolution.FailedInstalledPackageName);
        }
        else if (status == CatalogueCompatibilityStatus.MissingMainPackage)
        {
            missing.Add("routeros");
        }

        return new PackageCompatibilityStageResult
        {
            RouterIp = device.RouterIp,
            RouterIdentity = device.RouterIdentity,
            InstalledRouterOsVersion = device.RouterOsVersionRaw,
            Architecture = device.Architecture,
            Stage = stage,
            TargetRouterOsVersion = target.ToString(),
            Status = status,
            InventorySource = device.InventorySource,
            FixtureName = device.FixtureName,
            IndependentPackageNames = independentNames,
            BundledComponentCount = inventory.BundledCount,
            MissingPackageNames = missing,
            AmbiguousPackageCandidates = resolution.AmbiguousCandidates,
            Explanation = resolution.FailureReason
                ?? $"Package resolution failed for target {target}.",
            IsEligibleForV7Policy = classification.ShouldUpgradeToV7,
            DeviceType = classification.DeviceType,
        };
    }

    private static bool IsMainFailure(string? installedName) =>
        !string.IsNullOrWhiteSpace(installedName)
        && UpgradePackageResolver.IsMainRouterOsPackage(installedName);

    private static PackageCompatibilityStageResult BaseResult(
        PackageCompatibilityAuditDevice device,
        DeviceIdentityClassification classification,
        PackageCompatibilityUpgradeStage stage,
        string? targetVersion,
        CatalogueCompatibilityStatus status,
        string explanation,
        IReadOnlyList<string>? independentNames = null,
        int? bundledCount = null)
    {
        PackageInventorySummary inventory = PackageInventoryReporting.Summarize(device.Packages);
        return new PackageCompatibilityStageResult
        {
            RouterIp = device.RouterIp,
            RouterIdentity = device.RouterIdentity,
            InstalledRouterOsVersion = device.RouterOsVersionRaw,
            Architecture = device.Architecture,
            Stage = stage,
            TargetRouterOsVersion = targetVersion,
            Status = status,
            InventorySource = device.InventorySource,
            FixtureName = device.FixtureName,
            IndependentPackageNames = independentNames
                ?? inventory.IndependentPackages.Select(static p => p.Name).ToList(),
            BundledComponentCount = bundledCount ?? inventory.BundledCount,
            Explanation = explanation,
            IsEligibleForV7Policy = classification.ShouldUpgradeToV7,
            DeviceType = classification.DeviceType,
        };
    }
}
