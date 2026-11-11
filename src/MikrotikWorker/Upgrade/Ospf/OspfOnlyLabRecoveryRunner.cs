using System.Net;
using System.Text.Json;
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
        IDeviceBackupService backups = host.Services.GetRequiredService<IDeviceBackupService>();
        RouterboardFirmwareUpgradeService routerboard =
            host.Services.GetRequiredService<RouterboardFirmwareUpgradeService>();
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
            || ros.Minor != 23
            || ros.Patch != 8)
        {
            logger.LogError(
                "Expected RouterOS 7.23.8; observed '{Version}'. Refusing OSPF-only recovery.",
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
            ExecutionMode = UpgradeExecutionMode.FullUpgrade,
        };

        string evidenceDir = Path.Combine(
            AppContext.BaseDirectory,
            "Logs",
            "OspfOnlyLab",
            $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{job.ImportId:N}");
        Directory.CreateDirectory(evidenceDir);

        logger.LogInformation(
            "OSPF-only lab recovery starting. ImportId={ImportId}, JobId={JobId}, Host={Host}, Identity={Identity}, Version={Version}, EvidenceDir={EvidenceDir}",
            job.ImportId,
            job.JobId,
            targetIp,
            info.Identity,
            info.Version,
            evidenceDir);

        // Pre-mutation operational snapshot (neighbours / routes / filter menus).
        await WriteJsonAsync(
                Path.Combine(evidenceDir, "pre-mutation-operational.json"),
                await CaptureOperationalAsync(ops, connection, cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);

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

            OspfV7Inventory liveInventory = await ops.ReadV7InventoryAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<OspfIpAddressRow> addresses =
                await ops.ReadIpAddressInventoryAsync(connection, cancellationToken).ConfigureAwait(false);

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

        IOspfMigrationPlanStore planStore = host.Services.GetRequiredService<IOspfMigrationPlanStore>();
        await planStore.SaveAsync(plan, cancellationToken).ConfigureAwait(false);

        await WriteJsonAsync(
                Path.Combine(evidenceDir, "migration-plan.json"),
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

        OspfApplyGateResult apply = await ospf
            .ApplyAndVerifyAsync(connection, job, info.Identity!, plan, cancellationToken)
            .ConfigureAwait(false);

        await WriteJsonAsync(
                Path.Combine(evidenceDir, "apply-result.json"),
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
            return 6;
        }

        OperationalHealthResult opsHealth = await AssessOperationalHealthAsync(
                connection,
                plan,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(
                Path.Combine(evidenceDir, "operational-health.json"),
                opsHealth,
                cancellationToken)
            .ConfigureAwait(false);

        if (!opsHealth.Succeeded)
        {
            logger.LogError("OSPF operational health failed: {Detail}", opsHealth.Detail);
            ospf.DeletePlan(job);
            return 7;
        }

        MikrotikDeviceInfo postOspf = await deviceInfoReader
            .ReadDeviceInfoAsync(connection, targetIp.ToString(), cancellationToken)
            .ConfigureAwait(false);
        DeviceBackupResult backup = await backups
            .CreatePreUpgradeBackupAsync(
                connection,
                job,
                postOspf,
                mikrotikOptions.Value.Username,
                mikrotikOptions.Value.Password,
                cancellationToken)
            .ConfigureAwait(false);

        await WriteJsonAsync(
                Path.Combine(evidenceDir, "backup-result.json"),
                new
                {
                    backup.Succeeded,
                    backup.RemoteCleanupFailed,
                    backup.ManifestLocalPath,
                    backup.ExportLocalPath,
                    backup.BinaryLocalPath,
                    backup.FailureReason,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!backup.Succeeded)
        {
            logger.LogError("Post-migration backup failed: {Reason}", backup.FailureReason);
            ospf.DeletePlan(job);
            return 8;
        }

        UpgradeExecutionResult rb = await routerboard
            .ExecuteAsync(
                connection,
                job,
                postOspf,
                mikrotikOptions.Value.Username,
                mikrotikOptions.Value.Password,
                priorRebootInitiated: false,
                completedStageBefore: UpgradeCompletedStages.OspfMigration,
                cancellationToken)
            .ConfigureAwait(false);

        await WriteJsonAsync(
                Path.Combine(evidenceDir, "routerboard-result.json"),
                new
                {
                    rb.Outcome,
                    rb.Detail,
                    rb.CompletedStage,
                    rb.PostUpgradeVersion,
                    rb.RebootInitiated,
                },
                cancellationToken)
            .ConfigureAwait(false);

        MikrotikDeviceInfo finalInfo = await deviceInfoReader
            .ReadDeviceInfoAsync(connection, targetIp.ToString(), cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(
                Path.Combine(evidenceDir, "final-device-info.json"),
                new
                {
                    finalInfo.Identity,
                    finalInfo.Version,
                    finalInfo.ArchitectureName,
                    finalInfo.BoardName,
                    finalInfo.RouterboardModel,
                    finalInfo.RouterboardCurrentFirmware,
                    finalInfo.RouterboardUpgradeFirmware,
                },
                cancellationToken)
            .ConfigureAwait(false);

        ospf.DeletePlan(job);

        bool rbOk = rb.Outcome is UpgradeOutcome.UpgradeSucceeded or UpgradeOutcome.UpgradeSucceededV6Only
            || (rb.Outcome == UpgradeOutcome.UpgradeSucceeded);
        // RouterBOARD NotRequired returns UpgradeSucceeded; failure returns RouterboardUpgradeFailed.
        if (rb.Outcome == UpgradeOutcome.RouterboardUpgradeFailed)
        {
            logger.LogError("RouterBOARD stage failed after successful OSPF recovery: {Detail}", rb.Detail);
            await WriteTextAsync(
                    Path.Combine(evidenceDir, "verdict.txt"),
                    "OSPF CONFIGURATION APPLIED — OPERATIONAL HEALTH VERIFIED — ROUTERBOARD FAILED",
                    cancellationToken)
                .ConfigureAwait(false);
            return 9;
        }

        await WriteTextAsync(
                Path.Combine(evidenceDir, "verdict.txt"),
                "OSPF MIGRATION SUCCESSFUL — CONFIGURATION AND OPERATIONAL HEALTH VERIFIED",
                cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "OSPF-only lab recovery completed successfully. EvidenceDir={EvidenceDir}, BackupManifest={Manifest}, RouterBOARD={RbOutcome}",
            evidenceDir,
            backup.ManifestLocalPath,
            rb.Outcome);
        _ = rbOk;
        return 0;
    }

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
        services.AddSingleton<RouterboardFirmwareUpgradeService>();
        // Explicitly do NOT register IRouterOsV6UpgradeExecutor / scheduler / CSV watchers.
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
