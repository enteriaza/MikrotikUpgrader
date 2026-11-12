using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Production-path coverage for same-area consolidated-prefix cleanup via
/// <see cref="OspfMikrotikOperations.RemoveObsoleteTemplatesAsync"/>.
/// </summary>
public sealed class OspfConsolidatedPrefixCleanupTests
{
    private static readonly (string Networks, string Interfaces)[] FiveMgmtSubnets =
    [
        ("10.16.11.0/27", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.32/27", "Bridge-VLAN-197_MGMT"),
        ("10.16.11.64/26", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.128/26", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.192/26", "Bridge-VLAN-99_MGMT"),
    ];

    [Fact]
    public async Task ProductionCleanup_ExactFiveTemplateRegression_SucceedsWithoutProvenance()
    {
        OspfMigrationPlan plan = Plan();
        Assert.Empty(plan.SupersededTemplates);

        MutableStore store = MutableStore.WithPlanned(plan);
        int i = 3;
        foreach ((string networks, string interfaces) in FiveMgmtSubnets)
        {
            store.Templates.Add(Row(
                $"*{i++}",
                "site-v2",
                interfaces,
                networks,
                type: "ptp",
                cost: "1",
                priority: "1"));
        }

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(5, capturing.RemovedIds.Count);
        Assert.Equal(3, store.Templates.Count);
        Assert.Contains(store.Templates, t => t[".id"] == "*A" && t["interfaces"] == "all");
        Assert.DoesNotContain(
            store.Templates,
            t => FiveMgmtSubnets.Any(s => t.GetValueOrDefault("networks") == s.Networks));
        Assert.True(capturing.PrintCount >= 2, "Cleanup must re-read inventory during/after deletion.");
    }

    [Fact]
    public async Task ProductionCleanup_DifferentInterfacesThanAll_StillDeleted()
    {
        OspfMigrationPlan plan = Plan();
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*3", "site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"));
        store.Templates.Add(Row("*4", "site-v2", "Bridge-VLAN-197_MGMT", "10.16.11.32/27"));

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(2, capturing.RemovedIds.Count);
        Assert.DoesNotContain(store.Templates, t => t[".id"] is "*3" or "*4");
        Assert.Contains(store.Templates, t => t[".id"] == "*A" && t["interfaces"] == "all");
    }

    [Fact]
    public async Task ProductionCleanup_DifferentArea_IsPreserved()
    {
        OspfMigrationPlan plan = Plan();
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*3", "site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"));
        store.Templates.Add(Row("*X", "other-site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"));

        CapturingConnection capturing = store.Connect();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*X");
        Assert.Contains(store.Templates, t => t[".id"] == "*3");
    }

    [Fact]
    public async Task ProductionCleanup_OutsideConsolidated24_IsPreserved()
    {
        OspfMigrationPlan plan = Plan();
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*3", "site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"));
        store.Templates.Add(Row("*O", "site-v2", "Bridge-OTHER", "10.16.50.0/24"));

        CapturingConnection capturing = store.Connect();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*O");
    }

    [Fact]
    public async Task ProductionCleanup_LargerThanConsolidated24_IsPreserved()
    {
        OspfMigrationPlan plan = Plan();
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*3", "site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"));
        store.Templates.Add(Row("*W", "site-v2", "all", "10.16.0.0/16"));

        CapturingConnection capturing = store.Connect();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*W");
    }

    [Fact]
    public async Task ProductionCleanup_UnrelatedTemplate_IsPreserved()
    {
        OspfMigrationPlan plan = Plan();
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*3", "site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"));
        store.Templates.Add(Row("*U", "site-v2", "Bridge-OTHER", "192.0.2.0/24"));

        CapturingConnection capturing = store.Connect();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*U");
    }

    [Fact]
    public async Task ProductionCleanup_MultipleConsolidatedPrefixes_Handled()
    {
        OspfMigrationPlan plan = Plan();
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*3", "site-v2", "vlan-mgmt", "10.16.11.0/27"));
        store.Templates.Add(Row("*4", "site-v2", "vlan-cgnat", "100.64.10.0/24"));
        store.Templates.Add(Row("*5", "site-v2", "vlan-priv", "172.16.5.0/24"));

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(3, capturing.RemovedIds.Count);
        Assert.Equal(3, store.Templates.Count);
        Assert.All(store.Templates, t => Assert.Contains(t[".id"], (string[])["*1", "*2", "*A"]));
    }

    [Fact]
    public async Task ProductionCleanup_Ipv6MoreSpecific_IsDeleted()
    {
        OspfMigrationPlan plan = Plan(
            siteNetworks: "10.16.11.0/24,2001:db8:11::/64,100.64.0.0/10,172.16.0.0/12");
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*3", "site-v2", "Bridge-VLAN-V6", "2001:db8:11::/80"));

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(["*3"], capturing.RemovedIds);
        Assert.DoesNotContain(store.Templates, t => t[".id"] == "*3");
        Assert.Contains(store.Templates, t => t[".id"] == "*A");
    }

    [Fact]
    public async Task ProductionCleanup_FailedDeletion_FailsMigration()
    {
        OspfMigrationPlan plan = Plan();
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*3", "site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"));
        store.Templates.Add(Row("*4", "site-v2", "Bridge-VLAN-197_MGMT", "10.16.11.32/27"));

        CapturingConnection capturing = store.Connect();
        capturing.FailRemoveAtOrdinal = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.True(capturing.RemovedIds.Count < 2);
        Assert.Contains(store.Templates, t => t[".id"] is "*3" or "*4");
    }

    [Fact]
    public void Containment_StrictlyMoreSpecific_Ipv4AndIpv6()
    {
        Assert.True(
            OspfConsolidatedPrefixObsolescence.IsStrictlyContainedBy(
                IPNetwork.Parse("10.16.11.0/27"),
                IPNetwork.Parse("10.16.11.0/24")));
        Assert.False(
            OspfConsolidatedPrefixObsolescence.IsStrictlyContainedBy(
                IPNetwork.Parse("10.16.11.0/24"),
                IPNetwork.Parse("10.16.11.0/24")));
        Assert.False(
            OspfConsolidatedPrefixObsolescence.IsStrictlyContainedBy(
                IPNetwork.Parse("10.16.0.0/16"),
                IPNetwork.Parse("10.16.11.0/24")));
        Assert.True(
            OspfConsolidatedPrefixObsolescence.IsStrictlyContainedBy(
                IPNetwork.Parse("2001:db8:11::/80"),
                IPNetwork.Parse("2001:db8:11::/64")));
    }

    [Fact]
    public void Verification_FailsWhileSubnetsRemain_PassesWhenGone()
    {
        OspfMigrationPlan plan = Plan();
        OspfV7Inventory dirty = Inventory(plan, FiveMgmtSubnets.Select((x, i) =>
            new OspfInterfaceTemplateRow
            {
                Id = $"*{i + 3}",
                Area = "site-v2",
                Interfaces = x.Interfaces,
                Networks = x.Networks,
                Passive = true,
            }));
        Assert.Contains(
            OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, dirty),
            e => e.Contains("obsolete more-specific management", StringComparison.OrdinalIgnoreCase));

        OspfV7Inventory clean = Inventory(plan, []);
        Assert.Empty(OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, clean));
    }

    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    private static OspfMigrationPlan Plan(
        string siteNetworks = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12") =>
        new()
        {
            ImportId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            JobId = "job",
            RouterIp = "10.0.0.1",
            Identity = "BR01.POP011.016.Example",
            SourceRouterOsVersion = "6.49.23",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            InstanceName = "default-v2",
            RouterId = "10.16.0.11",
            TargetInstanceName = "default-v2",
            TargetBackboneAreaName = "backbone-v2",
            TargetSiteAreaName = "site-v2",
            LoopbackInterfaceName = "Loopback",
            LoopbackCidr = "10.16.0.11/32",
            DerivedManagementCidr = "10.16.11.0/24",
            Areas =
            [
                new OspfPlanArea
                {
                    Name = "backbone-v2",
                    AreaId = "0.0.0.0",
                    IsBackbone = true,
                    TargetType = "default",
                },
                new OspfPlanArea
                {
                    Name = "site-v2",
                    AreaId = "10.16.0.11",
                    IsBackbone = false,
                    TargetType = "stub",
                    DefaultCost = "1",
                },
            ],
            AreaRanges =
            [
                new OspfPlanAreaRange
                {
                    AreaName = "site-v2",
                    RangeCidr = "10.16.11.0/24",
                    Comment = "MGMT",
                    Advertise = true,
                },
            ],
            ActiveBackhauls =
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone-v2",
                    Interface = "eth-bh",
                    NetworkCidr = "10.16.248.76/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
            ],
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
            PassiveSiteNetworks =
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "site-v2",
                    Interfaces = "all",
                    NetworkCidr = siteNetworks,
                    Passive = true,
                },
            ],
            SupersededTemplates = [],
            ValidationErrors = [],
            NormalisationNotes = [],
            ContentHash = "test",
        };

    private static Dictionary<string, string> Row(
        string id,
        string area,
        string interfaces,
        string networks,
        bool passive = true,
        string? type = null,
        string? cost = null,
        string? priority = null)
    {
        Dictionary<string, string> d = new()
        {
            [".id"] = id,
            ["area"] = area,
            ["networks"] = networks,
            ["disabled"] = "false",
        };
        if (!string.IsNullOrWhiteSpace(interfaces))
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

        return d;
    }

    private static OspfV7Inventory Inventory(
        OspfMigrationPlan plan,
        IEnumerable<OspfInterfaceTemplateRow> extras) =>
        new()
        {
            Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.16.0.11", Version = "2" }],
            Areas =
            [
                new OspfAreaRow
                {
                    Name = "backbone-v2",
                    AreaId = "0.0.0.0",
                    Instance = "default-v2",
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Name = "site-v2",
                    AreaId = "10.16.0.11",
                    Instance = "default-v2",
                    Type = "stub",
                    DefaultCost = "1",
                },
                new OspfAreaRow
                {
                    Name = "other-site-v2",
                    AreaId = "10.16.0.99",
                    Instance = "default-v2",
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow { Area = "site-v2", Range = "10.16.11.0/24", Comment = "MGMT" },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*1",
                    Area = plan.ActiveBackhauls[0].AreaName,
                    Interfaces = plan.ActiveBackhauls[0].Interface,
                    Networks = plan.ActiveBackhauls[0].NetworkCidr,
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*2",
                    Area = plan.PassiveLoopbacks[0].AreaName,
                    Interfaces = plan.PassiveLoopbacks[0].Interface,
                    Networks = plan.PassiveLoopbacks[0].NetworkCidr,
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*A",
                    Area = plan.PassiveSiteNetworks[0].AreaName,
                    Interfaces = "all",
                    Networks = plan.PassiveSiteNetworks[0].NetworkCidr,
                    Passive = true,
                },
                ..extras,
            ],
        };

    private sealed class MutableStore
    {
        public List<Dictionary<string, string>> Templates { get; } = [];

        public static MutableStore WithPlanned(OspfMigrationPlan plan)
        {
            MutableStore store = new();
            store.Templates.Add(Row(
                "*1",
                plan.ActiveBackhauls[0].AreaName,
                plan.ActiveBackhauls[0].Interface,
                plan.ActiveBackhauls[0].NetworkCidr,
                passive: false,
                type: "ptp",
                cost: "10"));
            store.Templates.Add(Row(
                "*2",
                plan.PassiveLoopbacks[0].AreaName,
                plan.PassiveLoopbacks[0].Interface!,
                plan.PassiveLoopbacks[0].NetworkCidr));
            store.Templates.Add(Row(
                "*A",
                plan.PassiveSiteNetworks[0].AreaName,
                "all",
                plan.PassiveSiteNetworks[0].NetworkCidr));
            return store;
        }

        public CapturingConnection Connect()
        {
            CapturingConnection connection = new(this);
            connection.AttachFakeResponses();
            return connection;
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
        public int PrintCount { get; private set; }
        public int? FailRemoveAtOrdinal { get; set; }

        public void AttachFakeResponses()
        {
            _fake.WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        PrintCount++;
                        return
                        [
                            .._store.Templates.Select(static row =>
                                new TikFakeReSentence(new Dictionary<string, string>(row))),
                            new TikFakeDoneSentence(),
                        ];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/remove",
                    _ =>
                    {
                        _removeOrdinal++;
                        string? id = _pendingRemove?.Parameters
                            .FirstOrDefault(static p => p.Name == ".id")
                            ?.Value;
                        if (FailRemoveAtOrdinal is int failAt && failAt == _removeOrdinal)
                        {
                            throw new InvalidOperationException(
                                $"Simulated remove failure for .id={id} at ordinal {_removeOrdinal}.");
                        }

                        if (string.IsNullOrWhiteSpace(id))
                        {
                            throw new InvalidOperationException("Remove invoked without .id.");
                        }

                        RemovedIds.Add(id);
                        _store.Templates.RemoveAll(t =>
                            string.Equals(t[".id"], id, StringComparison.Ordinal));
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
                    static _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*ab",
                            ["name"] = "backbone-v2",
                            ["instance"] = "default-v2",
                            ["area-id"] = "0.0.0.0",
                        }),
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*as",
                            ["name"] = "site-v2",
                            ["instance"] = "default-v2",
                            ["area-id"] = "10.16.0.11",
                        }),
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*ao",
                            ["name"] = "other-site-v2",
                            ["instance"] = "default-v2",
                            ["area-id"] = "10.16.0.99",
                        }),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/area/range/print",
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First().StartsWith("/routing/filter", StringComparison.Ordinal),
                    static _ => [new TikFakeDoneSentence()]);
        }

        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        public override ITikCommand CreateCommandAndParameters(
            string commandTextOrEmpty,
            params string[] parameterNamesAndValues)
        {
            ITikCommand cmd = base.CreateCommandAndParameters(commandTextOrEmpty, parameterNamesAndValues);
            Track(commandTextOrEmpty, cmd);
            return cmd;
        }

        public override ITikCommand CreateCommandAndParameters(
            string commandTextOrEmpty,
            TikCommandParameterFormat defaultParameterFormat,
            params string[] parameterNamesAndValues)
        {
            ITikCommand cmd = base.CreateCommandAndParameters(
                commandTextOrEmpty,
                defaultParameterFormat,
                parameterNamesAndValues);
            Track(commandTextOrEmpty, cmd);
            return cmd;
        }

        private void Track(string commandText, ITikCommand cmd)
        {
            if (commandText == "/routing/ospf/interface-template/remove")
            {
                _pendingRemove = cmd;
            }
        }
    }
}
