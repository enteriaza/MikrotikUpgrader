using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Disposable lab harness: capture → plan → ApplyAndVerifyAsync → stop.
/// Does NOT invoke OspfOnlyLabRecoveryRunner (which continues to backup/RouterBOARD).
/// </summary>
public sealed class TommyOspfApplyOnlyHarness
{
    public const string EvidenceRootEnv = "TOMMY_OSPF_EVIDENCE_DIR";

    [Fact]
    [Trait("Category", "LiveTommyOspfApplyOnly")]
    public async Task Execute_ApplyAndVerifyOnly_OnTommyLab()
    {
        // Fail closed before probe/API — silent return previously let mis-filtered runs pass without opt-in.
        LiveMikrotikTestGate.RequireArmed(
            LiveMikrotikTestGate.AllowTommyOspfApplyOnlyEnv,
            "LiveTommyOspfApplyOnly");

        string evidenceDir = Environment.GetEnvironmentVariable(EvidenceRootEnv)
            ?? Path.Combine(
                Path.GetTempPath(),
                "TommyOspfApplyOnly",
                DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(evidenceDir);

        string probeLog = Path.Combine(evidenceDir, "external-mgmt-probe.log");
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(15));
        using ProbeHost probe = ProbeHost.Start("198.18.240.11", probeLog);

        await Task.Delay(2000, cts.Token);
        if (!probe.HadSuccess || !probe.IsHealthy)
        {
            WriteVerdict(evidenceDir, "NOT EXECUTED — PRECONDITIONS NOT MET", "External management probe not healthy before mutation.");
            Assert.Fail("External management probe not established.");
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        string workerDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "MikrotikWorker"));
        builder.Configuration
            .AddJsonFile(Path.Combine(workerDir, "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(workerDir, "appsettings.Development.json"), optional: true)
            .AddJsonFile(Path.Combine(workerDir, "appsettings.Development.local.json"), optional: true);

        ConfigureServices(builder.Services, builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        using IHost host = builder.Build();
        IOptions<FirmwareUpgradeOptions> upgrade = host.Services.GetRequiredService<IOptions<FirmwareUpgradeOptions>>();
        IOptions<MikroTikOptions> mikrotik = host.Services.GetRequiredService<IOptions<MikroTikOptions>>();

        if (upgrade.Value.Enabled)
        {
            WriteVerdict(evidenceDir, "NOT EXECUTED — PRECONDITIONS NOT MET", "FirmwareUpgrade.Enabled must be false.");
            Assert.Fail("FirmwareUpgrade enabled");
        }

        IPAddress targetIp = IPAddress.Parse("198.18.240.11");
        const string expectedIdentity = "BR01.POP011.016.Tommy";

        IMikrotikConnectionFactory factory = host.Services.GetRequiredService<IMikrotikConnectionFactory>();
        IMikrotikDeviceInfoReader deviceInfo = host.Services.GetRequiredService<IMikrotikDeviceInfoReader>();
        IOspfMigrationService ospf = host.Services.GetRequiredService<IOspfMigrationService>();
        IOspfMikrotikOperations ops = host.Services.GetRequiredService<IOspfMikrotikOperations>();
        ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TommyOspfApplyOnly");

        MikrotikConnectionOptions connOpts = new()
        {
            Host = targetIp.ToString(),
            Username = mikrotik.Value.Username,
            Password = mikrotik.Value.Password,
            Port = mikrotik.Value.Port,
        };

        DateTimeOffset start = DateTimeOffset.Now;
        string executionId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDir, "execution-meta.json"),
            JsonSerializer.Serialize(new { executionId, start, target = targetIp.ToString(), expectedIdentity }, Indented),
            cts.Token);

        using ITikConnection connection = await factory.CreateAsync(connOpts, cts.Token);
        MikrotikDeviceInfo info = await deviceInfo.ReadDeviceInfoAsync(connection, targetIp.ToString(), cts.Token);

        await WriteJson(evidenceDir, "device-info.json", new
        {
            info.Identity,
            info.Version,
            info.ArchitectureName,
            info.BoardName,
            info.RouterboardModel,
        });

        if (!string.Equals(info.Identity, expectedIdentity, StringComparison.Ordinal)
            || !RouterOsVersion.TryParse(info.Version, out RouterOsVersion ver)
            || ver.Major != 7 || ver.Minor != 23 || ver.Patch != 8
            || !string.Equals(info.ArchitectureName, "mipsbe", StringComparison.OrdinalIgnoreCase))
        {
            WriteVerdict(evidenceDir, "NOT EXECUTED — PRECONDITIONS NOT MET", "Identity/version/architecture mismatch.");
            Assert.Fail($"Identity/version/arch mismatch: {info.Identity} {info.Version} {info.ArchitectureName}");
        }

        // Baseline operational snapshot (read-only).
        OspfV7Inventory inventory = await ops.ReadV7InventoryAsync(connection, cts.Token);
        IReadOnlyList<OspfIpAddressRow> addresses = await ops.ReadIpAddressInventoryAsync(connection, cts.Token);
        IReadOnlyList<Dictionary<string, string>> neighbours = await ops.ReadOspfNeighboursAsync(connection, cts.Token);
        int filterCount = await ops.CountRoutingFiltersAsync(connection, cts.Token);
        List<Dictionary<string, string>> routes = await PrintAsync(connection, "/ip/route/print", cts.Token);

        await WriteJson(evidenceDir, "pre-inventory.json", new
        {
            filterCount,
            inventory.Instances,
            inventory.Areas,
            inventory.AreaRanges,
            inventory.Templates,
            inventory.OspfV3Objects,
            addresses,
            neighbours,
            routes,
        });

        OspfInterfaceTemplateRow? bhLive = inventory.Templates.FirstOrDefault(t =>
            !t.Disabled
            && !t.Passive
            && string.Equals(t.Type, "ptp", StringComparison.OrdinalIgnoreCase)
            && (t.Networks?.Contains("/30", StringComparison.Ordinal) ?? false));

        Dictionary<string, string>? fullNbr = neighbours.FirstOrDefault(n =>
            string.Equals(Get(n, "state"), "Full", StringComparison.OrdinalIgnoreCase));

        await WriteJson(evidenceDir, "pre-backbone.json", new
        {
            Backbone = bhLive,
            FullNeighbour = fullNbr,
            Probe = probe.Snapshot(),
        });

        if (bhLive is null || fullNbr is null)
        {
            WriteVerdict(evidenceDir, "NOT EXECUTED — PRECONDITIONS NOT MET", "Missing active backbone PTP or Full neighbour.");
            Assert.Fail("Backbone/neighbour preconditions failed.");
        }

        if (!probe.IsHealthy)
        {
            WriteVerdict(evidenceDir, "NOT EXECUTED — PRECONDITIONS NOT MET", "Probe failed before plan.");
            Assert.Fail("Probe unhealthy.");
        }

        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "tommy-ospf-apply-only",
            RowNumber = 2,
            RouterName = expectedIdentity,
            RouterIp = targetIp,
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(2),
        };

