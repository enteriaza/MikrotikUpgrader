using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;

namespace MikrotikWorker.Upgrade;

internal interface IRouterOsV6UpgradeExecutor
{
    Task<UpgradeExecutionResult> ExecuteAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        DeviceCompatibilityAssessment assessment,
        string username,
        string password,
        Func<bool> isWithinScheduleWindow,
        CancellationToken cancellationToken);
}

internal sealed class RouterOsV6UpgradeExecutor(
    IMikrotikRouterOperations routerOperations,
    IMikrotikConnectionFactory connectionFactory,
    IUpgradePackageResolver packageResolver,
    IDeviceBackupService backupService,
    IRouterOsV7TargetProvider v7TargetProvider,
    LegacyFleetUpgradeAutomationCleanup legacyFleetUpgradeAutomationCleanup,
    RouterboardFirmwareUpgradeService routerboardFirmwareUpgradeService,
    IOptions<FirmwareUpgradeOptions> upgradeOptions,
    IOptions<MikroTikOptions> mikroTikOptions,
    TimeProvider timeProvider,
    ILogger<RouterOsV6UpgradeExecutor> logger) : IRouterOsV6UpgradeExecutor
{
    public async Task<UpgradeExecutionResult> ExecuteAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        DeviceCompatibilityAssessment assessment,
        string username,
        string password,
        Func<bool> isWithinScheduleWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(isWithinScheduleWindow);

        FirmwareUpgradeOptions options = upgradeOptions.Value;
        RouterOsVersion stepping = RouterOsUpgradeVersions.V6Stepping;
        UpgradeExecutionMode executionMode = job.ExecutionMode;

        if (!options.Enabled)
        {
            logger.LogInformation("Upgrade skipped because FirmwareUpgrade:Enabled is false");
            return WithMode(
                new UpgradeExecutionResult { Outcome = UpgradeOutcome.SkippedUpgradeDisabled },
                executionMode);
        }

        if (!RouterOsVersion.TryParse(deviceInfo.Version, out RouterOsVersion currentVersion))
        {
            return PolicyBlock($"RouterOS version '{deviceInfo.Version}' could not be parsed.", executionMode);
        }

        if (assessment.OverallStatus == DeviceAssessmentStatus.Blocked)
        {
            return PolicyBlock(
                $"Device assessment status '{assessment.OverallStatus}' is not eligible for automated upgrade.",
                executionMode);
        }

        if (!isWithinScheduleWindow())
        {
            return PolicyBlock("Job is outside its scheduled execution window.", executionMode);
        }

        logger.LogInformation("Upgrade execution mode: {ExecutionMode}", executionMode);
        using (DeviceLogging.BeginServiceOnly())
        {
            logger.LogInformation(
                "Upgrade execution mode correlation. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RouterName={RouterName}, ExecutionMode={ExecutionMode}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                job.RouterName,
                executionMode);
        }

        logger.LogInformation(
            "Initial RouterOS version: {RouterOsVersion}",
            deviceInfo.Version);

        // Connection-time safety: remove legacy router-side FleetUpgrade automation before any
        // firmware mutation (applies to FullUpgrade and V6SteppingOnly, including ACE/BR).
        LegacyFleetUpgradeCleanupResult legacyCleanup = await legacyFleetUpgradeAutomationCleanup
            .CleanupAsync(connection, job, cancellationToken)
            .ConfigureAwait(false);
        if (!legacyCleanup.Succeeded)
        {
            return WithMode(
                new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.LegacyFirmwareAutomationCleanupFailed,
                    Detail = legacyCleanup.FailureReason,
                    PostUpgradeVersion = deviceInfo.Version,
                    PostUpgradeArchitecture = deviceInfo.ArchitectureName,
                    CompletedStage = UpgradeCompletedStages.None,
                },
                executionMode);
        }

        bool needsV6Stepping = currentVersion.IsV6 && currentVersion < stepping;
        logger.LogInformation(
            needsV6Stepping
                ? "v6 stepping required: {Current} < {Stepping}"
                : "v6 stepping unnecessary: {Current} is not an older RouterOS 6 release than {Stepping}",
            currentVersion,
            stepping);

        ITikConnection? live = null;
        bool disposeLive = false;
        bool rebootInitiated = false;
        bool v6SteppingVerified = false;
        try
        {
            MikrotikDeviceInfo workingDevice = deviceInfo;
            IReadOnlyList<InstalledPackageInfo>? workingPackages = null;
            live = connection;

            if (needsV6Stepping)
            {
                logger.LogInformation(
                    "v6 stepping upgrade starting (RouterOS {RouterOsVersion} → {Stepping})",
                    deviceInfo.Version,
                    stepping);

                StageExecutionResult stage1 = await ExecuteStageAsync(
                        connection,
                        job,
                        deviceInfo,
                        stepping,
                        username,
                        password,
                        isWithinScheduleWindow,
                        stageLabel: "v6-stepping",
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!IsStageSuccess(stage1.Execution.Outcome))
                {
                    logger.LogWarning(
                        "v6 stepping failed or did not verify (Outcome={Outcome}); CompletedStage remains {CompletedStage}",
                        stage1.Execution.Outcome,
                        UpgradeCompletedStages.None);
                    return WithMode(
                        stage1.Execution,
                        executionMode,
                        completedStage: UpgradeCompletedStages.None,
                        postArchitecture: stage1.PostDeviceInfo?.ArchitectureName ?? deviceInfo.ArchitectureName);
                }

                rebootInitiated = stage1.Execution.RebootInitiated;
                workingDevice = stage1.PostDeviceInfo
                    ?? throw new InvalidOperationException("v6 stepping verification did not produce device info.");
                workingPackages = stage1.PostPackages
                    ?? throw new InvalidOperationException("v6 stepping verification did not produce package inventory.");
                live = stage1.LiveConnection
                    ?? throw new InvalidOperationException("v6 stepping verification did not leave a live connection.");
                disposeLive = true;
                v6SteppingVerified = true;

                logger.LogInformation(
                    "v6 stepping verification succeeded (observed {ObservedVersion})",
                    stage1.Execution.PostUpgradeVersion);
            }
            else if (currentVersion.IsV6)
            {
                logger.LogInformation(
                    "Skipping v6 stepping because device already runs RouterOS {RouterOsVersion} (≥ {Stepping})",
                    currentVersion,
                    stepping);
                workingPackages = await routerOperations
                    .ListInstalledPackagesAsync(live, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (currentVersion.IsV7OrLater)
            {
                workingPackages = await routerOperations
                    .ListInstalledPackagesAsync(live, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                return PolicyBlock($"Unsupported RouterOS major version '{currentVersion}'.", executionMode);
            }

            DeviceIdentityClassification classification = await ClassifyIdentityAsync(
                    live,
                    job,
                    workingDevice,
                    cancellationToken)
                .ConfigureAwait(false);
            LogClassification(classification);

            string completedAfterV6 = v6SteppingVerified
                ? UpgradeCompletedStages.V6Stepping
                : UpgradeCompletedStages.None;

            // Stage cap is independent of identity policy and must be checked in the executor
            // before any second-stage (RouterOS 7) activity — including v7 target snapshot capture.
            if (executionMode == UpgradeExecutionMode.V6SteppingOnly)
            {
                return CompleteV6SteppingOnly(
                    job,
                    workingDevice,
                    classification,
                    rebootInitiated,
                    v6SteppingVerified);
            }

            if (!classification.ShouldUpgradeToV7)
            {
                logger.LogInformation(
                    "RouterOS 7 upgrade skipped intentionally. DeviceType={DeviceType}, Reason={Reason}",
                    classification.DeviceType,
                    classification.PolicyReason);
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeSucceededV6Only,
                    Detail = classification.PolicyReason,
                    PostUpgradeVersion = workingDevice.Version,
                    PostUpgradeArchitecture = workingDevice.ArchitectureName,
                    RebootInitiated = rebootInitiated,
                    ExecutionMode = executionMode,
                    CompletedStage = completedAfterV6,
                };
            }

            // Immutable for this job — capture only when the RouterOS 7 stage may run.
            RouterOsV7TargetSnapshot? v7Snapshot = v7TargetProvider.CaptureSnapshot();
            if (v7Snapshot is not null)
            {
                logger.LogInformation(
                    "Validated RouterOS 7 target snapshot for job: {Target} (captured {CapturedAt:o})",
                    v7Snapshot.VersionText,
                    v7Snapshot.CapturedAtUtc);
            }
            else
            {
                logger.LogWarning(
                    "No validated RouterOS 7 target is available from {Path}",
                    v7TargetProvider.VersionFilePath);
            }

            if (v7Snapshot is null)
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.TargetUnavailable,
                    Detail = $"RouterOS 7 target file is unavailable or invalid ({v7TargetProvider.VersionFilePath}).",
                    PostUpgradeVersion = workingDevice.Version,
                    PostUpgradeArchitecture = workingDevice.ArchitectureName,
                    RebootInitiated = rebootInitiated,
                    ExecutionMode = executionMode,
                    CompletedStage = completedAfterV6,
                };
            }

            if (!RouterOsVersion.TryParse(workingDevice.Version, out RouterOsVersion workingVersion))
            {
                return PolicyBlock(
                    $"Post-step RouterOS version '{workingDevice.Version}' could not be parsed.",
                    executionMode);
            }

            int cmpToV7 = workingVersion.CompareTo(v7Snapshot.Version);
            if (cmpToV7 == 0)
            {
                logger.LogInformation(
                    "Device already at RouterOS 7 target {Target}; no upgrade required",
                    v7Snapshot.VersionText);
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.SkippedAlreadyAtTarget,
                    PostUpgradeVersion = workingDevice.Version,
                    PostUpgradeArchitecture = workingDevice.ArchitectureName,
                    RebootInitiated = rebootInitiated,
                    ExecutionMode = executionMode,
                    // Already at v7 target — no stage was performed this job; do not claim routeros7 completion.
                    CompletedStage = UpgradeCompletedStages.None,
                };
            }

            if (cmpToV7 > 0)
            {
                logger.LogInformation(
                    "Device RouterOS {Current} is newer than target {Target}; downgrade is not performed",
                    workingVersion,
                    v7Snapshot.VersionText);
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.SkippedNewerThanTarget,
                    Detail = $"Current RouterOS {workingVersion} is newer than target {v7Snapshot.VersionText}; automatic downgrade is not performed.",
                    PostUpgradeVersion = workingDevice.Version,
                    PostUpgradeArchitecture = workingDevice.ArchitectureName,
                    RebootInitiated = rebootInitiated,
                    ExecutionMode = executionMode,
                    CompletedStage = UpgradeCompletedStages.None,
                };
            }

            // Never install v6 packages on a RouterOS 7 device; never mix majors in one stage.
            if (workingVersion.IsV7OrLater && v7Snapshot.Version.IsV7OrLater)
            {
                logger.LogInformation(
                    "Direct RouterOS 7 upgrade starting (RouterOS {Current} → {Target}). DeviceType={DeviceType}",
                    workingVersion,
                    v7Snapshot.VersionText,
                    classification.DeviceType);
            }
            else
            {
                logger.LogInformation(
                    "RouterOS 7 transition starting (RouterOS {Current} → {Target}). DeviceType={DeviceType}",
                    workingVersion,
                    v7Snapshot.VersionText,
                    classification.DeviceType);
            }

            StageExecutionResult stage2 = await ExecuteStageAsync(
                    live,
                    job,
                    workingDevice,
                    v7Snapshot.Version,
                    username,
                    password,
                    isWithinScheduleWindow,
                    stageLabel: "routeros7",
                    cancellationToken,
                    installedOverride: workingPackages)
                .ConfigureAwait(false);

            if (disposeLive)
            {
                try
                {
                    live.Dispose();
                }
                catch
                {
                }

                disposeLive = false;
                live = null;
            }

            if (stage2.Execution.Outcome == UpgradeOutcome.UpgradeSucceeded)
            {
                logger.LogInformation(
                    "Final RouterOS 7 verification succeeded (target {Target}, observed {ObservedVersion})",
                    v7Snapshot.VersionText,
                    stage2.Execution.PostUpgradeVersion);
            }

            bool stage2Verified = stage2.Execution.Outcome == UpgradeOutcome.UpgradeSucceeded;
            if (!stage2Verified)
            {
                stage2.LiveConnection?.Dispose();
                return WithMode(
                    new UpgradeExecutionResult
                    {
                        Outcome = stage2.Execution.Outcome,
                        Detail = stage2.Execution.Detail,
                        PostUpgradeVersion = stage2.Execution.PostUpgradeVersion ?? workingDevice.Version,
                        PostUpgradeArchitecture =
                            stage2.PostDeviceInfo?.ArchitectureName ?? workingDevice.ArchitectureName,
                        RebootInitiated = rebootInitiated || stage2.Execution.RebootInitiated,
                    },
                    executionMode,
                    completedStage: completedAfterV6);
            }

            // Post-v7: RouterBOARD assessment/upgrade before FullUpgrade success.
            ITikConnection? postV7 = stage2.LiveConnection;
            bool disposePostV7 = postV7 is not null;
            try
            {
                if (postV7 is null)
                {
                    MikrotikConnectionOptions reconnectOptions = new()
                    {
                        Host = job.RouterIp.ToString(),
                        Username = username,
                        Password = password,
                        Port = mikroTikOptions.Value.Port,
                    };
                    postV7 = await connectionFactory
                        .CreateAsync(reconnectOptions, cancellationToken)
                        .ConfigureAwait(false);
                    disposePostV7 = true;
                }

                MikrotikDeviceInfo postV7Device = stage2.PostDeviceInfo
                    ?? await routerOperations
                        .ReadDeviceInfoAsync(postV7, job.RouterIp.ToString(), cancellationToken)
                        .ConfigureAwait(false);

                logger.LogInformation("Post-RouterOS-7 backup before RouterBOARD assessment starting");
                DeviceBackupResult postV7Backup = await backupService
                    .CreatePreUpgradeBackupAsync(postV7, job, postV7Device, username, password, cancellationToken)
                    .ConfigureAwait(false);
                if (!postV7Backup.Succeeded)
                {
                    return WithMode(
                        new UpgradeExecutionResult
                        {
                            Outcome = UpgradeOutcome.BackupFailed,
                            Detail = postV7Backup.FailureReason ?? "Post-RouterOS-7 backup before RouterBOARD assessment failed.",
                            PostUpgradeVersion = postV7Device.Version,
                            PostUpgradeArchitecture = postV7Device.ArchitectureName,
                            RebootInitiated = rebootInitiated || stage2.Execution.RebootInitiated,
                        },
                        executionMode,
                        completedStage: UpgradeCompletedStages.RouterOs7);
                }

                MikrotikDeviceInfo beforeRb = await routerOperations
                    .ReadDeviceInfoAsync(postV7, job.RouterIp.ToString(), cancellationToken)
                    .ConfigureAwait(false);

                UpgradeExecutionResult rbResult = await routerboardFirmwareUpgradeService
                    .ExecuteAsync(
                        postV7,
                        job,
                        beforeRb,
                        username,
                        password,
                        priorRebootInitiated: rebootInitiated || stage2.Execution.RebootInitiated,
                        completedStageBefore: UpgradeCompletedStages.RouterOs7,
                        cancellationToken)
                    .ConfigureAwait(false);

                return WithMode(rbResult, executionMode);
            }
            finally
            {
                if (disposePostV7 && postV7 is not null)
                {
                    try
                    {
                        postV7.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }
        finally
        {
            if (disposeLive && live is not null && !ReferenceEquals(live, connection))
            {
                try
                {
                    live.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Structural v7 boundary for <see cref="UpgradeExecutionMode.V6SteppingOnly"/>.
    /// Must return before second-stage backup, v7 package resolution, fetch, or reboot.
    /// </summary>
    private UpgradeExecutionResult CompleteV6SteppingOnly(
        MikrotikScheduledJob job,
        MikrotikDeviceInfo workingDevice,
        DeviceIdentityClassification classification,
        bool rebootInitiated,
        bool v6SteppingVerified)
    {
        if (!RouterOsVersion.TryParse(workingDevice.Version, out RouterOsVersion workingVersion))
        {
            return PolicyBlock(
                $"Post-step RouterOS version '{workingDevice.Version}' could not be parsed.",
                UpgradeExecutionMode.V6SteppingOnly);
        }

        if (workingVersion.IsV7OrLater)
        {
            logger.LogInformation(
                "UpgradeStageCapReached: V6SteppingOnly — v6 stage not applicable to RouterOS 7 device; no upgrade or downgrade. Identity={Identity}, Version={Version}",
                string.IsNullOrWhiteSpace(classification.Identity) ? "(unavailable)" : classification.Identity,
                workingDevice.Version);
            using (DeviceLogging.BeginServiceOnly())
            {
                logger.LogInformation(
                    "UpgradeStageCapReached correlation. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, ExecutionMode={ExecutionMode}, Version={Version}",
                    job.ImportId,
                    job.JobId,
                    job.RouterIp,
                    UpgradeExecutionMode.V6SteppingOnly,
                    workingDevice.Version);
            }

            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.SkippedStageNotApplicable,
                Detail =
                    "Execution mode V6SteppingOnly is not applicable to RouterOS 7 devices; no upgrade or downgrade was performed.",
                PostUpgradeVersion = workingDevice.Version,
                PostUpgradeArchitecture = workingDevice.ArchitectureName,
                RebootInitiated = rebootInitiated,
                ExecutionMode = UpgradeExecutionMode.V6SteppingOnly,
                CompletedStage = UpgradeCompletedStages.None,
            };
        }

        string completedStage = v6SteppingVerified
            ? UpgradeCompletedStages.V6Stepping
            : UpgradeCompletedStages.None;

        if (v6SteppingVerified)
        {
            logger.LogInformation(
                "UpgradeStageCapReached: V6SteppingOnly after verified v6 stepping. Identity={Identity}, DeviceType={DeviceType}, Version={Version}. RouterOS 7 stage will not run.",
                string.IsNullOrWhiteSpace(classification.Identity) ? "(unavailable)" : classification.Identity,
                classification.DeviceType,
                workingDevice.Version);
        }
        else
        {
            logger.LogInformation(
                "UpgradeStageCapReached: V6SteppingOnly with v6 stepping unnecessary (already at/above stepping release). Identity={Identity}, DeviceType={DeviceType}, Version={Version}. RouterOS 7 stage will not run.",
                string.IsNullOrWhiteSpace(classification.Identity) ? "(unavailable)" : classification.Identity,
                classification.DeviceType,
                workingDevice.Version);
        }

        using (DeviceLogging.BeginServiceOnly())
        {
            logger.LogInformation(
                "UpgradeStageCapReached correlation. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, ExecutionMode={ExecutionMode}, Version={Version}, CompletedStage={CompletedStage}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                UpgradeExecutionMode.V6SteppingOnly,
                workingDevice.Version,
                completedStage);
        }

        return new UpgradeExecutionResult
        {
            Outcome = UpgradeOutcome.UpgradeSucceededV6Only,
            Detail = v6SteppingVerified
                ? "Execution mode V6SteppingOnly: v6 stepping verified. RouterOS 7 was intentionally not attempted."
                : "Execution mode V6SteppingOnly: device already at or above the v6 stepping release; no v6 upgrade performed. RouterOS 7 was intentionally not attempted.",
            PostUpgradeVersion = workingDevice.Version,
            PostUpgradeArchitecture = workingDevice.ArchitectureName,
            RebootInitiated = rebootInitiated,
            ExecutionMode = UpgradeExecutionMode.V6SteppingOnly,
            CompletedStage = completedStage,
        };
    }

    private static UpgradeExecutionResult WithMode(
        UpgradeExecutionResult result,
        UpgradeExecutionMode executionMode,
        string? completedStage = null,
        string? postArchitecture = null) =>
        new()
        {
            Outcome = result.Outcome,
            Detail = result.Detail,
            PostUpgradeVersion = result.PostUpgradeVersion,
            PostUpgradeArchitecture = postArchitecture ?? result.PostUpgradeArchitecture,
            RebootInitiated = result.RebootInitiated,
            ExecutionMode = executionMode,
            CompletedStage = completedStage ?? result.CompletedStage,
        };

    private async Task<DeviceIdentityClassification> ClassifyIdentityAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo fallbackDevice,
        CancellationToken cancellationToken)
    {
        string? identity = fallbackDevice.Identity;
        try
        {
            MikrotikDeviceInfo latest = await routerOperations
                .ReadDeviceInfoAsync(connection, job.RouterIp.ToString(), cancellationToken)
                .ConfigureAwait(false);
            identity = latest.Identity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep the last verified identity when a fresh read fails. Only a missing/empty
            // identity becomes Unknown — never abort or force ManualReviewRequired.
            logger.LogWarning(
                ex,
                "Identity re-read failed after first-stage verification; using previously observed identity if available");
        }

        if (string.IsNullOrWhiteSpace(identity))
        {
            logger.LogInformation("Device identity unavailable or empty; classifying as Unknown");
            identity = null;
        }

        return DeviceIdentityClassifier.Classify(identity);
    }

    private void LogClassification(DeviceIdentityClassification classification)
    {
        logger.LogInformation(
            "Device identity classification: Identity={Identity}, DeviceType={DeviceType}, MatchedStandardFormat={MatchedStandardFormat}, ShouldUpgradeToV7={ShouldUpgradeToV7}",
            string.IsNullOrWhiteSpace(classification.Identity) ? "(unavailable)" : classification.Identity,
            classification.DeviceType,
            classification.MatchedStandardFormat,
            classification.ShouldUpgradeToV7);
        logger.LogInformation("Second-stage eligibility: {Reason}", classification.PolicyReason);
    }

    private async Task<StageExecutionResult> ExecuteStageAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        RouterOsVersion target,
        string username,
        string password,
        Func<bool> isWithinScheduleWindow,
        string stageLabel,
        CancellationToken cancellationToken,
        IReadOnlyList<InstalledPackageInfo>? installedOverride = null)
    {
        FirmwareUpgradeOptions options = upgradeOptions.Value;

        logger.LogInformation(
            "Upgrade policy decision: proceed ({Stage} RouterOS {RouterOsVersion} → {TargetRouterOsVersion})",
            stageLabel,
            deviceInfo.Version,
            target);
        logger.LogInformation(
            "RouterBOARD RouterBOOT fields before {Stage} (raw): model={RouterboardModel}, type={FirmwareType}, current-firmware={CurrentFirmware}, upgrade-firmware={UpgradeFirmware}, factory-firmware={FactoryFirmware}",
            stageLabel,
            deviceInfo.RouterboardModel,
            deviceInfo.RouterboardFirmwareType,
            deviceInfo.RouterboardCurrentFirmware,
            deviceInfo.RouterboardUpgradeFirmware,
            deviceInfo.RouterboardFactoryFirmware);

        IReadOnlyList<InstalledPackageInfo> installed = installedOverride
            ?? await routerOperations
                .ListInstalledPackagesAsync(connection, cancellationToken)
                .ConfigureAwait(false);

        PackageInventorySummary inventory = PackageInventoryReporting.Summarize(installed);
        logger.LogInformation(
            "Installed package count: {IndependentCount}",
            inventory.IndependentCount);
        logger.LogInformation(
            "Installed packages: {Packages}",
            PackageInventoryReporting.FormatIndependentPackages(inventory.IndependentPackages));
        logger.LogInformation(
            "Bundled components: {BundledCount}",
            inventory.BundledCount);
        logger.LogDebug(
            "Bundled component inventory: {BundledPackages}",
            PackageInventoryReporting.FormatIndependentPackages(inventory.BundledComponents));

        UpgradePackageResolution resolution = packageResolver.Resolve(
            deviceInfo.ArchitectureName,
            installed,
            target);
        if (!resolution.Succeeded)
        {
            logger.LogError(
                "{Stage} package resolution failed: {Reason}",
                stageLabel,
                resolution.FailureReason);
            return StageExecutionResult.Failed(new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.PackageResolutionFailed,
                Detail = resolution.FailureReason,
            });
        }

        logger.LogInformation(
            "Required packages resolved: {SelectedPackages}",
            string.Join(", ", resolution.Packages.Select(p => $"{p.CatalogPackage.FileName}:{p.CatalogPackage.SizeBytes}")));

        logger.LogInformation("{Stage} backup prerequisite starting", stageLabel);
        DeviceBackupResult backup = await backupService
            .CreatePreUpgradeBackupAsync(connection, job, deviceInfo, username, password, cancellationToken)
            .ConfigureAwait(false);
        if (!backup.Succeeded)
        {
            logger.LogError(
                "{Stage} backup prerequisite failed; firmware fetch will not start: {Reason}",
                stageLabel,
                backup.FailureReason);
            return StageExecutionResult.Failed(new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.BackupFailed,
                Detail = backup.FailureReason,
            });
        }

        logger.LogInformation("{Stage} backup prerequisite passed", stageLabel);

        if (!isWithinScheduleWindow())
        {
            return StageExecutionResult.Failed(PolicyBlock(
                $"Job left its scheduled window after {stageLabel} backup; firmware fetch aborted."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        string baseUrl = options.AdvertisedBaseUrl.TrimEnd('/');
        logger.LogInformation("Firmware server URL selected: {AdvertisedBaseUrl}", baseUrl);

        foreach (ResolvedUpgradePackage package in resolution.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isWithinScheduleWindow())
            {
                return StageExecutionResult.Failed(PolicyBlock(
                    $"Job left its scheduled window during {stageLabel} package download."));
            }

            string url = $"{baseUrl}/{package.RelativeUrlPath}";
            string dst = package.CatalogPackage.FileName;
            logger.LogInformation("Fetching firmware package {PackageFile}", dst);

            try
            {
                await routerOperations
                    .FetchUrlToFileAsync(connection, url, dst, options.FetchTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return StageExecutionResult.Failed(new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeInterrupted,
                    Detail = $"Cancelled during {stageLabel} package download.",
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fetch failed for {PackageFile}", dst);
                return StageExecutionResult.Failed(new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail = $"Fetch failed for '{dst}': {ex.Message}",
                });
            }

            RouterFileInfo? fetched = await WaitForFetchedPackageSizeAsync(
                    connection,
                    dst,
                    package.CatalogPackage.SizeBytes,
                    options.BackupFileReadyTimeout,
                    options.BackupFileReadyPollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fetched is null)
            {
                RouterFileInfo? current = await routerOperations
                    .FindFileAsync(connection, dst, cancellationToken)
                    .ConfigureAwait(false);
                return StageExecutionResult.Failed(new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail = current is null
                        ? $"Downloaded file '{dst}' is missing on the router."
                        : current.SizeBytes <= 0
                            ? $"Downloaded file '{dst}' is empty."
                            : $"Downloaded file '{dst}' size mismatch (router={current.SizeBytes}, expected={package.CatalogPackage.SizeBytes}).",
                });
            }

            logger.LogInformation(
                "Staged package verified: {PackageFile} ({SizeBytes} bytes)",
                dst,
                fetched.SizeBytes);
        }

        logger.LogInformation("Package staging complete ({PackageCount} package(s))", resolution.Packages.Count);

        if (!isWithinScheduleWindow())
        {
            return StageExecutionResult.Failed(PolicyBlock(
                $"Job left its scheduled window before {stageLabel} reboot; installation aborted."));
        }

        logger.LogInformation("Initiating {Stage} reboot for package installation", stageLabel);
        await routerOperations.RebootAsync(connection, cancellationToken).ConfigureAwait(false);
        bool rebootInitiated = true;
        DateTimeOffset rebootInitiatedAt = timeProvider.GetUtcNow();
        DateTimeOffset rebootDeadline = rebootInitiatedAt + options.RebootTimeout;
        logger.LogInformation(
            "Reboot initiated; reconnection deadline {Deadline:yyyy-MM-dd HH:mm:ss} UTC ({Timeout})",
            rebootDeadline.UtcDateTime,
            options.RebootTimeout);

        MikrotikConnectionOptions reconnectOptions = new()
        {
            Host = job.RouterIp.ToString(),
            Username = username,
            Password = password,
            Port = mikroTikOptions.Value.Port,
        };

        ITikConnection? reconnected = null;
        try
        {
            int attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DateTimeOffset now = timeProvider.GetUtcNow();
                TimeSpan remaining = rebootDeadline - now;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                attempt++;
                TimeSpan elapsed = now - rebootInitiatedAt;
                logger.LogInformation(
                    "Reconnection attempt {Attempt} (elapsed {Elapsed}, remaining {Remaining})",
                    attempt,
                    FormatDuration(elapsed),
                    FormatDuration(remaining));

                using CancellationTokenSource attemptCts = new();
                using ITimer attemptTimer = timeProvider.CreateTimer(
                    static state =>
                    {
                        try
                        {
                            ((CancellationTokenSource)state!).Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    },
                    attemptCts,
                    remaining,
                    Timeout.InfiniteTimeSpan);
                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    attemptCts.Token);

                try
                {
                    reconnected = await connectionFactory
                        .CreateAsync(reconnectOptions, linkedCts.Token)
                        .ConfigureAwait(false);
                    logger.LogInformation(
                        "Reconnected successfully after {Elapsed}",
                        FormatDuration(timeProvider.GetUtcNow() - rebootInitiatedAt));
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return StageExecutionResult.Failed(new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.UpgradeInterrupted,
                        Detail = $"Cancelled while waiting for {stageLabel} reboot reconnect.",
                        RebootInitiated = rebootInitiated,
                    });
                }
                catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    now = timeProvider.GetUtcNow();
                    remaining = rebootDeadline - now;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    TimeSpan delay = options.ReconnectInterval < remaining
                        ? options.ReconnectInterval
                        : remaining;
                    try
                    {
                        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return StageExecutionResult.Failed(new UpgradeExecutionResult
                        {
                            Outcome = UpgradeOutcome.UpgradeInterrupted,
                            Detail = $"Cancelled while waiting for {stageLabel} reboot reconnect.",
                            RebootInitiated = rebootInitiated,
                        });
                    }
                }
            }

            if (reconnected is null)
            {
                TimeSpan waited = timeProvider.GetUtcNow() - rebootInitiatedAt;
                logger.LogError(
                    "Reconnection timed out after {Elapsed} (limit {Timeout})",
                    FormatDuration(waited),
                    options.RebootTimeout);
                return StageExecutionResult.Failed(new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeTimedOut,
                    Detail = $"Router did not become reachable within {options.RebootTimeout}.",
                    RebootInitiated = rebootInitiated,
                });
            }

            ITikConnection live = reconnected;
            reconnected = null;

            DateTimeOffset verifyPollDeadline = timeProvider.GetUtcNow() + options.PostUpgradeVerifyTimeout;
            if (verifyPollDeadline > rebootDeadline)
            {
                verifyPollDeadline = rebootDeadline;
            }

            MikrotikDeviceInfo? postInfo = null;
            IReadOnlyList<InstalledPackageInfo>? postPackages = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan remaining = rebootDeadline - timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    live.Dispose();
                    return StageExecutionResult.Failed(
                        TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout));
                }

                using CancellationTokenSource verifyCts = new();
                using ITimer verifyTimer = timeProvider.CreateTimer(
                    static state =>
                    {
                        try
                        {
                            ((CancellationTokenSource)state!).Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    },
                    verifyCts,
                    remaining,
                    Timeout.InfiniteTimeSpan);
                using CancellationTokenSource linkedVerifyCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    verifyCts.Token);

                try
                {
                    postInfo = await routerOperations
                        .ReadDeviceInfoAsync(live, job.RouterIp.ToString(), linkedVerifyCts.Token)
                        .ConfigureAwait(false);
                    postPackages = await routerOperations
                        .ListInstalledPackagesAsync(live, linkedVerifyCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    live.Dispose();
                    return StageExecutionResult.Failed(new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.UpgradeInterrupted,
                        Detail = $"Cancelled during {stageLabel} post-reboot verification.",
                        RebootInitiated = rebootInitiated,
                    });
                }
                catch (OperationCanceledException) when (verifyCts.IsCancellationRequested)
                {
                    live.Dispose();
                    return StageExecutionResult.Failed(
                        TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout));
                }

                if (RouterOsVersion.TryParse(postInfo.Version, out RouterOsVersion postVersion)
                    && postVersion == target)
                {
                    break;
                }

                if (timeProvider.GetUtcNow() >= verifyPollDeadline
                    || timeProvider.GetUtcNow() >= rebootDeadline)
                {
                    break;
                }

                remaining = rebootDeadline - timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    live.Dispose();
                    return StageExecutionResult.Failed(
                        TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout));
                }

                TimeSpan delay = options.ReconnectInterval < remaining
                    ? options.ReconnectInterval
                    : remaining;
                try
                {
                    await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    live.Dispose();
                    return StageExecutionResult.Failed(new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.UpgradeInterrupted,
                        Detail = $"Cancelled during {stageLabel} post-reboot verification.",
                        RebootInitiated = rebootInitiated,
                    });
                }
            }

            if (postInfo is null || postPackages is null)
            {
                live.Dispose();
                return StageExecutionResult.Failed(
                    TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout));
            }

            bool deadlineReached = timeProvider.GetUtcNow() >= rebootDeadline;
            bool versionMatches = RouterOsVersion.TryParse(postInfo.Version, out RouterOsVersion verified)
                && verified == target;

            if (deadlineReached && !versionMatches)
            {
                live.Dispose();
                return StageExecutionResult.Failed(
                    TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout));
            }

            logger.LogInformation("Post-upgrade RouterOS version: {RouterOsVersion}", postInfo.Version);

            PackageInventorySummary postInventory = PackageInventoryReporting.Summarize(postPackages);
            logger.LogInformation(
                "Post-upgrade installed package count: {IndependentCount}",
                postInventory.IndependentCount);
            logger.LogInformation(
                "Post-upgrade installed packages: {Packages}",
                PackageInventoryReporting.FormatIndependentPackages(postInventory.IndependentPackages));
            logger.LogInformation(
                "Post-upgrade bundled components: {BundledCount}",
                postInventory.BundledCount);
            // Raw RouterBOOT loader fields from /system/routerboard/print — not RouterOS version,
            // and not proof that /system/routerboard/upgrade ran. upgrade-firmware commonly updates
            // when a new RouterOS package stages a newer bootloader file on disk.
            logger.LogInformation(
                "RouterBOARD RouterBOOT fields after upgrade (raw; upgrade command not issued): model={RouterboardModel}, type={FirmwareType}, current-firmware={CurrentFirmware}, upgrade-firmware={UpgradeFirmware}, factory-firmware={FactoryFirmware}",
                postInfo.RouterboardModel,
                postInfo.RouterboardFirmwareType,
                postInfo.RouterboardCurrentFirmware,
                postInfo.RouterboardUpgradeFirmware,
                postInfo.RouterboardFactoryFirmware);

            if (timeProvider.GetUtcNow() > rebootDeadline)
            {
                live.Dispose();
                return StageExecutionResult.Failed(
                    TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout));
            }

            if (!versionMatches)
            {
                live.Dispose();
                return StageExecutionResult.Failed(new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeVersionMismatch,
                    Detail = $"Expected RouterOS {target}, observed '{postInfo.Version}'.",
                    PostUpgradeVersion = postInfo.Version,
                    RebootInitiated = rebootInitiated,
                });
            }

            string? expectedArch = string.IsNullOrWhiteSpace(deviceInfo.ArchitectureName)
                ? null
                : FirmwarePackageFileNameParser.NormalizeArchitecture(deviceInfo.ArchitectureName);
            string? actualArch = string.IsNullOrWhiteSpace(postInfo.ArchitectureName)
                ? null
                : FirmwarePackageFileNameParser.NormalizeArchitecture(postInfo.ArchitectureName);
            if (!string.Equals(expectedArch, actualArch, StringComparison.Ordinal))
            {
                live.Dispose();
                return StageExecutionResult.Failed(new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail = $"Architecture changed after upgrade (before={expectedArch}, after={actualArch}).",
                    PostUpgradeVersion = postInfo.Version,
                    RebootInitiated = rebootInitiated,
                });
            }

            PostUpgradePackageVerification packageVerification = PostUpgradePackageVerifier.Verify(
                target,
                expectedArch,
                postPackages,
                resolution.Packages);
            if (!packageVerification.Succeeded)
            {
                live.Dispose();
                return StageExecutionResult.Failed(new UpgradeExecutionResult
                {
                    Outcome = packageVerification.FailureOutcome,
                    Detail = packageVerification.Detail,
                    PostUpgradeVersion = postInfo.Version,
                    RebootInitiated = rebootInitiated,
                });
            }

            if (timeProvider.GetUtcNow() > rebootDeadline)
            {
                live.Dispose();
                return StageExecutionResult.Failed(
                    TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout));
            }

            return new StageExecutionResult
            {
                Execution = new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeSucceeded,
                    PostUpgradeVersion = postInfo.Version,
                    RebootInitiated = rebootInitiated,
                },
                LiveConnection = live,
                PostDeviceInfo = postInfo,
                PostPackages = postPackages,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reconnected?.Dispose();
            return StageExecutionResult.Failed(new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.UpgradeInterrupted,
                Detail = $"Cancelled after {stageLabel} reboot was initiated.",
                RebootInitiated = rebootInitiated,
            });
        }
        finally
        {
            reconnected?.Dispose();
        }
    }

    private async Task<RouterFileInfo?> WaitForFetchedPackageSizeAsync(
        ITikConnection connection,
        string fileName,
        long expectedSizeBytes,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow() + timeout;
        while (timeProvider.GetUtcNow() <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RouterFileInfo? file = await routerOperations
                .FindFileAsync(connection, fileName, cancellationToken)
                .ConfigureAwait(false);
            if (file is not null && file.SizeBytes == expectedSizeBytes)
            {
                return file;
            }

            await Task.Delay(pollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private UpgradeExecutionResult PolicyBlock(
        string detail,
        UpgradeExecutionMode executionMode = UpgradeExecutionMode.FullUpgrade)
    {
        logger.LogWarning("Upgrade policy blocked device: {Detail}", detail);
        return new UpgradeExecutionResult
        {
            Outcome = UpgradeOutcome.PolicyBlocked,
            Detail = detail,
            ExecutionMode = executionMode,
        };
    }

    private UpgradeExecutionResult TimedOutAfterReboot(
        bool rebootInitiated,
        DateTimeOffset rebootInitiatedAt,
        TimeSpan rebootTimeout)
    {
        TimeSpan waited = timeProvider.GetUtcNow() - rebootInitiatedAt;
        logger.LogError(
            "Post-reboot upgrade window timed out after {Elapsed} (limit {Timeout})",
            FormatDuration(waited),
            rebootTimeout);
        return new UpgradeExecutionResult
        {
            Outcome = UpgradeOutcome.UpgradeTimedOut,
            Detail = $"Router did not complete post-reboot verification within {rebootTimeout}.",
            RebootInitiated = rebootInitiated,
        };
    }

    private static bool IsStageSuccess(UpgradeOutcome outcome) =>
        outcome is UpgradeOutcome.UpgradeSucceeded or UpgradeOutcome.SkippedAlreadyAtTarget;

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalMinutes >= 1
            ? $"{(int)value.TotalMinutes}m {value.Seconds:D2}s"
            : $"{value.TotalSeconds:0.###}s";
    }

    private sealed class StageExecutionResult
    {
        public required UpgradeExecutionResult Execution { get; init; }

        public ITikConnection? LiveConnection { get; init; }

        public MikrotikDeviceInfo? PostDeviceInfo { get; init; }

        public IReadOnlyList<InstalledPackageInfo>? PostPackages { get; init; }

        public static StageExecutionResult Failed(UpgradeExecutionResult execution) =>
            new() { Execution = execution };
    }
}
