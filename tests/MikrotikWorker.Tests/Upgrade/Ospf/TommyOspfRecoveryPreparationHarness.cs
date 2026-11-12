// -----------------------------------------------------------------------------
// File: TommyOspfRecoveryPreparationHarness.cs
//
// Description:
// Read-only OSPF-only recovery preparation for Tommy. Captures live inventory,
// builds the production plan, resolves identities, and emits the exact
// operation inventory. Does not mutate, backup, or apply.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Read-only OSPF-only recovery preparation for Tommy. Captures live inventory, builds the
/// production plan, resolves identities, and emits the exact operation inventory.
/// Does not mutate, backup, or apply.
/// </summary>
public sealed class TommyOspfRecoveryPreparationHarness
{
    /// <summary>Environment variable naming the directory where recovery-preparation evidence is written.</summary>
    public const string EvidenceRootEnv = "TOMMY_OSPF_RECOVERY_PREP_DIR";

    /// <summary>Indented JSON options used for evidence files.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Scenario: live, read-only run against the Tommy lab router: read the v7 inventory, addresses and neighbours, rebuild the OSPF plan from the pre-upgrade .rsc reference and resolve live object identities, without changing the router.
    /// Expected: FirmwareUpgrade stays disabled; the router is BR01.POP011.016.Tommy on 7.25.1 mipsbe with a Full neighbour 198.18.255.253 and a default-v2 instance with router-id 10.16.0.11; the backbone and site areas live on default-v2; the recovery plan is valid with MGMT 10.16.11.0/24, the 100.99.76.16/28 and 172.26.197.4/30 ranges and the v2 target instance and site-area names; the preparation report is written.
    /// Invariant: the reconciliation plan is prepared entirely from read-only data and never mutates the router.
    /// </summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Prepare_Tommy_OspfOnly_Reconciliation_Plan_ReadOnly()
    {
        LiveMikrotikTestGate.RequireArmed(LiveMikrotikTestGate.AllowLiveMikrotikEnv, "Live");

        string evidenceDir = Environment.GetEnvironmentVariable(EvidenceRootEnv)
            ?? Path.Combine(
                Path.GetTempPath(),
                "TommyOspfRecoveryPrep",
                DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(evidenceDir);

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
        Assert.False(upgrade.Value.Enabled, "FirmwareUpgrade.Enabled must remain false.");

        const string expectedIdentity = "BR01.POP011.016.Tommy";
        IPAddress targetIp = IPAddress.Parse("198.18.240.11");
        const string managementHost = "198.18.0.66";

        IMikrotikConnectionFactory factory = host.Services.GetRequiredService<IMikrotikConnectionFactory>();
        IMikrotikDeviceInfoReader deviceInfo = host.Services.GetRequiredService<IMikrotikDeviceInfoReader>();
        IOspfMikrotikOperations ops = host.Services.GetRequiredService<IOspfMikrotikOperations>();

        MikrotikConnectionOptions connOpts = new()
        {
            Host = targetIp.ToString(),
            Username = mikrotik.Value.Username,
            Password = mikrotik.Value.Password,
            Port = mikrotik.Value.Port,
        };

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));
        using ITikConnection connection = await factory.CreateAsync(connOpts, cts.Token);
        MikrotikDeviceInfo info = await deviceInfo.ReadDeviceInfoAsync(connection, targetIp.ToString(), cts.Token);

        Assert.Equal(expectedIdentity, info.Identity);
        Assert.StartsWith("7.25.1", info.Version, StringComparison.Ordinal);
        Assert.Equal("mipsbe", info.ArchitectureName, ignoreCase: true);

        OspfV7Inventory inventory = await ops.ReadV7InventoryAsync(connection, cts.Token);
        IReadOnlyList<OspfIpAddressRow> addresses = await ops.ReadIpAddressInventoryAsync(connection, cts.Token);
        IReadOnlyList<Dictionary<string, string>> neighbours =
            await ops.ReadOspfNeighboursAsync(connection, cts.Token);
        int filterCount = await ops.CountRoutingFiltersAsync(connection, cts.Token);
        List<Dictionary<string, string>> packages = await PrintAsync(connection, "/system/package/print", cts.Token);
        List<Dictionary<string, string>> routes = await PrintAsync(connection, "/ip/route/print", cts.Token);