        logger.LogInformation("CaptureAndValidate starting. ExecutionId={ExecutionId}", executionId);
        OspfGateResult capture = await ospf.CaptureAndValidateAsync(
            connection,
            job,
            info.Identity!,
            info.Version,
            info.ArchitectureName,
            persistPlan: false,
            cts.Token);

        OspfMigrationPlan? plan = capture.Plan;
        string planSource = "live-v7-capture";

        bool liveIncomplete = plan is null
            || !capture.Succeeded
            || plan.PassiveSiteNetworks.Count == 0
            || plan.AreaRanges.Count == 0
            || !plan.PassiveSiteNetworks.Any(static s =>
                OspfSiteMatcherConsolidation.IsAllInterfaces(s.Interfaces)
                && (s.NetworkCidr.Contains("100.64.0.0/10", StringComparison.Ordinal)
                    || s.NetworkCidr.Contains("172.16.0.0/12", StringComparison.Ordinal)
                    || s.NetworkCidr.Contains("/16", StringComparison.Ordinal)
                    || s.Comment?.Contains("PPPoE", StringComparison.OrdinalIgnoreCase) == true));

        if (liveIncomplete)
        {
            logger.LogWarning("Live capture incomplete; rebuilding from pre-upgrade .rsc reference + live inventory.");
            string[] exportCandidates =
            [
                Path.Combine(workerDir, "bin", "Release", "net10.0", "Backups", "Exports"),
                Path.Combine(workerDir, "bin", "Debug", "net10.0", "Backups", "Exports"),
                Path.Combine(workerDir, "Backups", "Exports"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Backups", "Exports")),
            ];

            string? referencePath = null;
            string? exportsDirUsed = null;
            foreach (string exportsDir in exportCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(exportsDir))
                {
                    continue;
                }

                referencePath = OspfLabRecoveryPlanBuilder.FindPreUpgradeExportPath(
                    exportsDir,
                    expectedIdentity,
                    targetIp.ToString());
                if (referencePath is not null)
                {
                    exportsDirUsed = exportsDir;
                    break;
                }
            }

            await WriteJson(evidenceDir, "recovery-reference-lookup.json", new
            {
                exportCandidates,
                exportsDirUsed,
                referencePath,
                capture.Succeeded,
                capture.Detail,
            });

            if (referencePath is null)
            {
                WriteVerdict(
                    evidenceDir,
                    "NOT EXECUTED — PRECONDITIONS NOT MET",
                    "No pre-upgrade .rsc with OSPF networks/area-ranges found in candidate export directories.");
                Assert.Fail("Missing recovery reference export.");
            }

            OspfPlanValidationResult recovery = OspfLabRecoveryPlanBuilder.BuildFromLiveAndReference(
                job.ImportId,
                job.JobId,
                targetIp,
                info.Identity!,
                info.Version,
                info.ArchitectureName,
                inventory,
                addresses,
                referencePath);

            await WriteJson(evidenceDir, "recovery-plan-validation.json", new
            {
                recovery.Succeeded,
                recovery.Plan.ValidationErrors,
                recovery.Plan.NormalisationNotes,
            });

            if (!recovery.Succeeded)
            {
                WriteVerdict(evidenceDir, "NOT EXECUTED — PRECONDITIONS NOT MET", string.Join("; ", recovery.Plan.ValidationErrors));
                Assert.Fail("Recovery plan validation failed.");
            }

            plan = recovery.Plan;
            planSource = "live-v7-inventory+pre-upgrade-rsc-reference";
        }

