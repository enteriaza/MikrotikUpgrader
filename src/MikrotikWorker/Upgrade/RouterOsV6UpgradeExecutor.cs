using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade.Ospf;
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
    IOspfMigrationService ospfMigrationService,
    IOspfStaticSummaryRedistributionService ospfStaticSummaryRedistributionService,
    IOptions<FirmwareUpgradeOptions> upgradeOptions,
    IOptions<MikroTikOptions> mikroTikOptions,
    TimeProvider timeProvider,
    ILogger<RouterOsV6UpgradeExecutor> logger,
    IImportedCsvResultWriter? csvResultWriter = null) : IRouterOsV6UpgradeExecutor
{
    private readonly IImportedCsvResultWriter _csvResultWriter =
        csvResultWriter ?? NullImportedCsvResultWriter.Instance;

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

        if (!options.Enabled)
        {
            const string disabledDetail =
                "Firmware upgrades are administratively disabled (FirmwareUpgrade:Enabled=false); no upgrade was attempted and the device was not changed.";
            logger.LogWarning(
                "Upgrade administratively disabled: FirmwareUpgrade:Enabled is false. No upgrade will be attempted for this device; the job is reported as Failed. Set FirmwareUpgrade:Enabled=true to allow upgrades.");
            using (DeviceLogging.BeginServiceOnly())
            {
                logger.LogWarning(
                    "Upgrade administratively disabled. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RouterName={RouterName}",
                    job.ImportId,
                    job.JobId,
                    job.RouterIp,
                    job.RouterName);
            }

            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.SkippedUpgradeDisabled,
                Detail = disabledDetail,
                PostUpgradeVersion = deviceInfo.Version,
                PostUpgradeArchitecture = deviceInfo.ArchitectureName,
                CompletedStage = UpgradeCompletedStages.None,
            };
        }

        if (!RouterOsVersion.TryParse(deviceInfo.Version, out RouterOsVersion currentVersion))
        {
            return PolicyBlock($"RouterOS version '{deviceInfo.Version}' could not be parsed.");
        }

        if (assessment.OverallStatus == DeviceAssessmentStatus.Blocked)
        {
            return PolicyBlock(
                $"Device assessment status '{assessment.OverallStatus}' is not eligible for automated upgrade.");
        }

        if (!isWithinScheduleWindow())
        {
            return PolicyBlock("Job is outside its scheduled execution window.");
        }

        logger.LogInformation(
            "Initial RouterOS version: {RouterOsVersion}",
            deviceInfo.Version);

        // UpgradeStarted boundary: after connect/preflight/policy gates, before mutation work.
        await _csvResultWriter
            .RecordUpgradeStartedAsync(job, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        // Connection-time safety: remove legacy router-side FleetUpgrade automation before any
        // firmware mutation (applies to every device, including ACE/BR).
        LegacyFleetUpgradeCleanupResult legacyCleanup = await legacyFleetUpgradeAutomationCleanup
            .CleanupAsync(connection, job, cancellationToken)
            .ConfigureAwait(false);
        if (!legacyCleanup.Succeeded)
        {
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.LegacyFirmwareAutomationCleanupFailed,
                Detail = legacyCleanup.FailureReason,
                PostUpgradeVersion = deviceInfo.Version,
                PostUpgradeArchitecture = deviceInfo.ArchitectureName,
                CompletedStage = UpgradeCompletedStages.None,
            };
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
        bool deleteOspfPlanInFinally = false;
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
                    return WithOverrides(
                        stage1.Execution,
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
                return PolicyBlock($"Unsupported RouterOS major version '{currentVersion}'.");
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

            // Use the same identity string that drove classification (post re-read).
            string liveIdentity = classification.Identity ?? workingDevice.Identity ?? string.Empty;

            // Eligible devices always proceed to the configured RouterOS 7 target.
            bool willEnterV7Stage = classification.ShouldUpgradeToV7;

            OspfMigrationPlan? ospfPlan = null;
            OspfGateResult ospfGate = await ospfMigrationService
                .CaptureAndValidateAsync(
                    live,
                    job,
                    liveIdentity,
                    workingDevice.Version,
                    workingDevice.ArchitectureName,
                    persistPlan: willEnterV7Stage,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!ospfGate.Succeeded)
            {
                ospfMigrationService.DeletePlan(job);
                return new UpgradeExecutionResult
                {
                    Outcome = ospfGate.FailureOutcome ?? UpgradeOutcome.OspfPlanValidationFailed,
                    Detail = ospfGate.Detail,
                    PostUpgradeVersion = workingDevice.Version,
                    PostUpgradeArchitecture = workingDevice.ArchitectureName,
                    RebootInitiated = rebootInitiated,
                    CompletedStage = completedAfterV6,
                };
            }

            ospfPlan = ospfGate.Plan;
            // Persist only when the v7 stage may run; always delete on every terminal path
            // (including cancellation) via finally when a plan may exist on disk.
            deleteOspfPlanInFinally = willEnterV7Stage;

            if (!classification.ShouldUpgradeToV7)
            {
                // Identity policy currently makes every device type eligible; this guard keeps the
                // fail-closed contract if policy ever excludes a device. An ineligible device is
                // never reported as upgraded — it is Blocked with an explicit reason.
                ospfMigrationService.DeletePlan(job);
                deleteOspfPlanInFinally = false;
                logger.LogWarning(
                    "RouterOS 7 upgrade blocked by identity policy. DeviceType={DeviceType}, Reason={Reason}",
                    classification.DeviceType,
                    classification.PolicyReason);
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.PolicyBlocked,
                    Detail =
                        $"Device is not eligible for the RouterOS 7 target by identity policy ({classification.PolicyReason}); the device was not upgraded to RouterOS 7.",
                    PostUpgradeVersion = workingDevice.Version,
                    PostUpgradeArchitecture = workingDevice.ArchitectureName,
                    RebootInitiated = rebootInitiated,
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
                ospfMigrationService.DeletePlan(job);
                deleteOspfPlanInFinally = false;
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.TargetUnavailable,
                    Detail = $"RouterOS 7 target file is unavailable or invalid ({v7TargetProvider.VersionFilePath}).",
                    PostUpgradeVersion = workingDevice.Version,
                    PostUpgradeArchitecture = workingDevice.ArchitectureName,
                    RebootInitiated = rebootInitiated,
                    CompletedStage = completedAfterV6,
                };
            }

            if (!RouterOsVersion.TryParse(workingDevice.Version, out RouterOsVersion workingVersion))
            {
                ospfMigrationService.DeletePlan(job);
                deleteOspfPlanInFinally = false;
                return PolicyBlock(
                    $"Post-step RouterOS version '{workingDevice.Version}' could not be parsed.");
            }

            int cmpToV7 = workingVersion.CompareTo(v7Snapshot.Version);
            if (cmpToV7 == 0)
            {
                // Already on ROS7 target: reconcile OSPF without package reboot, then RouterBOARD path.
                logger.LogInformation(
                    "Device already at RouterOS 7 target {Target}; reconciling OSPF without package stage",
                    v7Snapshot.VersionText);

                // Verified backup before OSPF config mutation (and before any RouterBOARD firmware mutation).
                MikrotikDeviceInfo alreadyV7Device = await routerOperations
                    .ReadDeviceInfoAsync(live, job.RouterIp.ToString(), cancellationToken)
                    .ConfigureAwait(false);

                DeviceBackupResult alreadyV7Backup = await backupService
                    .CreatePreUpgradeBackupAsync(live, job, alreadyV7Device, username, password, cancellationToken)
                    .ConfigureAwait(false);
                if (!IsCentralBackupVerified(alreadyV7Backup))
                {
                    ospfMigrationService.DeletePlan(job);
                    deleteOspfPlanInFinally = false;
                    return new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.BackupFailed,
                        Detail = alreadyV7Backup.FailureReason ?? "Pre-OSPF backup failed on already-v7 device.",
                        PostUpgradeVersion = alreadyV7Device.Version,
                        PostUpgradeArchitecture = alreadyV7Device.ArchitectureName,
                        RebootInitiated = rebootInitiated,
                        CompletedStage = UpgradeCompletedStages.None,
                    };
                }

                // Caller cancellation must stop the workflow before any OSPF mutation, even when
                // central backup verification (and optional remote cleanup failure) would otherwise continue.
                cancellationToken.ThrowIfCancellationRequested();

                // Device is already on the ROS7 target and central backup of that state is verified.
                // There is no separate backup stage value; routeros7 is the last completed stage
                // until OSPF (or a later stage) succeeds — matching Path B post-backup reporting.
                string alreadyV7CompletedStage = UpgradeCompletedStages.RouterOs7;

                OspfApplyGateResult alreadyV7Ospf = await ospfMigrationService
                    .ApplyAndVerifyAsync(
                        live,
                        job,
                        alreadyV7Device.Identity ?? workingDevice.Identity ?? string.Empty,
                        ospfPlan,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!alreadyV7Ospf.Succeeded)
                {
                    deleteOspfPlanInFinally = false;
                    logger.LogWarning(
                        "OSPF migration failed on already-v7 device; preserving migration plan for retry/diagnosis. Outcome={Outcome}",
                        alreadyV7Ospf.FailureOutcome);
                    return new UpgradeExecutionResult
                    {
                        Outcome = alreadyV7Ospf.FailureOutcome,
                        Detail = alreadyV7Ospf.Detail,
                        PostUpgradeVersion = alreadyV7Device.Version,
                        PostUpgradeArchitecture = alreadyV7Device.ArchitectureName,
                        RebootInitiated = rebootInitiated,
                        CompletedStage = alreadyV7CompletedStage,
                    };
                }

                OptionalSummaryStageGate alreadyV7Summary = await RunOptionalStaticSummaryRedistributionAsync(
                        live,
                        alreadyV7Ospf.Plan ?? ospfPlan,
                        alreadyV7Device.Version,
                        alreadyV7Device.ArchitectureName,
                        rebootInitiated,
                        completedStageOnFailure: UpgradeCompletedStages.OspfMigration,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (alreadyV7Summary.Failure is not null)
                {
                    deleteOspfPlanInFinally = false;
                    return alreadyV7Summary.Failure;
                }

                string alreadyV7CompletedBeforeRb = alreadyV7Summary.StageCompleted
                    ? UpgradeCompletedStages.OspfSummaryRedistribution
                    : UpgradeCompletedStages.OspfMigration;

                ospfMigrationService.DeletePlan(job);
                deleteOspfPlanInFinally = false;

                UpgradeExecutionResult alreadyV7Rb = await routerboardFirmwareUpgradeService
                    .ExecuteAsync(
                        live,
                        job,
                        alreadyV7Device,
                        username,
                        password,
                        priorRebootInitiated: rebootInitiated,
                        completedStageBefore: alreadyV7CompletedBeforeRb,
                        cancellationToken)
                    .ConfigureAwait(false);

                // Verified on the RouterOS 7 target with no package upgrade and no RouterBOARD reboot:
                // report "already up to date" rather than claiming an upgrade was performed.
                if (alreadyV7Rb.Outcome == UpgradeOutcome.UpgradeSucceeded && !alreadyV7Rb.RebootInitiated)
                {
                    return new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.SkippedAlreadyAtTarget,
                        Detail =
                            $"Device already runs the RouterOS 7 target {v7Snapshot.VersionText}; no package upgrade was required."
                            + (string.IsNullOrWhiteSpace(alreadyV7Rb.Detail) ? string.Empty : $" {alreadyV7Rb.Detail}"),
                        PostUpgradeVersion = alreadyV7Rb.PostUpgradeVersion,
                        PostUpgradeArchitecture = alreadyV7Rb.PostUpgradeArchitecture,
                        RebootInitiated = false,
                        CompletedStage = alreadyV7Rb.CompletedStage,
                    };
                }

                return alreadyV7Rb;
            }

            if (cmpToV7 > 0)
            {
                ospfMigrationService.DeletePlan(job);
                deleteOspfPlanInFinally = false;
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

            // Ownership: ExecuteStageAsync never disposes its input. After stage1, `live` is Conn₁
            // owned here via disposeLive — dispose it now that stage2 has finished using it
            // (whether or not stage2 rebooted; the peer session is superseded either way).
            if (disposeLive)
            {
                TryDisposeOwnedConnection(live);
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
                ospfMigrationService.DeletePlan(job);
                deleteOspfPlanInFinally = false;
                return new UpgradeExecutionResult
                {
                    Outcome = stage2.Execution.Outcome,
                    Detail = stage2.Execution.Detail,
                    PostUpgradeVersion = stage2.Execution.PostUpgradeVersion ?? workingDevice.Version,
                    PostUpgradeArchitecture =
                        stage2.PostDeviceInfo?.ArchitectureName ?? workingDevice.ArchitectureName,
                    RebootInitiated = rebootInitiated || stage2.Execution.RebootInitiated,
                    CompletedStage = completedAfterV6,
                };
            }

            // Post-v7: verified ROS7 central backup → OSPF reconcile → optional summary → RouterBOARD.
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

                // Verified central backup of the post-upgrade RouterOS 7 state before any OSPF mutation.
                logger.LogInformation(
                    "Post-RouterOS-7 backup before OSPF migration starting");
                DeviceBackupResult postV7Backup = await backupService
                    .CreatePreUpgradeBackupAsync(postV7, job, postV7Device, username, password, cancellationToken)
                    .ConfigureAwait(false);
                if (!IsCentralBackupVerified(postV7Backup))
                {
                    deleteOspfPlanInFinally = false;
                    return new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.BackupFailed,
                        Detail = postV7Backup.FailureReason
                                 ?? "Post-RouterOS-7 backup before OSPF migration failed.",
                        PostUpgradeVersion = postV7Device.Version,
                        PostUpgradeArchitecture = postV7Device.ArchitectureName,
                        RebootInitiated = rebootInitiated || stage2.Execution.RebootInitiated,
                        CompletedStage = UpgradeCompletedStages.RouterOs7,
                    };
                }

                // Explicit gate: verified backup must not authorise OSPF after caller cancellation.
                cancellationToken.ThrowIfCancellationRequested();

                OspfApplyGateResult ospfApply = await ospfMigrationService
                    .ApplyAndVerifyAsync(
                        postV7,
                        job,
                        postV7Device.Identity ?? workingDevice.Identity ?? string.Empty,
                        ospfPlan,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!ospfApply.Succeeded)
                {
                    // Preserve the plan on OSPF apply/verify failure so retry and diagnosis retain
                    // provenance evidence. Delete only after successful OSPF migration.
                    deleteOspfPlanInFinally = false;
                    logger.LogWarning(
                        "OSPF migration failed; preserving migration plan on disk for retry/diagnosis. Outcome={Outcome}",
                        ospfApply.FailureOutcome);
                    return new UpgradeExecutionResult
                    {
                        Outcome = ospfApply.FailureOutcome,
                        Detail = ospfApply.Detail,
                        PostUpgradeVersion = postV7Device.Version,
                        PostUpgradeArchitecture = postV7Device.ArchitectureName,
                        RebootInitiated = rebootInitiated || stage2.Execution.RebootInitiated,
                        CompletedStage = UpgradeCompletedStages.RouterOs7,
                    };
                }

                OptionalSummaryStageGate summaryGate = await RunOptionalStaticSummaryRedistributionAsync(
                        postV7,
                        ospfApply.Plan ?? ospfPlan,
                        postV7Device.Version,
                        postV7Device.ArchitectureName,
                        rebootInitiated || stage2.Execution.RebootInitiated,
                        completedStageOnFailure: UpgradeCompletedStages.OspfMigration,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (summaryGate.Failure is not null)
                {
                    deleteOspfPlanInFinally = false;
                    return summaryGate.Failure;
                }

                string completedBeforeRb = summaryGate.StageCompleted
                    ? UpgradeCompletedStages.OspfSummaryRedistribution
                    : UpgradeCompletedStages.OspfMigration;

                ospfMigrationService.DeletePlan(job);
                deleteOspfPlanInFinally = false;

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
                        completedStageBefore: completedBeforeRb,
                        cancellationToken)
                    .ConfigureAwait(false);

                return rbResult;
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
            if (deleteOspfPlanInFinally)
            {
                try
                {
                    ospfMigrationService.DeletePlan(job);
                }
                catch
                {
                }
            }

            // Never dispose `connection` (Conn₀) — owned by RouterJobExecutor.
            if (disposeLive && live is not null && !ReferenceEquals(live, connection))
            {
                TryDisposeOwnedConnection(live);
            }
        }
    }

    private static UpgradeExecutionResult WithOverrides(
        UpgradeExecutionResult result,
        string? completedStage = null,
        string? postArchitecture = null) =>
        new()
        {
            Outcome = result.Outcome,
            Detail = result.Detail,
            PostUpgradeVersion = result.PostUpgradeVersion,
            PostUpgradeArchitecture = postArchitecture ?? result.PostUpgradeArchitecture,
            RebootInitiated = result.RebootInitiated,
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

    /// <summary>
    /// Runs the optional static-summary redistribution stage after OSPF verify and before RouterBOARD.
    /// </summary>
    private async Task<OptionalSummaryStageGate> RunOptionalStaticSummaryRedistributionAsync(
        ITikConnection connection,
        OspfMigrationPlan? plan,
        string? postUpgradeVersion,
        string? postUpgradeArchitecture,
        bool rebootInitiated,
        string completedStageOnFailure,
        CancellationToken cancellationToken)
    {
        OspfStaticSummaryRedistributionResult summary = await ospfStaticSummaryRedistributionService
            .ApplyAndVerifyAsync(connection, plan, cancellationToken)
            .ConfigureAwait(false);

        if (summary.Skipped)
        {
            logger.LogInformation("{Detail}", summary.Detail);
            return OptionalSummaryStageGate.Skipped();
        }

        if (!summary.Succeeded)
        {
            logger.LogWarning(
                "OSPF static summary redistribution failed; preserving migration plan and blocking RouterBOARD. Detail={Detail}",
                summary.Detail);
            return OptionalSummaryStageGate.Failed(new UpgradeExecutionResult
            {
                Outcome = summary.FailureOutcome,
                Detail = summary.Detail,
                PostUpgradeVersion = postUpgradeVersion,
                PostUpgradeArchitecture = postUpgradeArchitecture,
                RebootInitiated = rebootInitiated,
                CompletedStage = completedStageOnFailure,
            });
        }

        logger.LogInformation(
            "OSPF static summary redistribution completed; CompletedStage={CompletedStage}",
            UpgradeCompletedStages.OspfSummaryRedistribution);
        return OptionalSummaryStageGate.Completed();
    }

    private readonly struct OptionalSummaryStageGate
    {
        public UpgradeExecutionResult? Failure { get; private init; }
        public bool StageCompleted { get; private init; }

        public static OptionalSummaryStageGate Skipped() => new();

        public static OptionalSummaryStageGate Completed() => new() { StageCompleted = true };

        public static OptionalSummaryStageGate Failed(UpgradeExecutionResult failure) =>
            new() { Failure = failure };
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
        // Ownership: `connection` (stage input) remains owned by the caller. After reboot it is
        // unusable but must not be disposed here — that would race with RouterJobExecutor's
        // ownership of Conn₀ and with disposeLive ownership of intermediate LiveConnections.
        // This method owns only `reconnected` until transferred via LiveConnection or disposed.
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
            reconnected = null;
            return StageExecutionResult.Failed(new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.UpgradeInterrupted,
                Detail = $"Cancelled after {stageLabel} reboot was initiated.",
                RebootInitiated = rebootInitiated,
            });
        }
        finally
        {
            // Only dispose a reconnect that was not transferred to LiveConnection (reconnected set null).
            reconnected?.Dispose();
        }
    }

    /// <summary>
    /// Disposes a connection this executor owns. Never throws — must not mask upgrade outcomes.
    /// </summary>
    private static void TryDisposeOwnedConnection(ITikConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            connection.Dispose();
        }
        catch
        {
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

    private UpgradeExecutionResult PolicyBlock(string detail)
    {
        logger.LogWarning("Upgrade policy blocked device: {Detail}", detail);
        return new UpgradeExecutionResult
        {
            Outcome = UpgradeOutcome.PolicyBlocked,
            Detail = detail,
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

    /// <summary>
    /// True when central backup artefacts were verified. Remote cleanup failure after verification
    /// still qualifies — cleanup status must not block OSPF when central copies are good.
    /// </summary>
    internal static bool IsCentralBackupVerified(DeviceBackupResult backup) =>
        backup.Succeeded || backup.RemoteCleanupFailed;

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