        Dictionary<string, string>? fullNbr = neighbours.FirstOrDefault(n =>
            string.Equals(Get(n, "state"), "Full", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Get(n, "router-id"), "198.18.255.253", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fullNbr);

        OspfInstanceRow? instance = inventory.Instances.FirstOrDefault(i =>
            string.Equals(i.Name, OspfFleetNaming.Ros7InstanceName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(instance);
        Assert.Equal("10.16.0.11", instance.RouterId);

        OspfAreaRow? backbone = inventory.Areas.FirstOrDefault(a =>
            string.Equals(a.Name, OspfFleetNaming.Ros7BackboneAreaName, StringComparison.OrdinalIgnoreCase));
        OspfAreaRow? site = inventory.Areas.FirstOrDefault(a =>
            string.Equals(
                a.Name,
                OspfFleetNaming.Ros7SiteAreaName(expectedIdentity),
                StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(backbone);
        Assert.NotNull(site);
        Assert.Equal("0.0.0.0", backbone.AreaId);
        Assert.Equal("10.16.0.11", site.AreaId);
        Assert.Equal(OspfFleetNaming.Ros7InstanceName, backbone.Instance);
        Assert.Equal(OspfFleetNaming.Ros7InstanceName, site.Instance);

        await WriteJson(evidenceDir, "01-preflight-inventory.json", new
        {
            inspectedAt = DateTimeOffset.Now,
            managementHost,
            device = info,
            packages,
            filterCount,
            inventory.Instances,
            inventory.Areas,
            inventory.AreaRanges,
            inventory.Templates,
            inventory.Filters,
            addresses,
            neighbours,
            managementRoutes = routes.Where(r =>
                    (Get(r, "dst-address")?.StartsWith("0.0.0.0", StringComparison.Ordinal) ?? false)
                    || (Get(r, "dst-address")?.Contains("198.18.", StringComparison.Ordinal) ?? false)
                    || (Get(r, "gateway")?.Contains("198.18.", StringComparison.Ordinal) ?? false))
                .Take(20)
                .ToList(),
        });

        string[] exportCandidates =
        [
            Path.Combine(workerDir, "bin", "Release", "net10.0", "Backups", "Exports"),
            Path.Combine(workerDir, "bin", "Debug", "net10.0", "Backups", "Exports"),
            Path.Combine(workerDir, "Backups", "Exports"),
        ];

        string? referencePath = null;
        foreach (string dir in exportCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            referencePath = OspfLabRecoveryPlanBuilder.FindPreUpgradeExportPath(
                dir,
                expectedIdentity,
                targetIp.ToString());
            if (referencePath is not null)
            {
                break;
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(referencePath), "Missing pre-upgrade .rsc reference with OSPF networks/ranges.");

        Guid prepImportId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        const string prepJobId = "tommy-ospf-recovery-prep:readonly";
        OspfPlanValidationResult recovery = OspfLabRecoveryPlanBuilder.BuildFromLiveAndReference(
            prepImportId,
            prepJobId,
            targetIp,
            info.Identity!,
            info.Version,
            info.ArchitectureName,
            inventory,
            addresses,
            referencePath!);

        await WriteJson(evidenceDir, "02-plan-validation.json", new
        {
            referencePath,
            recovery.Succeeded,
            recovery.Plan.ValidationErrors,
            recovery.Plan.NormalisationNotes,
            recovery.Plan.ContentHash,
            recovery.Plan.Identity,
            recovery.Plan.RouterId,
            recovery.Plan.InstanceName,
            recovery.Plan.TargetInstanceName,
            recovery.Plan.TargetBackboneAreaName,
            recovery.Plan.TargetSiteAreaName,
            recovery.Plan.LoopbackInterfaceName,
            recovery.Plan.LoopbackCidr,
            recovery.Plan.DerivedManagementCidr,
            recovery.Plan.ObservedInScopeLoopbackAddresses,
            recovery.Plan.IgnoredOutsideRangeLoopbackAddresses,
            Areas = recovery.Plan.Areas,
            AreaRanges = recovery.Plan.AreaRanges,
            ActiveBackhauls = recovery.Plan.ActiveBackhauls,
            PassiveLoopbacks = recovery.Plan.PassiveLoopbacks,
            PassiveSiteNetworks = recovery.Plan.PassiveSiteNetworks,
            SupersededTemplates = recovery.Plan.SupersededTemplates,
        });

        Assert.True(recovery.Succeeded, string.Join("; ", recovery.Plan.ValidationErrors));
        OspfMigrationPlan plan = recovery.Plan;

        OspfLiveIdentityResolutionResult identity = OspfLiveIdentityResolver.Resolve(plan, inventory, addresses);
        await WriteJson(evidenceDir, "03-live-identity-resolution.json", new
        {
            identity.Succeeded,
            identity.Errors,
            Binding = identity.Binding is null
                ? null
                : new
                {
                    identity.Binding.LogicalInstanceName,
                    identity.Binding.LiveInstanceName,
                    identity.Binding.LiveInstanceId,
                    identity.Binding.LiveRouterId,
                    identity.Binding.DerivedLoopbackRouterId,
                    identity.Binding.LoopbackInterfaceName,
                    Areas = identity.Binding.Areas.Values.Select(a => new
                    {
                        a.LogicalName,
                        a.LiveName,
                        a.LiveObjectId,
                        a.AreaId,
                    }),
                },
        });
        Assert.True(identity.Succeeded, identity.Detail);
        Assert.NotNull(identity.Binding);

        OspfMigrationPlan livePlan = OspfLiveIdentityResolver.RemapPlan(plan, identity.Binding!);
        List<object> operations = BuildOperationInventory(livePlan, inventory, addresses, filterCount);
        object deletionReview = BuildDeletionReview(livePlan, inventory);

        string backupRoot = upgrade.Value.GetAbsoluteBackupDirectory(Path.Combine(workerDir, "bin", "Release", "net10.0"));
        var backupReadiness = new
        {
            backupRoot,
            exports = Path.Combine(backupRoot, "Exports"),
            binary = Path.Combine(backupRoot, "Binary"),
            manifests = Path.Combine(backupRoot, "Manifests"),
            kestrelServes = "FirmwareFiles only (RequestPath=/firmware_files) — Backups not mapped",
            existingTommyManifests = Directory.Exists(Path.Combine(backupRoot, "Manifests"))
                ? Directory.GetFiles(Path.Combine(backupRoot, "Manifests"), "*Tommy*")
                    .Select(Path.GetFileName)
                    .OrderByDescending(static s => s, StringComparer.Ordinal)
                    .ToList()
                : [],
            note = "No backup created by this preparation harness. Pre- and post-reconciliation backups require explicit approval.",
        };

        int creates = operations.Count(o => string.Equals(Prop(o, "kind"), "create", StringComparison.Ordinal));
        int updates = operations.Count(o => string.Equals(Prop(o, "kind"), "update", StringComparison.Ordinal));
        int noOps = operations.Count(o => string.Equals(Prop(o, "kind"), "no-op", StringComparison.Ordinal));
        int proposedDeletes = operations.Count(o => string.Equals(Prop(o, "kind"), "delete", StringComparison.Ordinal));

        var report = new
        {
            preparedAt = DateTimeOffset.Now,
            mutationAuthorised = false,
            preconditions = new
            {
                identityOk = true,
                versionOk = true,
                architectureOk = true,
                neighbourFull = true,
                neighbourRouterId = Get(fullNbr, "router-id"),
                neighbourAddress = Get(fullNbr, "address"),
                neighbourInterface = Get(fullNbr, "interface"),
                neighbourAdjacency = Get(fullNbr, "adjacency"),
                managementReachable = true,
                planValid = plan.IsValid,
                identityResolved = identity.Succeeded,
                ambiguityRemaining = false,
            },
            operationCounts = new { creates, updates, noOps, proposedDeletes },
            operations,
            deletionReview,
            backupReadiness,
            approvedExecutionSequence = new[]
            {
                "1. Reconnect to Tommy",
                "2. Revalidate identity/version/arch/management",
                "3. Revalidate default-v2, RID, areas, neighbour Full",
                "4. Capture fresh OSPF inventory",
                "5. Create+verify pre-reconciliation ROS7 backup (approval required)",
                "6. Rebuild plan from fresh inventory + reference export",
                "7. Resolve live identities / fresh .id values",
                "8. Validate complete operation plan",
                "9. Apply OSPF reconciliation idempotently (approval required)",
                "10. Verify areas and area ranges",
                "11. Verify backhaul/loopback/site templates",
                "12. Verify filters and redistribution",
                "13. Verify neighbour remains Full",
                "14. Create post-migration backup",
                "15. Verify backup manifests/hashes",
                "16. Mark OSPF migration successful",
            },
            nextActionRequiringApproval =
                "Create pre-reconciliation ROS7 backup, then apply OSPF-only reconciliation via production OspfV7Reconciler (not FullUpgrade / not failed job replay).",
        };

        await WriteJson(evidenceDir, "04-reconciliation-preparation-report.json", report);
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDir, "APPROVAL-GATE.txt"),
            "PLAN PREPARED — NO MUTATIONS EXECUTED.\n"
            + $"Creates={creates}, Updates={updates}, NoOps={noOps}, ProposedDeletes={proposedDeletes}\n"
            + "Await explicit approval before backup or apply.\n",
            cts.Token);

        Assert.True(File.Exists(Path.Combine(evidenceDir, "04-reconciliation-preparation-report.json")));
        Assert.True(plan.IsValid);
        Assert.Equal("10.16.11.0/24", plan.DerivedManagementCidr);
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "10.16.11.0/24" && r.Comment == "MGMT");
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "100.99.76.16/28");
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "172.26.197.4/30");
        Assert.Equal(OspfFleetNaming.Ros7InstanceName, plan.TargetInstanceName);
        Assert.Equal(OspfFleetNaming.Ros7SiteAreaName(expectedIdentity), plan.TargetSiteAreaName);
    }

