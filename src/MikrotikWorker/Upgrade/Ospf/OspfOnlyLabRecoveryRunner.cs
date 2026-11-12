using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;
using MsLog = Microsoft.Extensions.Logging.ILogger;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Controlled OSPF-only lab recovery for a single already-upgraded RouterOS 7 device.
/// Does not register or invoke <see cref="IRouterOsV6UpgradeExecutor"/>, CSV scheduling, package fetch, or firmware enablement.
/// </summary>
internal static class OspfOnlyLabRecoveryRunner
{
    public const string Argument = "--ospf-only-lab-recovery";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static bool IsRequested(string[] args) =>
        args.Any(static a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!IsRequested(args))
        {
            return 2;
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddJsonFile(
            $"appsettings.{builder.Environment.EnvironmentName}.local.json",
            optional: true,
            reloadOnChange: false);

        ConfigureServices(builder.Services, builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        using IHost host = builder.Build();

        IOptions<FirmwareUpgradeOptions> upgradeOptions =
            host.Services.GetRequiredService<IOptions<FirmwareUpgradeOptions>>();
        IOptions<MikroTikOptions> mikrotikOptions =
            host.Services.GetRequiredService<IOptions<MikroTikOptions>>();

        string? gateFailure = DescribeGateFailure(upgradeOptions.Value, mikrotikOptions.Value);
        if (gateFailure is not null)
        {
            Console.Error.WriteLine("OSPF-only lab recovery refused: " + gateFailure);
            return 3;
        }

        OspfLabTargetOptions pin = upgradeOptions.Value.OspfLabTarget ?? new OspfLabTargetOptions();
        IPAddress targetIp = IPAddress.Parse(pin.RouterIp.Trim());
        string expectedIdentity = pin.RouterIdentity;

        IMikrotikConnectionFactory factory = host.Services.GetRequiredService<IMikrotikConnectionFactory>();
        IMikrotikDeviceInfoReader deviceInfoReader = host.Services.GetRequiredService<IMikrotikDeviceInfoReader>();
        IOspfMigrationService ospf = host.Services.GetRequiredService<IOspfMigrationService>();
        IOspfMikrotikOperations ops = host.Services.GetRequiredService<IOspfMikrotikOperations>();
        IOspfStaticSummaryRedistributionService summaryRedistribution =
            host.Services.GetRequiredService<IOspfStaticSummaryRedistributionService>();
        IDeviceBackupService backups = host.Services.GetRequiredService<IDeviceBackupService>();
        ILoggerFactory loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        MsLog logger = loggerFactory.CreateLogger("OspfOnlyLabRecovery");

        MikrotikConnectionOptions connectionOptions = new()
        {
            Host = targetIp.ToString(),
            Username = mikrotikOptions.Value.Username,
            Password = mikrotikOptions.Value.Password,
            Port = mikrotikOptions.Value.Port,
        };

        using ITikConnection connection = await factory
            .CreateAsync(connectionOptions, cancellationToken)
            .ConfigureAwait(false);

        MikrotikDeviceInfo info = await deviceInfoReader
            .ReadDeviceInfoAsync(connection, targetIp.ToString(), cancellationToken)
            .ConfigureAwait(false);

        string? identityFailure = DescribeIdentityFailure(info, targetIp, expectedIdentity);
        if (identityFailure is not null)
        {
            logger.LogError("Pre-execution identity check failed: {Reason}", identityFailure);
            return 4;
        }

        DeviceIdentityClassification classification = DeviceIdentityClassifier.Classify(info.Identity);
        if (classification.DeviceType != DeviceType.Br || !classification.MatchedStandardFormat)
        {
            logger.LogError(
                "Device classification failed. Type={Type}, MatchedStandardFormat={Matched}",
                classification.DeviceType,
                classification.MatchedStandardFormat);
            return 4;
        }

        if (!RouterOsVersion.TryParse(info.Version, out RouterOsVersion ros)
            || !ros.IsV7OrLater
            || ros.Major != 7
            || ros.Minor != 25
            || ros.Patch != 1)
        {
            logger.LogError(
                "Expected RouterOS 7.25.1; observed '{Version}'. Refusing OSPF-only recovery.",
                info.Version);
            return 4;
        }

        if (!string.Equals(info.ArchitectureName, "mipsbe", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("Expected architecture mipsbe; observed '{Arch}'", info.ArchitectureName);
            return 4;
        }

        bool boardOk =
            string.Equals(info.BoardName, "hEX PoE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(info.BoardName, "960PGS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(info.RouterboardModel, "960PGS", StringComparison.OrdinalIgnoreCase)
            || (info.BoardName?.Contains("960PGS", StringComparison.OrdinalIgnoreCase) == true)
            || (info.BoardName?.Contains("hEX PoE", StringComparison.OrdinalIgnoreCase) == true);
        if (!boardOk)
        {
            logger.LogError(
                "Expected board 960PGS / hEX PoE; observed BoardName='{Board}', RouterboardModel='{Model}'",
                info.BoardName,
                info.RouterboardModel);
            return 4;
        }

        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "ospf-only-lab-recovery",
            RowNumber = 2,
            RouterName = expectedIdentity,
            RouterIp = targetIp,
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(4),
        };

        string evidenceDir = Path.Combine(
            AppContext.BaseDirectory,
            "Logs",
            "OspfOnlyLab",
            $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{job.ImportId:N}");
        Directory.CreateDirectory(evidenceDir);

        DateTimeOffset startUtc = DateTimeOffset.UtcNow;
        string managementHost = string.IsNullOrWhiteSpace(pin.ManagementHost)
            ? "198.18.0.66"
            : pin.ManagementHost.Trim();

        logger.LogInformation(
            "OSPF-only lab recovery starting. ImportId={ImportId}, JobId={JobId}, Host={Host}, Identity={Identity}, Version={Version}, ManagementHost={Mgmt}, EvidenceDir={EvidenceDir}",
            job.ImportId,
            job.JobId,
            targetIp,
            info.Identity,
            info.Version,
            managementHost,
            evidenceDir);

        // Preflight operational snapshot (neighbours / routes / filter menus) — no mutation yet.
        object preflightOperational = await CaptureOperationalAsync(ops, connection, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(
                Path.Combine(evidenceDir, "01-preflight-operational.json"),
                preflightOperational,
                cancellationToken)
            .ConfigureAwait(false);

        OspfV7Inventory preflightInventory = await ops.ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<OspfIpAddressRow> preflightAddresses =
            await ops.ReadIpAddressInventoryAsync(connection, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Dictionary<string, string>> preflightNeighbours =
            await ops.ReadOspfNeighboursAsync(connection, cancellationToken).ConfigureAwait(false);

        Dictionary<string, string>? fullNbr = preflightNeighbours.FirstOrDefault(n =>
            string.Equals(Get(n, "state"), "Full", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Get(n, "router-id"), "198.18.255.253", StringComparison.Ordinal));
        if (fullNbr is null)
        {
            logger.LogError(
                "Preflight neighbour RID 198.18.255.253 is not Full. Refusing mutation. Neighbours={Count}",
                preflightNeighbours.Count);
            await WriteJsonAsync(
                    Path.Combine(evidenceDir, "preflight-neighbours.json"),
                    preflightNeighbours,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(
                    Path.Combine(evidenceDir, "verdict.txt"),
                    "FAILED — PREFLIGHT NEIGHBOUR NOT FULL",
                    cancellationToken)
                .ConfigureAwait(false);
            return 4;
        }

        // --- Pre-reconciliation backup (mandatory before any OSPF mutation) ---
        logger.LogInformation("Creating pre-reconciliation ROS7 backup");
        DeviceBackupResult preBackup = await backups
            .CreatePreUpgradeBackupAsync(
                connection,
                job,
                info,
                mikrotikOptions.Value.Username,
                mikrotikOptions.Value.Password,
                cancellationToken)
            .ConfigureAwait(false);

        await WriteJsonAsync(
                Path.Combine(evidenceDir, "02-pre-reconciliation-backup.json"),
                DescribeBackup(preBackup),
                cancellationToken)
            .ConfigureAwait(false);

        if (!preBackup.Succeeded)
        {
            logger.LogError(
                "Pre-reconciliation backup failed: {Reason}. Refusing OSPF mutation.",
                preBackup.FailureReason);
            await WriteTextAsync(
                    Path.Combine(evidenceDir, "verdict.txt"),
                    "FAILED — PRE-RECONCILIATION BACKUP FAILED; NO OSPF MUTATION",
                    cancellationToken)
                .ConfigureAwait(false);
            return 8;
        }

        // Fresh inventory + plan rebuild after backup (do not reuse stale .ids).
        MikrotikDeviceInfo afterBackup = await deviceInfoReader
            .ReadDeviceInfoAsync(connection, targetIp.ToString(), cancellationToken)
            .ConfigureAwait(false);
        string? afterBackupIdentity = DescribeIdentityFailure(afterBackup, targetIp, expectedIdentity);
        if (afterBackupIdentity is not null
            || !string.Equals(afterBackup.Version, info.Version, StringComparison.Ordinal))
        {
            logger.LogError(
                "Identity/version changed after pre-backup: {Reason}",
                afterBackupIdentity ?? "version changed");
            return 4;
        }

        OspfV7Inventory liveInventory = await ops.ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<OspfIpAddressRow> addresses =
            await ops.ReadIpAddressInventoryAsync(connection, cancellationToken).ConfigureAwait(false);

        OspfGateResult capture = await ospf
            .CaptureAndValidateAsync(
                connection,
                job,
                info.Identity!,
                info.Version,
                info.ArchitectureName,
                persistPlan: false,
                cancellationToken)
            .ConfigureAwait(false);

        OspfMigrationPlan? plan = capture.Plan;
        string planSource = "live-v7-capture";

        bool livePlanIncomplete = plan is null
            || !capture.Succeeded
            || plan.PassiveSiteNetworks.Count == 0
            || plan.AreaRanges.Count == 0
            || !plan.PassiveSiteNetworks.Any(static s =>
                OspfSiteMatcherConsolidation.IsAllInterfaces(s.Interfaces)
                && (s.NetworkCidr.Contains("100.64.0.0/10", StringComparison.Ordinal)
                    || s.NetworkCidr.Contains("172.16.0.0/12", StringComparison.Ordinal)
                    || s.NetworkCidr.Contains("/16", StringComparison.Ordinal)
                    || s.Comment?.Contains("PPPoE", StringComparison.OrdinalIgnoreCase) == true));

        if (livePlanIncomplete)
        {
            logger.LogWarning(
                "Live v7 capture alone is incomplete for fleet OSPF recovery (Succeeded={Succeeded}, Detail={Detail}). Rebuilding from pre-upgrade export reference + live inventory.",
                capture.Succeeded,
                capture.Detail);

            await WriteTextAsync(
                    Path.Combine(evidenceDir, "live-capture-validation.txt"),
                    capture.Detail ?? "live plan incomplete (missing site/PPPoE matchers or area ranges)",
                    cancellationToken)
                .ConfigureAwait(false);

            string exportsDir = Path.Combine(AppContext.BaseDirectory, "Backups", "Exports");
            string? referencePath = OspfLabRecoveryPlanBuilder.FindPreUpgradeExportPath(
                exportsDir,
                expectedIdentity,
                targetIp.ToString());
            if (referencePath is null)
            {
                logger.LogError(
                    "No pre-upgrade .rsc reference with OSPF networks/area-ranges found under {ExportsDir}. Refusing mutation.",
                    exportsDir);
                return 5;
            }

            OspfPlanValidationResult recovery = OspfLabRecoveryPlanBuilder.BuildFromLiveAndReference(
                job.ImportId,
                job.JobId,
                targetIp,
                info.Identity!,
                info.Version,
                info.ArchitectureName,
                liveInventory,
                addresses,
                referencePath);

            await WriteJsonAsync(
                    Path.Combine(evidenceDir, "recovery-reference.json"),
                    new { ReferencePath = referencePath, recovery.Succeeded, recovery.Plan.ValidationErrors, recovery.Plan.NormalisationNotes },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!recovery.Succeeded)
            {
                logger.LogError(
                    "OSPF recovery plan validation failed: {Detail}",
                    string.Join("; ", recovery.Plan.ValidationErrors));
                return 5;
            }

            plan = recovery.Plan;
            planSource = "live-v7-inventory+pre-upgrade-rsc-reference";
        }

        if (plan is null || !plan.IsValid)
        {
            logger.LogError("OSPF plan unavailable after capture/recovery construction.");
            return 5;
        }

        OspfLiveIdentityResolutionResult identityResolution =
            OspfLiveIdentityResolver.Resolve(plan, liveInventory, addresses);
        OspfLiveAreaBinding? resolvedBackbone = identityResolution.Binding?.Areas.Values
            .FirstOrDefault(static a => string.Equals(a.AreaId, "0.0.0.0", StringComparison.Ordinal));
        OspfLiveAreaBinding? resolvedSite = identityResolution.Binding?.Areas.Values
            .FirstOrDefault(a =>
                string.Equals(
                    a.LiveName,
                    OspfFleetNaming.Ros7SiteAreaName(expectedIdentity),
                    StringComparison.OrdinalIgnoreCase));

        await WriteJsonAsync(
                Path.Combine(evidenceDir, "03-live-identity-resolution.json"),
                new
                {
                    identityResolution.Succeeded,
                    identityResolution.Errors,
                    Binding = identityResolution.Binding is null
                        ? null
                        : new
                        {
                            identityResolution.Binding.LogicalInstanceName,
                            identityResolution.Binding.LiveInstanceName,
                            identityResolution.Binding.LiveInstanceId,
                            identityResolution.Binding.LiveRouterId,
                            identityResolution.Binding.DerivedLoopbackRouterId,
                            identityResolution.Binding.LoopbackInterfaceName,
                            Areas = identityResolution.Binding.Areas.Values.Select(static a => new
                            {
                                a.LogicalName,
                                a.LiveName,
                                a.AreaId,
                                a.LiveObjectId,
                            }),
                        },
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!identityResolution.Succeeded || identityResolution.Binding is null)
        {
            logger.LogError(
                "Live OSPF identity resolution failed: {Detail}",
                string.Join("; ", identityResolution.Errors));
            await WriteTextAsync(
                    Path.Combine(evidenceDir, "verdict.txt"),
                    "FAILED — LIVE IDENTITY RESOLUTION; NO OSPF MUTATION",
                    cancellationToken)
                .ConfigureAwait(false);
            return 5;
        }

        OspfLiveIdentityBinding binding = identityResolution.Binding;
        if (!string.Equals(binding.LiveInstanceName, OspfFleetNaming.Ros7InstanceName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(binding.DerivedLoopbackRouterId, "10.16.0.11", StringComparison.Ordinal)
            || resolvedBackbone is null
            || !string.Equals(resolvedBackbone.LiveName, OspfFleetNaming.Ros7BackboneAreaName, StringComparison.OrdinalIgnoreCase)
            || resolvedSite is null
            || !string.Equals(resolvedSite.AreaId, "10.16.0.11", StringComparison.Ordinal))
        {
            logger.LogError(
                "Resolved OSPF identities do not match authorised Tommy targets. Instance={Inst} RID={Rid} BH={Bh}/{BhId} Site={Site}/{SiteId}",
                binding.LiveInstanceName,
                binding.DerivedLoopbackRouterId,
                resolvedBackbone?.LiveName,
                resolvedBackbone?.AreaId,
                resolvedSite?.LiveName,
                resolvedSite?.AreaId);
            return 5;
        }

        plan = OspfLiveIdentityResolver.RemapPlan(plan, binding);

        string? planGuard = DescribePlanTargetFailure(plan, expectedIdentity);
        if (planGuard is not null)
        {
            logger.LogError("Plan target validation failed: {Reason}", planGuard);
            await WriteTextAsync(
                    Path.Combine(evidenceDir, "verdict.txt"),
                    "FAILED — PLAN TARGET MISMATCH; NO OSPF MUTATION",
                    cancellationToken)
                .ConfigureAwait(false);
            return 5;
        }

        IOspfMigrationPlanStore planStore = host.Services.GetRequiredService<IOspfMigrationPlanStore>();
        await planStore.SaveAsync(plan, cancellationToken).ConfigureAwait(false);

        await WriteJsonAsync(
                Path.Combine(evidenceDir, "04-migration-plan.json"),
                new { PlanSource = planSource, Plan = plan },
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(evidenceDir, "migration-plan.hash.txt"),
                plan.ContentHash,
                cancellationToken)
            .ConfigureAwait(false);

        // Reconfirm identity immediately before mutation.
        MikrotikDeviceInfo beforeApply = await deviceInfoReader
            .ReadDeviceInfoAsync(connection, targetIp.ToString(), cancellationToken)
            .ConfigureAwait(false);
        string? recheck = DescribeIdentityFailure(beforeApply, targetIp, expectedIdentity);
        if (recheck is not null
            || !string.Equals(beforeApply.Version, info.Version, StringComparison.Ordinal))
        {
            logger.LogError("Pre-mutation identity/version re-check failed: {Reason}", recheck ?? "version changed");
            ospf.DeletePlan(job);
            return 4;
        }

        logger.LogInformation(
            "Applying OSPF reconciliation. ManagementHost={MgmtHost}, PlanHash={Hash}",
            managementHost,
            plan.ContentHash);

        OspfApplyGateResult apply = await ospf
            .ApplyAndVerifyAsync(
                connection,
                job,
                info.Identity!,
                plan,
                cancellationToken,
                managementHost)
            .ConfigureAwait(false);

        await WriteJsonAsync(
                Path.Combine(evidenceDir, "05-apply-result.json"),
                new { apply.Succeeded, apply.FailureOutcome, apply.Detail },
                cancellationToken)
            .ConfigureAwait(false);

        if (!apply.Succeeded)
        {
            logger.LogError(
                "OSPF apply/verify failed. Outcome={Outcome}, Detail={Detail}",
                apply.FailureOutcome,
                apply.Detail);
            await WriteJsonAsync(
                    Path.Combine(evidenceDir, "post-failure-operational.json"),
                    await CaptureOperationalAsync(ops, connection, cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
            ospf.DeletePlan(job);
            await WriteTextAsync(
                    Path.Combine(evidenceDir, "verdict.txt"),
                    "FAILED — OSPF APPLY/VERIFY",
                    cancellationToken)
                .ConfigureAwait(false);
            return 6;
        }

        OperationalHealthResult opsHealth = await AssessOperationalHealthAsync(
                connection,
                plan,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(
                Path.Combine(evidenceDir, "06-operational-health.json"),
                opsHealth,
                cancellationToken)
            .ConfigureAwait(false);

        if (!opsHealth.Succeeded)
        {
            logger.LogError("OSPF operational health failed: {Detail}", opsHealth.Detail);
            ospf.DeletePlan(job);
            await WriteTextAsync(
                    Path.Combine(evidenceDir, "verdict.txt"),
                    "PARTIALLY COMPLETED — OSPF VERIFY REPORTED SUCCESS BUT OPERATIONAL HEALTH FAILED",
                    cancellationToken)
                .ConfigureAwait(false);
            return 7;
        }

        // Same production service as the upgrade executor; respects RedistributeSummaryviaStatics.
        // Pre-reconciliation backup already retains evidence before these mutations.
        OspfStaticSummaryRedistributionResult summary = await summaryRedistribution
            .ApplyAndVerifyAsync(connection, apply.Plan ?? plan, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(
                Path.Combine(evidenceDir, "06b-static-summary-redistribution.json"),
                new
                {
                    summary.Succeeded,
                    summary.Skipped,
                    summary.FailureOutcome,
                    summary.Detail,
                    summary.CompensationAttempted,
                    summary.CompensationSucceeded,
                    summary.CompensationDetail,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (summary.Skipped)
        {
            logger.LogInformation("{Detail}", summary.Detail);
        }
        else if (!summary.Succeeded)
        {
            logger.LogError(
                "OSPF static summary redistribution failed. Detail={Detail}",
                summary.Detail);
            await WriteJsonAsync(
                    Path.Combine(evidenceDir, "post-failure-operational.json"),
                    await CaptureOperationalAsync(ops, connection, cancellationToken).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
            // Preserve plan for retry/diagnosis (same semantics as the upgrade executor).
            await WriteTextAsync(
                    Path.Combine(evidenceDir, "verdict.txt"),
                    "FAILED — OSPF STATIC SUMMARY REDISTRIBUTION",
                    cancellationToken)
                .ConfigureAwait(false);
            return 9;
        }
        else
        {
            logger.LogInformation("OSPF static summary redistribution completed successfully");
        }

        object postOperational = await CaptureOperationalAsync(ops, connection, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(
                Path.Combine(evidenceDir, "07-post-reconciliation-operational.json"),
                postOperational,
                cancellationToken)
            .ConfigureAwait(false);

        MikrotikDeviceInfo postOspf = await deviceInfoReader
            .ReadDeviceInfoAsync(connection, targetIp.ToString(), cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation("Creating post-reconciliation ROS7 backup");
        DeviceBackupResult postBackup = await backups
            .CreatePreUpgradeBackupAsync(
                connection,
                job,
                postOspf,
                mikrotikOptions.Value.Username,
                mikrotikOptions.Value.Password,
                cancellationToken)
            .ConfigureAwait(false);

        await WriteJsonAsync(
                Path.Combine(evidenceDir, "08-post-reconciliation-backup.json"),
                DescribeBackup(postBackup),
                cancellationToken)
            .ConfigureAwait(false);

        if (!postBackup.Succeeded)
        {
            logger.LogError("Post-migration backup failed: {Reason}", postBackup.FailureReason);
            ospf.DeletePlan(job);
            await WriteTextAsync(
                    Path.Combine(evidenceDir, "verdict.txt"),
                    "PARTIALLY COMPLETED — OSPF VERIFIED BUT POST-MIGRATION BACKUP FAILED",
                    cancellationToken)
                .ConfigureAwait(false);
            return 8;
        }

        // RouterBOARD firmware is explicitly deferred — report status only; do not upgrade or reboot.
        await WriteJsonAsync(
                Path.Combine(evidenceDir, "09-routerboard-status-deferred.json"),
                new
                {
                    Deferred = true,
                    Reason = "RouterBOARD upgrade requires separate explicit authorisation.",
                    postOspf.RouterboardCurrentFirmware,
                    postOspf.RouterboardUpgradeFirmware,
                    postOspf.BoardName,
                    postOspf.RouterboardModel,
                },
                cancellationToken)
            .ConfigureAwait(false);

        MikrotikDeviceInfo finalInfo = await deviceInfoReader
            .ReadDeviceInfoAsync(connection, targetIp.ToString(), cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset endUtc = DateTimeOffset.UtcNow;
        await WriteJsonAsync(
                Path.Combine(evidenceDir, "10-final-device-info.json"),
                new
                {
                    finalInfo.Identity,
                    finalInfo.Version,
                    finalInfo.ArchitectureName,
                    finalInfo.BoardName,
                    finalInfo.RouterboardModel,
                    finalInfo.RouterboardCurrentFirmware,
                    finalInfo.RouterboardUpgradeFirmware,
                    StartUtc = startUtc,
                    EndUtc = endUtc,
                    job.ImportId,
                    job.JobId,
                    PlanSource = planSource,
                    plan.ContentHash,
                    PreBackupManifest = preBackup.ManifestLocalPath,
                    PostBackupManifest = postBackup.ManifestLocalPath,
                },
                cancellationToken)
            .ConfigureAwait(false);

        ospf.DeletePlan(job);

        await WriteTextAsync(
                Path.Combine(evidenceDir, "verdict.txt"),
                "Completed — OSPF-only recovery verified; pre/post backups verified; RouterBOARD deferred",
                cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "OSPF-only lab recovery completed successfully. EvidenceDir={EvidenceDir}, PreBackup={Pre}, PostBackup={Post}. RouterBOARD deferred (current={Current}, upgrade={Upgrade})",
            evidenceDir,
            preBackup.ManifestLocalPath,
            postBackup.ManifestLocalPath,
            finalInfo.RouterboardCurrentFirmware,
            finalInfo.RouterboardUpgradeFirmware);
        _ = preflightInventory;
        return 0;
    }

    private static object DescribeBackup(DeviceBackupResult backup) =>
        new
        {
            backup.Succeeded,
            backup.FailureReason,
            backup.ExportLocalPath,
            backup.BinaryLocalPath,
            backup.ManifestLocalPath,
            backup.RemoteCleanupSucceeded,
            backup.RemoteCleanupFailed,
            backup.RemainingRemoteFiles,
            Manifest = backup.Manifest is null
                ? null
                : new
                {
                    backup.Manifest.ExportSha256,
                    backup.Manifest.BinaryBackupSha256,
                    backup.Manifest.ExportSizeBytes,
                    backup.Manifest.BinaryBackupSizeBytes,
                    backup.Manifest.ExportFileName,
                    backup.Manifest.BinaryBackupFileName,
                    backup.Manifest.RemoteExportFileName,
                    backup.Manifest.RemoteBinaryFileName,
                    backup.Manifest.BackupTimestampUtc,
                    backup.Manifest.BackupCompletionStatus,
                },
        };

    private static string? DescribePlanTargetFailure(OspfMigrationPlan plan, string expectedIdentity)
    {
        if (!string.Equals(plan.RouterId, "10.16.0.11", StringComparison.Ordinal))
        {
            return $"Router ID '{plan.RouterId}' is not 10.16.0.11.";
        }

        if (!string.Equals(plan.InstanceName, OspfFleetNaming.Ros7InstanceName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(plan.TargetInstanceName, OspfFleetNaming.Ros7InstanceName, StringComparison.OrdinalIgnoreCase))
        {
            return $"Instance '{plan.InstanceName}' / target '{plan.TargetInstanceName}' is not default-v2.";
        }

        string expectedSite = OspfFleetNaming.Ros7SiteAreaName(expectedIdentity);
        if (!plan.Areas.Any(a =>
                a.IsBackbone
                && string.Equals(a.Name, OspfFleetNaming.Ros7BackboneAreaName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.AreaId, "0.0.0.0", StringComparison.Ordinal)))
        {
            return "Plan is missing backbone-v2 / 0.0.0.0.";
        }

        if (!plan.Areas.Any(a =>
                !a.IsBackbone
                && string.Equals(a.Name, expectedSite, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.AreaId, "10.16.0.11", StringComparison.Ordinal)))
        {
            return $"Plan is missing site area '{expectedSite}' / 10.16.0.11.";
        }

        if (!plan.PassiveLoopbacks.Any(l =>
                string.Equals(l.NetworkCidr, "10.16.0.11/32", StringComparison.Ordinal)
                || string.Equals(l.NetworkCidr, "10.16.0.11", StringComparison.Ordinal)))
        {
            return "Plan is missing Loopback 10.16.0.11/32.";
        }

        if (!plan.PassiveSiteNetworks.Any(s =>
                OspfSiteMatcherConsolidation.IsAllInterfaces(s.Interfaces)
                && OspfNetworkList.SetsEqual(
                    s.NetworkCidr,
                    "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12")))
        {
            return "Plan is missing consolidated site matcher 10.16.11.0/24,100.64.0.0/10,172.16.0.0/12.";
        }

        string[] requiredRanges = ["10.16.11.0/24", "100.99.76.16/28", "172.26.197.4/30"];
        foreach (string range in requiredRanges)
        {
            if (!plan.AreaRanges.Any(r =>
                    string.Equals(r.RangeCidr, range, StringComparison.Ordinal)
                    || string.Equals(NormalizeCidr(r.RangeCidr), NormalizeCidr(range), StringComparison.Ordinal)))
            {
                return $"Plan is missing required area-range '{range}'.";
            }
        }

        return null;
    }

    private static string NormalizeCidr(string? cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p) ? p.ToCidrString() : cidr?.Trim() ?? string.Empty;

    internal static string? DescribeGateFailure(FirmwareUpgradeOptions upgrade, MikroTikOptions mikrotik)
    {
        if (upgrade.Enabled)
        {
            return "FirmwareUpgrade:Enabled must be false for OSPF-only recovery.";
        }

        OspfLabTargetOptions pin = upgrade.OspfLabTarget ?? new OspfLabTargetOptions();
        if (string.IsNullOrWhiteSpace(pin.RouterIp) || string.IsNullOrWhiteSpace(pin.RouterIdentity))
        {
            return "OspfLabTarget RouterIp/RouterIdentity must be present in local configuration for target binding.";
        }

        if (!IPAddress.TryParse(pin.RouterIp.Trim(), out _))
        {
            return "OspfLabTarget RouterIp is not a valid IP address.";
        }

        if (!mikrotik.Enabled)
        {
            return "Mikrotik:Enabled must be true.";
        }

        if (string.IsNullOrWhiteSpace(mikrotik.Username) || string.IsNullOrEmpty(mikrotik.Password))
        {
            return "Mikrotik credentials are required.";
        }

        return null;
    }

    private static string? DescribeIdentityFailure(
        MikrotikDeviceInfo info,
        IPAddress expectedIp,
        string expectedIdentity)
    {
        if (!string.Equals(info.Identity, expectedIdentity, StringComparison.Ordinal))
        {
            return $"Identity mismatch: expected '{expectedIdentity}', observed '{info.Identity}'.";
        }

        // Connection host was the pin; surface architecture/board for diagnostics only.
        _ = expectedIp;
        return null;
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<MikroTikOptions>()
            .Bind(configuration.GetSection(MikroTikOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MikroTikOptions>, MikroTikOptionsValidator>();

        services
            .AddOptions<FirmwareUpgradeOptions>()
            .Bind(configuration.GetSection(FirmwareUpgradeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FirmwareUpgradeOptions>, FirmwareUpgradeOptionsValidator>();

        services
            .AddOptions<SerilogOptions>()
            .Bind(configuration.GetSection(SerilogOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DeviceLogIdentityRegistry>();
        services.AddSingleton<DeviceLogWriter>();
        services.AddHostedService(static sp => sp.GetRequiredService<DeviceLogWriter>());

        services.AddSingleton<IMikrotikConnectionFactory, Tik4netConnectionFactory>();
        services.AddSingleton<MikrotikApiClient>();
        services.AddSingleton<IMikrotikApiClient>(static sp => sp.GetRequiredService<MikrotikApiClient>());
        services.AddSingleton<IMikrotikDeviceInfoReader>(static sp => sp.GetRequiredService<MikrotikApiClient>());
        services.AddSingleton<IMikrotikRouterOperations>(static sp => sp.GetRequiredService<MikrotikApiClient>());
        services.AddSingleton<IMikrotikFileTransferClient, MikrotikScpFileTransferClient>();
        services.AddSingleton<IDeviceBackupService, DeviceBackupService>();
        services.AddSingleton<IOspfMikrotikOperations, OspfMikrotikOperations>();
        services.AddSingleton<IOspfMigrationPlanStore, OspfMigrationPlanStore>();
        services.AddSingleton<OspfV7Reconciler>();
        services.AddSingleton<OspfV7MigrationVerifier>();
        services.AddSingleton<IOspfMigrationService, OspfMigrationService>();
        services
            .AddOptions<OspfSummaryRedistributionOptions>()
            .Configure<IConfiguration>((opts, configuration) =>
            {
                opts.RedistributeSummaryviaStatics =
                    configuration.GetValue("RedistributeSummaryviaStatics", false);
            });
        services.AddSingleton<IOspfStaticSummaryRedistributionService, OspfStaticSummaryRedistributionService>();
        // Explicitly do NOT register RouterBOARD upgrade, IRouterOsV6UpgradeExecutor, scheduler, or CSV watchers.
    }

    private static async Task<object> CaptureOperationalAsync(
        IOspfMikrotikOperations ops,
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        OspfV7Inventory inventory = await ops.ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        int filters = await ops.CountRoutingFiltersAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<OspfIpAddressRow> addresses =
            await ops.ReadIpAddressInventoryAsync(connection, cancellationToken).ConfigureAwait(false);

        List<Dictionary<string, string>> neighbours = await PrintAsync(connection, "/routing/ospf/neighbor/print", cancellationToken)
            .ConfigureAwait(false);
        List<Dictionary<string, string>> routes = await PrintAsync(connection, "/ip/route/print", cancellationToken)
            .ConfigureAwait(false);

        return new
        {
            FilterCount = filters,
            inventory.Instances,
            inventory.Areas,
            inventory.AreaRanges,
            inventory.Templates,
            inventory.Filters,
            inventory.OspfV3Objects,
            Addresses = addresses,
            Neighbours = neighbours,
            Routes = routes.Take(80).ToList(),
        };
    }

    private static async Task<OperationalHealthResult> AssessOperationalHealthAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        List<string> errors = [];
        List<Dictionary<string, string>> neighbours =
            await PrintAsync(connection, "/routing/ospf/neighbor/print", cancellationToken).ConfigureAwait(false);
        List<Dictionary<string, string>> templates =
            await PrintAsync(connection, "/routing/ospf/interface-template/print", cancellationToken)
                .ConfigureAwait(false);
        List<Dictionary<string, string>> routes =
            await PrintAsync(connection, "/ip/route/print", cancellationToken).ConfigureAwait(false);

        Dictionary<string, string>? expected = neighbours.FirstOrDefault(n =>
            string.Equals(Get(n, "address"), "10.16.248.77", StringComparison.Ordinal)
            && string.Equals(Get(n, "router-id"), "198.18.255.253", StringComparison.Ordinal));

        if (expected is null)
        {
            errors.Add("Expected OSPF neighbour 10.16.248.77 / RID 198.18.255.253 was not found.");
        }
        else if (!string.Equals(Get(expected, "state"), "Full", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Expected neighbour state Full; observed '{Get(expected, "state")}'.");
        }

        bool backhaulOk = templates.Any(t =>
            !IsTruthy(Get(t, "disabled"))
            && !IsTruthy(Get(t, "passive"))
            && string.Equals(Get(t, "cost"), "10", StringComparison.Ordinal)
            && (Get(t, "networks")?.Contains("10.16.248.76", StringComparison.Ordinal) == true
                || Get(t, "interfaces")?.Contains("eth-01_BH01.Tommy-Shylock", StringComparison.Ordinal) == true));
        if (!backhaulOk)
        {
            errors.Add("Active backhaul template with cost 10 was not observed.");
        }

        bool loopbackPassive = templates.Any(t =>
            IsTruthy(Get(t, "passive"))
            && (Get(t, "networks")?.Contains("10.16.0.11", StringComparison.Ordinal) == true
                || string.Equals(Get(t, "interfaces"), "Loopback", StringComparison.OrdinalIgnoreCase)));
        if (!loopbackPassive)
        {
            errors.Add("Passive loopback template was not observed.");
        }

        // Site / PPPoE summaries: look for OSPF routes covering planned site ranges where present.
        foreach (OspfPlanAreaRange range in plan.AreaRanges)
        {
            bool seen = routes.Any(r =>
                (Get(r, "dst-address")?.StartsWith(range.RangeCidr.Split('/')[0], StringComparison.Ordinal) == true
                    || string.Equals(Get(r, "dst-address"), range.RangeCidr, StringComparison.Ordinal))
                && (Get(r, "ospf") is not null || Get(r, "immediate-gw") is not null || Get(r, "gateway") is not null));
            if (!seen)
            {
                // Soft observation — RouterOS 7 may encode summaries differently; record but don't hard-fail
                // unless neighbour itself failed. Promote to warning in detail.
                errors.Add($"Route observation: planned area-range '{range.RangeCidr}' not clearly present in /ip/route.");
            }
        }

        // If only soft route observations failed but neighbour/backhaul/loopback OK, treat routes as warnings
        // when neighbour is Full — still require neighbour + backhaul + loopback.
        bool hardFail = errors.Any(e =>
            e.Contains("neighbour", StringComparison.OrdinalIgnoreCase)
            || e.Contains("backhaul", StringComparison.OrdinalIgnoreCase)
            || e.Contains("loopback", StringComparison.OrdinalIgnoreCase));

        List<string> routeWarnings = errors
            .Where(e => e.StartsWith("Route observation:", StringComparison.Ordinal))
            .ToList();
        List<string> hardErrors = errors.Except(routeWarnings).ToList();

        return new OperationalHealthResult
        {
            Succeeded = hardErrors.Count == 0,
            Detail = hardErrors.Count == 0
                ? (routeWarnings.Count == 0
                    ? "Neighbour Full; backhaul cost 10; loopback passive."
                    : string.Join(" ", hardErrors.Concat(routeWarnings)))
                : string.Join(" ", hardErrors),
            NeighbourState = expected is null ? null : Get(expected, "state"),
            NeighbourAddress = expected is null ? null : Get(expected, "address"),
            NeighbourRouterId = expected is null ? null : Get(expected, "router-id"),
            RouteWarnings = routeWarnings,
            NeighbourCount = neighbours.Count,
        };
    }

    private static async Task<List<Dictionary<string, string>>> PrintAsync(
        ITikConnection connection,
        string path,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand(path)
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(static r => r.Words.ToDictionary(static kv => kv.Key, static kv => kv.Value)).ToList();
    }

    private static string? Get(Dictionary<string, string> row, string key) =>
        row.TryGetValue(key, out string? value) ? value : null;

    private static bool IsTruthy(string? value) =>
        value is not null
        && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value == "" // ROS7 often uses empty flag presence for passive
            || value == "1");

    private static Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

    private static Task WriteTextAsync(string path, string value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, value, cancellationToken);

    private sealed class OperationalHealthResult
    {
        public bool Succeeded { get; init; }
        public string? Detail { get; init; }
        public string? NeighbourState { get; init; }
        public string? NeighbourAddress { get; init; }
        public string? NeighbourRouterId { get; init; }
        public int NeighbourCount { get; init; }
        public IReadOnlyList<string> RouteWarnings { get; init; } = [];
    }
}