        if (plan is null || !plan.IsValid)
        {
            WriteVerdict(evidenceDir, "NOT EXECUTED — PRECONDITIONS NOT MET", "Plan invalid after construction.");
            Assert.Fail("Invalid plan.");
        }

        // Area unset uncertainty — record current area flags before apply.
        await WriteJson(evidenceDir, "pre-area-flags.json", inventory.Areas.Select(a => new
        {
            a.Name,
            a.AreaId,
            a.Type,
            a.NoSummaries,
            a.DefaultCost,
            Planned = plan.Areas.FirstOrDefault(p =>
                string.Equals(p.Name, a.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.AreaId, a.AreaId, StringComparison.Ordinal)),
        }));

        await WriteJson(evidenceDir, "migration-plan.json", new
        {
            PlanSource = planSource,
            plan.ContentHash,
            plan.InstanceName,
            plan.RouterId,
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            plan.ValidationErrors,
            plan.NormalisationNotes,
        });
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, "migration-plan.hash.txt"), plan.ContentHash, cts.Token);

        logger.LogInformation(
            "Plan ready. Source={Source}, Hash={Hash}, BH={Bh}, Loop={Loop}, Sites={Sites}, Ranges={Ranges}",
            planSource,
            plan.ContentHash,
            plan.ActiveBackhauls.Count,
            plan.PassiveLoopbacks.Count,
            plan.PassiveSiteNetworks.Count,
            plan.AreaRanges.Count);

        if (!probe.IsHealthy)
        {
            WriteVerdict(evidenceDir, "NOT EXECUTED — PRECONDITIONS NOT MET", "Probe failed immediately before apply.");
            Assert.Fail("Probe failed before apply.");
        }

        // Identity re-check immediately before mutation.
        MikrotikDeviceInfo beforeApply = await deviceInfo.ReadDeviceInfoAsync(connection, targetIp.ToString(), cts.Token);
        if (!string.Equals(beforeApply.Identity, expectedIdentity, StringComparison.Ordinal)
            || !string.Equals(beforeApply.Version, info.Version, StringComparison.Ordinal))
        {
            WriteVerdict(evidenceDir, "NOT EXECUTED — PRECONDITIONS NOT MET", "Pre-apply identity/version changed.");
            Assert.Fail("Pre-apply identity/version changed.");
        }

        logger.LogInformation("ApplyAndVerifyAsync starting. ProbeFailuresSoFar={Failures}", probe.FailureCount);
        DateTimeOffset applyStart = DateTimeOffset.Now;
        OspfApplyGateResult apply = await ospf.ApplyAndVerifyAsync(
            connection,
            job,
            info.Identity!,
            plan,
            cts.Token);
        DateTimeOffset applyEnd = DateTimeOffset.Now;

        await WriteJson(evidenceDir, "apply-result.json", new
        {
            apply.Succeeded,
            apply.FailureOutcome,
            apply.Detail,
            applyStart,
            applyEnd,
            Probe = probe.Snapshot(),
        });

        // Post-state snapshot (read-only).
        OspfV7Inventory postInv = await ops.ReadV7InventoryAsync(connection, cts.Token);
        IReadOnlyList<Dictionary<string, string>> postNbr = await ops.ReadOspfNeighboursAsync(connection, cts.Token);
        await WriteJson(evidenceDir, "post-inventory.json", new
        {
            postInv.Instances,
            postInv.Areas,
            postInv.AreaRanges,
            postInv.Templates,
            postInv.OspfV3Objects,
            Neighbours = postNbr,
            Probe = probe.Snapshot(),
        });

        ospf.DeletePlan(job);

        bool probeOk = probe.IsHealthy && probe.HadSuccess && !probe.HadFailure;
        // Allow brief single-packet blips only if last samples are healthy — fail if any failure recorded during apply window.
        probeOk = probe.FailureCount == 0 && probe.HadSuccess;

        if (!apply.Succeeded)
        {
            string outcome = apply.FailureOutcome is UpgradeOutcome.OspfMigrationApplyFailed
                ? "OSPF MIGRATION ABORTED — SAFETY CONDITION FAILED"
                : "OSPF MIGRATION PARTIALLY APPLIED — MANUAL REVIEW REQUIRED";
            WriteVerdict(evidenceDir, outcome, apply.Detail ?? "Apply/verify failed.");
            Assert.Fail(apply.Detail ?? "Apply failed");
        }

        if (!probeOk)
        {
            WriteVerdict(
                evidenceDir,
                "OSPF MIGRATION PARTIALLY APPLIED — MANUAL REVIEW REQUIRED",
                $"Apply reported success but external probe failures={probe.FailureCount}.");
            Assert.Fail("External probe failed during/after apply.");
        }

        bool stillFull = postNbr.Any(n =>
            string.Equals(Get(n, "state"), "Full", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Get(n, "address"), Get(fullNbr, "address"), StringComparison.Ordinal));

        if (!stillFull)
        {
            WriteVerdict(
                evidenceDir,
                "OSPF MIGRATION PARTIALLY APPLIED — MANUAL REVIEW REQUIRED",
                "Verifier passed but Full neighbour not observed in post snapshot.");
            Assert.Fail("Neighbour not Full after apply.");
        }

        DateTimeOffset end = DateTimeOffset.Now;
        await WriteJson(evidenceDir, "execution-complete.json", new { executionId, start, end, planSource, plan.ContentHash });
        WriteVerdict(evidenceDir, "OSPF MIGRATION SUCCEEDED — ALL SAFETY CHECKS PASSED", null);
        logger.LogInformation("Apply-and-verify completed successfully. Evidence={Dir}", evidenceDir);
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MikroTikOptions>()
            .Bind(configuration.GetSection(MikroTikOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MikroTikOptions>, MikroTikOptionsValidator>();

        services.AddOptions<FirmwareUpgradeOptions>()
            .Bind(configuration.GetSection(FirmwareUpgradeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FirmwareUpgradeOptions>, FirmwareUpgradeOptionsValidator>();

        services.AddOptions<SerilogOptions>()
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
        services.AddSingleton<IOspfMikrotikOperations, OspfMikrotikOperations>();
        services.AddSingleton<IOspfMigrationPlanStore, OspfMigrationPlanStore>();
        services.AddSingleton<OspfV7Reconciler>();
        services.AddSingleton<OspfV7MigrationVerifier>();
        services.AddSingleton<IOspfMigrationService, OspfMigrationService>();
        // Intentionally NO backup service, NO RouterBOARD, NO upgrade executor.
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static async Task WriteJson(string dir, string name, object value) =>
        await File.WriteAllTextAsync(Path.Combine(dir, name), JsonSerializer.Serialize(value, Indented));

    private static void WriteVerdict(string dir, string verdict, string? detail)
    {
        File.WriteAllText(
            Path.Combine(dir, "verdict.txt"),
            detail is null ? verdict : $"{verdict}{Environment.NewLine}{detail}");
    }

    private static string? Get(Dictionary<string, string>? d, string key) =>
        d is not null && d.TryGetValue(key, out string? v) ? v : null;

    private static async Task<List<Dictionary<string, string>>> PrintAsync(
        ITikConnection connection,
        string command,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand(command)
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(static r => r.Words.ToDictionary(static kv => kv.Key, static kv => kv.Value)).ToList();
    }

    private sealed class ProbeHost : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly object _gate = new();
        private int _success;
        private int _failure;
        private DateTimeOffset? _lastSuccess;
        private DateTimeOffset? _lastFailure;
        private string? _lastError;

        private ProbeHost(string host, string logPath)
        {
            _loop = Task.Run(async () =>
            {
                await using StreamWriter writer = new(logPath) { AutoFlush = true };
                using Ping ping = new();
                while (!_cts.IsCancellationRequested)
                {
                    DateTimeOffset ts = DateTimeOffset.Now;
                    try
                    {
                        PingReply reply = await ping.SendPingAsync(host, 1000);
                        if (reply.Status == IPStatus.Success)
                        {
                            lock (_gate)
                            {
                                _success++;
                                _lastSuccess = ts;
                            }

                            await writer.WriteLineAsync($"{ts:o} OK ttl={reply.Options?.Ttl} rtt={reply.RoundtripTime}ms");
                        }
                        else
                        {
                            lock (_gate)
                            {
                                _failure++;
                                _lastFailure = ts;
                                _lastError = reply.Status.ToString();
                            }

                            await writer.WriteLineAsync($"{ts:o} FAIL status={reply.Status}");
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (_gate)
                        {
                            _failure++;
                            _lastFailure = ts;
                            _lastError = ex.GetType().Name + ": " + ex.Message;
                        }

                        await writer.WriteLineAsync($"{ts:o} FAIL ex={ex.GetType().Name}");
                    }

                    try
                    {
                        await Task.Delay(500, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        public static ProbeHost Start(string host, string logPath) => new(host, logPath);

        public bool HadSuccess
        {
            get { lock (_gate) return _success > 0; }
        }

        public bool HadFailure
        {
            get { lock (_gate) return _failure > 0; }
        }

        public int FailureCount
        {
            get { lock (_gate) return _failure; }
        }

        public bool IsHealthy
        {
            get
            {
                lock (_gate)
                {
                    return _success > 0
                           && _lastSuccess is not null
                           && (_lastFailure is null || _lastSuccess > _lastFailure);
                }
            }
        }

        public object Snapshot()
        {
            lock (_gate)
            {
                return new
                {
                    SuccessCount = _success,
                    FailureCount = _failure,
                    LastSuccess = _lastSuccess,
                    LastFailure = _lastFailure,
                    LastError = _lastError,
                    IsHealthy,
                };
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
            }

            _cts.Dispose();
        }
    }
}