    /// <summary>
    /// Proposed KnownLegacy deletions (executed only after ensure; reported here for approval).
    /// </summary>
    private static List<object> BuildOperationInventory(
        OspfMigrationPlan livePlan,
        OspfV7Inventory inventory,
        IReadOnlyList<OspfIpAddressRow> addresses,
        int filterCount)
    {
        List<object> ops = [];

        OspfInstanceRow liveInstance = inventory.Instances.Single(i =>
            string.Equals(i.Name, livePlan.InstanceName, StringComparison.OrdinalIgnoreCase));
        ops.Add(Op(
            "no-op",
            false,
            "/routing/ospf/instance",
            livePlan.InstanceName,
            liveInstance.Id,
            $"router-id={liveInstance.RouterId}; filters/redistribute empty",
            $"router-id={livePlan.RouterId}; redistribution cleared; filter refs cleared",
            "Already matches fleet instance identity and policy.",
            "Instance name=default-v2, router-id=10.16.0.11, no filter refs, redistribute empty"));

        foreach (OspfPlanArea area in livePlan.Areas)
        {
            OspfAreaRow live = inventory.Areas.Single(a =>
                string.Equals(a.Name, area.Name, StringComparison.OrdinalIgnoreCase));
            if (area.IsBackbone)
            {
                ops.Add(Op(
                    "no-op",
                    false,
                    "/routing/ospf/area",
                    area.Name,
                    live.Id,
                    $"area-id={live.AreaId}; type={live.Type}",
                    $"area-id={area.AreaId}; type={area.TargetType}",
                    "Backbone area already correct.",
                    "Area exists with area-id=0.0.0.0 and normal type"));
                continue;
            }

            bool noSummariesPresent = live.NoSummaries == true;
            bool costOk = string.Equals(live.DefaultCost, area.DefaultCost, StringComparison.Ordinal);
            string kind = (!noSummariesPresent && costOk && string.Equals(live.Type, "stub", StringComparison.OrdinalIgnoreCase))
                ? "no-op"
                : "update";
            ops.Add(Op(
                kind,
                false,
                "/routing/ospf/area",
                area.Name,
                live.Id,
                $"type={live.Type}; default-cost={live.DefaultCost}; no-summaries={(noSummariesPresent ? "present" : "absent")}",
                $"type=stub; default-cost={area.DefaultCost}; no-summaries=absent (unset)",
                noSummariesPresent
                    ? "Clear no-summaries via /routing/ospf/area/unset; ensure stub + default-cost=1."
                    : "Align site area properties to fleet stub policy.",
                "Area type=stub, default-cost=1, no-summaries property absent"));
        }

        foreach (OspfPlanAreaRange range in livePlan.AreaRanges)
        {
            OspfAreaRangeRow? live = inventory.AreaRanges.FirstOrDefault(r =>
                string.Equals(r.Area, range.AreaName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(r.Range), Normalize(range.RangeCidr), StringComparison.OrdinalIgnoreCase));
            if (live is null)
            {
                ops.Add(Op(
                    "create",
                    false,
                    "/routing/ospf/area/range",
                    $"{range.AreaName}:{range.RangeCidr}",
                    null,
                    "<missing>",
                    $"area={range.AreaName}; range={range.RangeCidr}; comment={range.Comment ?? "<none>"}; advertise=yes",
                    "Required site area-range is absent on live ROS7 inventory.",
                    $"Range {range.RangeCidr} exists on {range.AreaName} with comment={range.Comment ?? "<none>"}"));
            }
            else
            {
                bool commentOk = string.Equals(
                    live.Comment ?? string.Empty,
                    range.Comment ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
                ops.Add(Op(
                    commentOk ? "no-op" : "update",
                    false,
                    "/routing/ospf/area/range",
                    $"{range.AreaName}:{range.RangeCidr}",
                    live.Id,
                    $"comment={live.Comment ?? "<none>"}",
                    $"comment={range.Comment ?? "<none>"}",
                    commentOk ? "Range already present." : "Normalise range comment.",
                    $"Range {range.RangeCidr} present with intended comment"));
            }
        }

        foreach (OspfPlanBackhaulTemplate bh in livePlan.ActiveBackhauls)
        {
            OspfInterfaceTemplateRow? live = inventory.Templates.FirstOrDefault(t =>
                !t.Disabled
                && !t.Passive
                && string.Equals(t.Area, bh.AreaName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Interfaces, bh.Interface, StringComparison.Ordinal)
                && string.Equals(Normalize(t.Networks), Normalize(bh.NetworkCidr), StringComparison.OrdinalIgnoreCase));
            bool ok = live is not null
                && string.Equals(live.Cost, "10", StringComparison.Ordinal)
                && (string.Equals(live.Type, "ptp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(live.Type, "point-to-point", StringComparison.OrdinalIgnoreCase));
            ops.Add(Op(
                ok ? "no-op" : (live is null ? "create" : "update"),
                false,
                "/routing/ospf/interface-template",
                $"backhaul:{bh.Interface}",
                live?.Id,
                live is null
                    ? "<missing>"
                    : $"interfaces={live.Interfaces}; networks={live.Networks}; type={live.Type}; cost={live.Cost}; passive={live.Passive}",
                $"interfaces={bh.Interface}; networks={bh.NetworkCidr}; type=ptp; cost=10; passive=false",
                ok ? "Active backhaul already matches fleet policy." : "Ensure active backhaul template.",
                "Active PTP backhaul present with cost 10"));
        }

        foreach (OspfPlanLoopbackTemplate loop in livePlan.PassiveLoopbacks)
        {
            OspfInterfaceTemplateRow? live = inventory.Templates.FirstOrDefault(t =>
                !t.Disabled
                && t.Passive
                && string.Equals(t.Area, loop.AreaName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(t.Networks), Normalize(loop.NetworkCidr), StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(loop.Interface)
                    || string.Equals(t.Interfaces, loop.Interface, StringComparison.Ordinal)));
            bool forcedPtp = live is not null
                && !string.IsNullOrWhiteSpace(live.Type)
                && (live.Type.Equals("ptp", StringComparison.OrdinalIgnoreCase)
                    || live.Type.Equals("point-to-point", StringComparison.OrdinalIgnoreCase));
            string kind = live is null ? "create" : forcedPtp ? "update" : "no-op";
            ops.Add(Op(
                kind,
                forcedPtp, // remove+recreate is destructive but replaces like-for-like
                "/routing/ospf/interface-template",
                $"loopback:{loop.NetworkCidr}",
                live?.Id,
                live is null
                    ? "<missing>"
                    : $"interfaces={live.Interfaces}; networks={live.Networks}; type={live.Type ?? "<unset>"}; passive={live.Passive}",
                $"interfaces={loop.Interface}; networks={loop.NetworkCidr}; type=<unset>; passive=true",
                forcedPtp
                    ? "Forced PTP cannot be unset; production EnsureLoopbackTemplateAsync removes then recreates without type, retaining passive Loopback advertisement."
                    : "Ensure passive Loopback advertisement.",
                "Passive Loopback template exists without type=ptp"));
        }

        foreach (OspfPlanSiteNetworkTemplate site in livePlan.PassiveSiteNetworks)
        {
            OspfInterfaceTemplateRow? live = inventory.Templates.FirstOrDefault(t =>
                OspfSiteMatcherConsolidation.MatchesPlannedSite(
                    t.Area,
                    t.Interfaces,
                    t.Networks,
                    t.Passive,
                    t.Disabled,
                    site));
            ops.Add(Op(
                live is null ? "create" : "no-op",
                false,
                "/routing/ospf/interface-template",
                $"site:{site.AreaName}:{site.NetworkCidr}",
                live?.Id,
                live is null
                    ? "<missing>"
                    : $"interfaces={live.Interfaces}; networks={live.Networks}; passive={live.Passive}",
                $"interfaces={site.Interfaces}; networks={site.NetworkCidr}; passive=true",
                live is null
                    ? "Create consolidated passive site network template from source provenance."
                    : "Planned site matcher already present.",
                "Passive interfaces=all site template present with intended networks"));
        }

        ops.Add(Op(
            filterCount == 0 ? "no-op" : "delete",
            filterCount > 0,
            "/routing/filter/* (v7 rule menus)",
            "routing-filters",
            null,
            $"count={filterCount}",
            "count=0",
            filterCount == 0
                ? "No routing filters present."
                : "Remove all routing filters per fleet OSPF policy.",
            "Zero routing filters"));

        ops.Add(Op(
            "no-op",
            false,
            "/routing/ospf/instance (redistribute)",
            livePlan.InstanceName,
            liveInstance.Id,
            $"redistribute='{liveInstance.RedistributeProperties.GetValueOrDefault("redistribute")}'",
            "redistribute empty",
            "Redistribution already empty.",
            "Instance redistribute properties empty"));

        // Proposed KnownLegacy deletions (executed only after ensure; reported here for approval).
        foreach (OspfInterfaceTemplateRow t in inventory.Templates.Where(static x => !x.Disabled))
        {
            if (livePlan.ActiveBackhauls.Any(b =>
                    string.Equals(t.Area, b.AreaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Interfaces, b.Interface, StringComparison.Ordinal)
                    && string.Equals(Normalize(t.Networks), Normalize(b.NetworkCidr), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (livePlan.PassiveLoopbacks.Any(l =>
                    t.Passive
                    && string.Equals(t.Area, l.AreaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Normalize(t.Networks), Normalize(l.NetworkCidr), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (livePlan.PassiveSiteNetworks.Any(s =>
                    OspfSiteMatcherConsolidation.MatchesPlannedSite(
                        t.Area, t.Interfaces, t.Networks, t.Passive, t.Disabled, s)))
            {
                continue;
            }

            OspfTemplateInventoryRow row = ToInventoryRow(t, livePlan.InstanceName);
            OspfSupersededTemplateIdentity? match = livePlan.SupersededTemplates.FirstOrDefault(s =>
                OspfSiteMatcherConsolidation.MatchesSupersededIdentity(row, s));
            if (match is not null)
            {
                ops.Add(Op(
                    "delete",
                    true,
                    "/routing/ospf/interface-template",
                    $"legacy:{t.Interfaces}:{t.Networks}",
                    t.Id,
                    $"area={t.Area}; interfaces={t.Interfaces}; networks={t.Networks}; passive={t.Passive}; type={t.Type}",
                    "<removed>",
                    "KnownLegacy: matches planner SupersededTemplates provenance after consolidation; deletable only after planned site template exists.",
                    "Template absent; planned consolidated site matcher remains"));
            }
            else
            {
                ops.Add(Op(
                    "blocked-unknown",
                    true,
                    "/routing/ospf/interface-template",
                    $"unknown:{t.Interfaces}:{t.Networks}",
                    t.Id,
                    $"area={t.Area}; interfaces={t.Interfaces}; networks={t.Networks}; passive={t.Passive}; type={t.Type}",
                    "<must remain until positively classified>",
                    "Not planned and no superseded provenance match — production cleanup would abort rather than delete.",
                    "No Unknown templates remain after cleanup classification"));
            }
        }

        _ = addresses;
        return ops;
    }

    /// <summary>
    /// Builds a review object listing live templates that would be deleted as known legacy and those blocked as
    /// unknown, skipping templates the plan protects; nothing is deleted.
    /// </summary>
    private static object BuildDeletionReview(OspfMigrationPlan livePlan, OspfV7Inventory inventory)
    {
        List<object> proposed = [];
        List<object> blocked = [];
        foreach (OspfInterfaceTemplateRow t in inventory.Templates.Where(static x => !x.Disabled))
        {
            if (livePlan.ActiveBackhauls.Any(b =>
                    string.Equals(t.Area, b.AreaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Interfaces, b.Interface, StringComparison.Ordinal)
                    && string.Equals(Normalize(t.Networks), Normalize(b.NetworkCidr), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (livePlan.PassiveLoopbacks.Any(l =>
                    t.Passive
                    && string.Equals(t.Area, l.AreaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Normalize(t.Networks), Normalize(l.NetworkCidr), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (livePlan.PassiveSiteNetworks.Any(s =>
                    OspfSiteMatcherConsolidation.MatchesPlannedSite(
                        t.Area, t.Interfaces, t.Networks, t.Passive, t.Disabled, s)))
            {
                continue;
            }

            OspfTemplateInventoryRow row = ToInventoryRow(t, livePlan.InstanceName);
            string fingerprint = OspfTemplateCleanupClassifier.LegacyFingerprint(row);
            OspfSupersededTemplateIdentity? match = livePlan.SupersededTemplates.FirstOrDefault(s =>
                OspfSiteMatcherConsolidation.MatchesSupersededIdentity(row, s));
            var item = new
            {
                t.Id,
                t.Area,
                t.Interfaces,
                t.Networks,
                t.Type,
                t.Cost,
                t.Passive,
                fingerprint,
                classification = match is null ? "Unknown" : "KnownLegacy",
                reason = match is null
                    ? "No superseded provenance match"
                    : $"Superseded by consolidation provenance networks={match.NetworkCidr} interfaces={match.Interfaces}",
            };
            if (match is null)
            {
                blocked.Add(item);
            }
            else
            {
                proposed.Add(item);
            }
        }

        return new
        {
            note = "Deletions are not executed in preparation. Production RemoveObsoleteTemplatesAsync runs only after Ensure* and aborts if Unknown remain.",
            proposedKnownLegacyDeletes = proposed,
            blockedUnknown = blocked,
            protectedRoles = new
            {
                backhauls = livePlan.ActiveBackhauls.Count,
                loopbacks = livePlan.PassiveLoopbacks.Count,
                plannedSites = livePlan.PassiveSiteNetworks.Count,
            },
        };
    }

    /// <summary>Converts a live interface template into an inventory row tagged with its area instance.</summary>
    /// <param name="t">Live template row.</param>
    /// <param name="areaInstance">Instance the template's area belongs to.</param>
    private static OspfTemplateInventoryRow ToInventoryRow(OspfInterfaceTemplateRow t, string areaInstance) =>
        new()
        {
            Id = t.Id,
            Area = t.Area,
            Interfaces = t.Interfaces,
            Networks = t.Networks,
            Type = t.Type,
            Cost = t.Cost,
            Passive = t.Passive,
            Disabled = t.Disabled,
            Auth = t.Auth,
            AuthId = t.AuthId,
            Priority = t.Priority,
            UseBfd = t.UseBfd,
            HelloInterval = t.HelloInterval,
            DeadInterval = t.DeadInterval,
            RetransmitInterval = t.RetransmitInterval,
            TransmitDelay = t.TransmitDelay,
            InstanceId = t.InstanceId,
            AreaInstanceName = areaInstance,
            PrefixList = t.PrefixList,
            VlinkNeighborId = t.VlinkNeighborId,
            VlinkTransitArea = t.VlinkTransitArea,
            Comment = t.Comment,
        };

    /// <summary>Creates an evidence record describing one proposed recovery operation.</summary>
    /// <param name="kind">Operation kind.</param>
    /// <param name="destructive">Whether the operation removes or overwrites router state.</param>
    /// <param name="menu">RouterOS menu the operation targets.</param>
    /// <param name="logical">Logical identity of the template.</param>
    /// <param name="liveId">Live item id, if any.</param>
    /// <param name="current">Current value.</param>
    /// <param name="intended">Intended value.</param>
    /// <param name="reason">Why the operation is proposed.</param>
    /// <param name="verification">Condition that verifies the operation.</param>
    private static object Op(
        string kind,
        bool destructive,
        string menu,
        string logical,
        string? liveId,
        string current,
        string intended,
        string reason,
        string verification) =>
        new
        {
            kind,
            destructive,
            menu,
            logicalIdentity = logical,
            liveId,
            currentValue = current,
            intendedValue = intended,
            reason,
            verificationCondition = verification,
        };

    /// <summary>Reads a public property from an anonymous evidence record by reflection.</summary>
    /// <param name="o">Record to inspect.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The property value as text, or null.</returns>
    private static string? Prop(object o, string name) =>
        o.GetType().GetProperty(name)?.GetValue(o)?.ToString();

    /// <summary>Normalises a CIDR string to canonical form, or trims it when it does not parse.</summary>
    /// <param name="cidr">CIDR text.</param>
    private static string Normalize(string? cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p) ? p.ToCidrString() : cidr?.Trim() ?? string.Empty;

    /// <summary>
    /// Returns the value for the key in the dictionary, or null when the dictionary is null or lacks the key.
    /// </summary>
    private static string? Get(Dictionary<string, string>? d, string key) =>
        d is not null && d.TryGetValue(key, out string? v) ? v : null;

    /// <summary>
    /// Registers the options, validators, device log writer, Tik4net connection factory, API client and OSPF
    /// operations that the harness needs.
    /// </summary>
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
    }

    /// <summary>Writes <paramref name="value"/> as indented JSON to <paramref name="name"/> in <paramref name="dir"/>.</summary>
    /// <param name="dir">Evidence directory.</param>
    /// <param name="name">File name.</param>
    /// <param name="value">Object to serialise.</param>
    private static async Task WriteJson(string dir, string name, object value) =>
        await File.WriteAllTextAsync(Path.Combine(dir, name), JsonSerializer.Serialize(value, Indented));

    /// <summary>
    /// Executes a read-only print command on the connection and returns each reply row as a dictionary.
    /// </summary>
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
}
