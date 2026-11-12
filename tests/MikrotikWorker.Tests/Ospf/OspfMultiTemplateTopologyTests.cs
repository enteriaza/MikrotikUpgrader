using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Fake-based coverage for topology-independent OSPF plans: multiple backhauls, multiple site
/// matchers, provenance-gated KnownLegacy, and mutation-free repeated reconciliation.
/// </summary>
public sealed class OspfMultiTemplateTopologyTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task MultiBackhaul_CleanupAndReconcile_PreservesAllActivePtpCost10(int backhaulCount)
    {
        OspfMigrationPlan plan = MultiBackhaulPlan(backhaulCount, siteCount: 1);
        MutableStore store = MutableStore.WithPlan(plan);
        CapturingConnection capturing = store.Connect();
        OspfMikrotikOperations ops = CreateOps();

        await ops.RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);
        Assert.Empty(capturing.RemovedIds);
        Assert.Equal(plan.ActiveBackhauls.Count + plan.PassiveLoopbacks.Count + plan.PassiveSiteNetworks.Count, store.Templates.Count);

        RecordingOps recording = new(ops) { PlanForInventory = plan };
        OspfV7Reconciler reconciler = new(recording, NullLogger<OspfV7Reconciler>.Instance);
        await reconciler.ApplyAsync(capturing, plan, "10.0.0.1", CancellationToken.None);
        int sets = capturing.SetCount;
        int adds = capturing.AddCount;
        int removes = capturing.RemovedIds.Count;
        string[] ids = store.Templates.Select(t => t[".id"]).OrderBy(static x => x).ToArray();

        await reconciler.ApplyAsync(capturing, plan, "10.0.0.1", CancellationToken.None);
        await reconciler.ApplyAsync(capturing, plan, "10.0.0.1", CancellationToken.None);

        Assert.Equal(adds, capturing.AddCount);
        Assert.Equal(sets, capturing.SetCount);
        Assert.Equal(removes, capturing.RemovedIds.Count);
        Assert.Equal(ids, store.Templates.Select(t => t[".id"]).OrderBy(static x => x).ToArray());
        Assert.All(
            plan.ActiveBackhauls,
            bh =>
            {
                Dictionary<string, string> row = store.Templates.Single(t =>
                    t["interfaces"] == bh.Interface && t["networks"] == bh.NetworkCidr);
                Assert.Equal("ptp", row["type"]);
                Assert.Equal("10", row["cost"]);
                Assert.False(row.ContainsKey("passive"));
                Assert.Equal("false", row["disabled"]);
            });
    }

    [Fact]
    public async Task MultiSiteMatchers_AllPlannedSurviveCleanup()
    {
        OspfMigrationPlan plan = ClonePlan(
            MultiBackhaulPlan(backhaulCount: 1, siteCount: 0),
            sites:
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "site",
                    Interfaces = "LAN-A",
                    NetworkCidr = "10.10.1.0/24",
                    Passive = true,
                },
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "site",
                    Interfaces = "LAN-B",
                    NetworkCidr = "10.10.2.0/24",
                    Passive = true,
                },
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "site",
                    Interfaces = "all",
                    NetworkCidr = "100.64.0.0/10",
                    Passive = true,
                },
            ],
            superseded: []);

        MutableStore store = MutableStore.WithPlan(plan);
        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Empty(capturing.RemovedIds);
        Assert.Equal(5, store.Templates.Count); // 1 bh + 1 loop + 3 sites
        Assert.Contains(store.Templates, t => t["interfaces"] == "LAN-A");
        Assert.Contains(store.Templates, t => t["interfaces"] == "LAN-B");
        Assert.Contains(store.Templates, t => t["interfaces"] == "all" && t["networks"] == "100.64.0.0/10");
    }

    [Fact]
    public async Task MissingPlannedBackhaul_AbortsWithZeroDeletes()
    {
        OspfMigrationPlan plan = MultiBackhaulPlan(2, 1);
        MutableStore store = MutableStore.WithPlan(plan);
        store.Templates.RemoveAll(t => t["interfaces"] == plan.ActiveBackhauls[1].Interface);
        CapturingConnection capturing = store.Connect();

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
    }

    [Fact]
    public async Task DuplicateBackhaulIdentity_AbortsWithZeroDeletes()
    {
        OspfMigrationPlan plan = MultiBackhaulPlan(1, 1);
        MutableStore store = MutableStore.WithPlan(plan);
        store.Templates.Add(CloneRow(store.Templates[0], "*DUP"));
        CapturingConnection capturing = store.Connect();

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
    }

    [Fact]
    public async Task UnexpectedActiveTemplate_AbortsWithZeroDeletes()
    {
        OspfMigrationPlan plan = MultiBackhaulPlan(1, 1);
        MutableStore store = MutableStore.WithPlan(plan);
        store.Templates.Add(Row("*U", "backbone-v2", "ether99", "10.255.0.0/30", passive: false, type: "ptp", cost: "10"));
        CapturingConnection capturing = store.Connect();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
    }

    [Fact]
    public async Task ProvenanceLegacy_Deleted_OperatorCoveredPrefix_NotDeleted()
    {
        OspfMigrationPlan plan = ClonePlan(
            MultiBackhaulPlan(1, 1),
            superseded:
            [
                OspfTemplateSemanticIdentity.FromCaptured(
                    "site",
                    "vlan-legacy",
                    "10.16.11.0/27",
                    passive: true,
                    new OspfCapturedTemplateSemantics { AreaInstanceName = "default-v2" }),
            ]);

        MutableStore store = MutableStore.WithPlan(plan);
        store.Templates.Add(LegacyRow("*L", "vlan-legacy", "10.16.11.0/27"));
        store.Templates.Add(LegacyRow("*OP", "VLAN-200", "10.16.11.128/26"));
        CapturingConnection capturing = store.Connect();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*OP");
        Assert.Contains(store.Templates, t => t[".id"] == "*L");
    }

    [Fact]
    public async Task UnknownAppearsOnSubsequentRun_AbortsWithoutDelete()
    {
        OspfMigrationPlan plan = ClonePlan(
            MultiBackhaulPlan(1, 1),
            superseded:
            [
                OspfTemplateSemanticIdentity.FromCaptured(
                    "site",
                    "vlan10",
                    "10.16.11.0/27",
                    passive: true,
                    new OspfCapturedTemplateSemantics { AreaInstanceName = "default-v2" }),
            ]);
        MutableStore store = MutableStore.WithPlan(plan);
        store.Templates.Add(LegacyRow("*L", "vlan10", "10.16.11.0/27"));
        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);
        Assert.Equal(["*L"], capturing.RemovedIds);

        capturing.RemovedIds.Clear();
        store.Templates.Add(Row("*U", "site", "operator", "10.16.11.64/27", passive: true));
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
    }

    [Fact]
    public void PlannerConsolidation_RecordsSupersededProvenance()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = System.Net.IPAddress.Parse("10.0.0.1"),
            Identity = "R1",
            RouterOsVersion = "6.49.23",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            Instances = [new OspfInstanceRow { Name = "default", RouterId = "10.0.0.11", Version = "2" }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Type = "default" },
                new OspfAreaRow { Name = "site", AreaId = "10.0.0.11", Type = "stub", DefaultCost = "1" },
            ],
            Networks =
            [
                new OspfNetworkRow { Area = "backbone", Network = "10.0.0.0/30" },
                new OspfNetworkRow { Area = "backbone", Network = "10.0.0.11/32" },
                new OspfNetworkRow { Area = "site", Network = "10.16.11.0/27", Comment = "mgmt" },
                new OspfNetworkRow { Area = "site", Network = "100.64.1.0/24" },
            ],
            Interfaces =
            [
                new OspfInterfaceRow { Interface = "ether1", NetworkType = "ptp", Passive = false },
            ],
            IpAddresses =
            [
                new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
                new OspfIpAddressRow { Address = "10.0.0.11/32", Network = "10.0.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "Bridge-VLAN-99" },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow { Area = "site", Range = "10.16.11.0/24" },
            ],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.NotEmpty(result.Plan.SupersededTemplates);
        Assert.Contains(
            result.Plan.SupersededTemplates,
            s => OspfNetworkList.SetsEqual(s.NetworkCidr, "10.16.11.0/27")
                 || OspfNetworkList.SetsEqual(s.NetworkCidr, "100.64.1.0/24"));
        Assert.Single(result.Plan.PassiveSiteNetworks);
        Assert.True(OspfSiteMatcherConsolidation.IsAllInterfaces(result.Plan.PassiveSiteNetworks[0].Interfaces));
    }

    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    private static OspfMigrationPlan ClonePlan(
        OspfMigrationPlan plan,
        IReadOnlyList<OspfPlanSiteNetworkTemplate>? sites = null,
        IReadOnlyList<OspfSupersededTemplateIdentity>? superseded = null) =>
        new()
        {
            ImportId = plan.ImportId,
            JobId = plan.JobId,
            RouterIp = plan.RouterIp,
            Identity = plan.Identity,
            SourceRouterOsVersion = plan.SourceRouterOsVersion,
            Architecture = plan.Architecture,
            SourceKind = plan.SourceKind,
            ContentHash = plan.ContentHash,
            InstanceName = plan.InstanceName,
            RouterId = plan.RouterId,
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = sites ?? plan.PassiveSiteNetworks,
            SupersededTemplates = superseded ?? plan.SupersededTemplates,
            ValidationErrors = plan.ValidationErrors,
            NormalisationNotes = plan.NormalisationNotes,
        };

    private static OspfMigrationPlan MultiBackhaulPlan(int backhaulCount, int siteCount)
    {
        List<OspfPlanBackhaulTemplate> backhauls = [];
        for (int i = 0; i < backhaulCount; i++)
        {
            backhauls.Add(new OspfPlanBackhaulTemplate
            {
                AreaName = "backbone-v2",
                Interface = $"eth-BH{i}",
                NetworkCidr = $"10.16.{i}.0/30",
                Cost = 10,
                Type = "ptp",
                Passive = false,
            });
        }

        List<OspfPlanSiteNetworkTemplate> sites = [];
        for (int i = 0; i < siteCount; i++)
        {
            sites.Add(new OspfPlanSiteNetworkTemplate
            {
                AreaName = "site",
                Interfaces = "all",
                NetworkCidr = i == 0 ? "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12" : $"10.20.{i}.0/24",
                Passive = true,
            });
        }

        return new OspfMigrationPlan
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = "10.0.0.1",
            Identity = "LAB",
            SourceRouterOsVersion = "7.23.8",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            InstanceName = "default-v2",
            RouterId = "10.16.0.11",
            TargetInstanceName = OspfFleetNaming.Ros7InstanceName,
            TargetBackboneAreaName = OspfFleetNaming.Ros7BackboneAreaName,
            TargetSiteAreaName = "site",
            LoopbackInterfaceName = "Loopback",
            LoopbackCidr = "10.16.0.11/32",
            DerivedManagementCidr = "10.16.11.0/24",
            Areas =
            [
                new OspfPlanArea { Name = "backbone-v2", AreaId = "0.0.0.0", TargetType = "default", IsBackbone = true },
                new OspfPlanArea
                {
                    Name = "site",
                    AreaId = "10.16.0.11",
                    TargetType = "stub",
                    DefaultCost = "1",
                    IsBackbone = false,
                },
            ],
            ActiveBackhauls = backhauls,
            PassiveLoopbacks =
            [
                new OspfPlanLoopbackTemplate
                {
                    AreaName = "backbone-v2",
                    Interface = "Loopback",
                    NetworkCidr = "10.16.0.11/32",
                    Passive = true,
                },
            ],
            PassiveSiteNetworks = sites,
            SupersededTemplates = [],
            AreaRanges = [],
            ValidationErrors = [],
            NormalisationNotes = [],
            ContentHash = "multi",
        };
    }

    private static Dictionary<string, string> LegacyRow(string id, string interfaces, string networks) =>
        Row(id, "site", interfaces, networks, passive: true, type: "broadcast", cost: "1", priority: "128", useBfd: "false");

    private static Dictionary<string, string> Row(
        string id,
        string area,
        string interfaces,
        string networks,
        bool passive,
        string? type = null,
        string? cost = null,
        bool disabled = false,
        string? priority = null,
        string? useBfd = null)
    {
        Dictionary<string, string> d = new()
        {
            [".id"] = id,
            ["area"] = area,
            ["networks"] = networks,
            ["disabled"] = disabled ? "true" : "false",
        };
        if (!string.IsNullOrEmpty(interfaces))
        {
            d["interfaces"] = interfaces;
        }

        if (passive)
        {
            d["passive"] = "";
        }

        if (type is not null)
        {
            d["type"] = type;
        }

        if (cost is not null)
        {
            d["cost"] = cost;
        }

        if (priority is not null)
        {
            d["priority"] = priority;
        }

        if (useBfd is not null)
        {
            d["use-bfd"] = useBfd;
        }

        return d;
    }

    private static Dictionary<string, string> CloneRow(Dictionary<string, string> source, string newId)
    {
        Dictionary<string, string> copy = new(source, StringComparer.Ordinal) { [".id"] = newId };
        return copy;
    }

    private sealed class MutableStore
    {
        public List<Dictionary<string, string>> Templates { get; } = [];
        public List<Dictionary<string, string>> Areas { get; } = [];

        public static MutableStore WithPlan(OspfMigrationPlan plan)
        {
            MutableStore store = new();
            foreach (OspfPlanArea area in plan.Areas)
            {
                store.Areas.Add(new Dictionary<string, string>
                {
                    [".id"] = $"*A-{area.Name}",
                    ["name"] = area.Name,
                    ["instance"] = plan.InstanceName,
                    ["area-id"] = area.AreaId,
                });
            }

            int n = 1;
            foreach (OspfPlanBackhaulTemplate bh in plan.ActiveBackhauls)
            {
                store.Templates.Add(Row($"*B{n++}", bh.AreaName, bh.Interface, bh.NetworkCidr, passive: false, type: "ptp", cost: "10"));
            }

            foreach (OspfPlanLoopbackTemplate loop in plan.PassiveLoopbacks)
            {
                store.Templates.Add(Row($"*L{n++}", loop.AreaName, loop.Interface ?? "Loopback", loop.NetworkCidr, passive: true));
            }

            foreach (OspfPlanSiteNetworkTemplate site in plan.PassiveSiteNetworks)
            {
                store.Templates.Add(Row($"*S{n++}", site.AreaName, site.Interfaces, site.NetworkCidr, passive: true));
            }

            return store;
        }

        public CapturingConnection Connect()
        {
            CapturingConnection c = new(this);
            c.Attach();
            return c;
        }
    }

    private sealed class CapturingConnection : DisposeTrackingConnection
    {
        private readonly MutableStore _store;
        private readonly TikFakeConnection _fake;
        private ITikCommand? _pendingRemove;
        private int _removeOrdinal;

        public CapturingConnection(MutableStore store)
            : this(store, new TikFakeConnection())
        {
        }

        private CapturingConnection(MutableStore store, TikFakeConnection fake)
            : base(fake)
        {
            _store = store;
            _fake = fake;
        }

        public List<string> RemovedIds { get; } = [];
        public int AddCount { get; private set; }
        public int SetCount { get; private set; }
        public int UnsetCount { get; private set; }
        public int? FailRemoveAtOrdinal { get; set; }

        public void Attach()
        {
            _fake.WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    [
                        .._store.Templates.Select(static r => new TikFakeReSentence(new Dictionary<string, string>(r))),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/remove",
                    _ =>
                    {
                        _removeOrdinal++;
                        string? id = _pendingRemove?.Parameters.FirstOrDefault(p => p.Name == ".id")?.Value;
                        if (FailRemoveAtOrdinal == _removeOrdinal)
                        {
                            throw new InvalidOperationException($"Simulated remove failure for {id}");
                        }

                        RemovedIds.Add(id!);
                        _store.Templates.RemoveAll(t => t[".id"] == id);
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/add",
                    _ =>
                    {
                        AddCount++;
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/set",
                    _ =>
                    {
                        SetCount++;
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/unset",
                    _ =>
                    {
                        UnsetCount++;
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/instance/print",
                    static _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*i",
                            ["name"] = "default-v2",
                            ["version"] = "2",
                            ["router-id"] = "10.16.0.11",
                        }),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/area/print",
                    _ =>
                    [
                        .._store.Areas.Select(static a => new TikFakeReSentence(new Dictionary<string, string>(a))),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(static rows => rows.First() == "/routing/ospf/area/range/print", static _ => [new TikFakeDoneSentence()])
                .WithResponse(static rows => rows.First() == "/routing/ospf/neighbor/print", static _ => [new TikFakeDoneSentence()])
                .WithResponse(static rows => rows.First() == "/routing/ospf/interface/print", static _ => [new TikFakeDoneSentence()])
                .WithResponse(static rows => rows.First().StartsWith("/routing/filter", StringComparison.Ordinal), static _ => [new TikFakeDoneSentence()])
                .WithResponse(static rows => rows.First() == "/ip/route/print", static _ => [new TikFakeDoneSentence()])
                .WithResponse(static rows => rows.First() == "/system/resource/print", static _ =>
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["version"] = "7.23.8" }),
                    new TikFakeDoneSentence(),
                ]);
        }

        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            if (commandText.Equals("/routing/ospf/interface-template/remove", StringComparison.Ordinal))
            {
                _pendingRemove = cmd;
            }

            return cmd;
        }

        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            if (commandText.Equals("/routing/ospf/interface-template/remove", StringComparison.Ordinal))
            {
                _pendingRemove = cmd;
            }

            return cmd;
        }
    }

    private sealed class RecordingOps(OspfMikrotikOperations real) : IOspfMikrotikOperations
    {
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) =>
            real.EnsureBackhaulTemplateAsync(connection, template, cancellationToken);
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            real.EnsureLoopbackTemplateAsync(connection, template, protectedBackhauls, cancellationToken);
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            real.EnsureSiteNetworkTemplateAsync(connection, template, protectedBackhauls, cancellationToken);

        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            real.AssertSiteNetworkTemplateStateAsync(connection, template, cancellationToken);
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            real.RemoveObsoleteTemplatesAsync(connection, plan, cancellationToken);
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Minimal(managementHost));
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public OspfMigrationPlan PlanForInventory { get; set; } = MultiBackhaulPlan(1, 1);

        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.InventoryMatchingPlan(PlanForInventory));
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));
    }
}
