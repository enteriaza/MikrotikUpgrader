// -----------------------------------------------------------------------------
// File: OspfConsolidatedPrefixCleanupTests.cs
//
// Description:
// Production-path coverage for same-area consolidated-prefix cleanup via
//
// -----------------------------------------------------------------------------

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
    /// <summary>
    /// Management sub-prefixes (network, interface) that sit strictly inside the consolidated 10.16.11.0/24 site template and are expected to be removed as obsolete.
    /// </summary>
    private static readonly (string Networks, string Interfaces)[] FiveMgmtSubnets =
    [
        ("10.16.11.0/27", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.32/27", "Bridge-VLAN-197_MGMT"),
        ("10.16.11.64/26", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.128/26", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.192/26", "Bridge-VLAN-99_MGMT"),
    ];

    /// <summary>
    /// Scenario: the plan has no superseded-template provenance and the router holds the three planned templates plus the five legacy MGMT subnet templates (ptp, cost 1).
    /// Expected: all five legacy templates are removed, three templates remain including the consolidated 'all' template (*A), and the inventory is re-read at least twice.
    /// Invariant: obsolete more-specific templates are found from containment in the consolidated prefix alone and deletion is confirmed against a fresh read.
    /// </summary>
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

    /// <summary>
    /// Scenario: two legacy /27 templates bound to named VLAN interfaces sit inside the consolidated 10.16.11.0/24.
    /// Expected: both are removed while the 'all' template *A remains.
    /// Invariant: the interface binding of a legacy template does not shield it from cleanup when its network is contained in the consolidated prefix.
    /// </summary>
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

    /// <summary>
    /// Scenario: a matching legacy template exists in site-v2 and an identical-looking one exists in another area (other-site-v2).
    /// Expected: cleanup throws InvalidOperationException ('No deletion was attempted'), removes nothing and leaves both *3 and *X.
    /// Invariant: a template in a different area is unexpected and aborts cleanup before any deletion.
    /// </summary>
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

    /// <summary>
    /// Scenario: besides a legacy /27, the site area holds a Bridge-OTHER template for 10.16.50.0/24 outside the consolidated /24.
    /// Expected: cleanup throws ('No deletion was attempted'), nothing is removed and *O is retained.
    /// Invariant: templates outside the consolidated prefix are never deleted and their presence aborts the whole cleanup.
    /// </summary>
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

    /// <summary>
    /// Scenario: a broader 10.16.0.0/16 'all' template exists in the site area alongside a legacy /27.
    /// Expected: cleanup throws ('No deletion was attempted'), removes nothing and keeps *W.
    /// Invariant: a template broader than the consolidated /24 is not more specific, so it is never removed.
    /// </summary>
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

    /// <summary>
    /// Scenario: a Bridge-OTHER template for 192.0.2.0/24 is present in the site area next to a legacy /27.
    /// Expected: cleanup throws ('No deletion was attempted'), removes nothing and keeps *U.
    /// Invariant: unrelated templates are never touched, and cleanup fails closed when it meets one.
    /// </summary>
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

    /// <summary>
    /// Scenario: legacy templates inside each consolidated prefix: 10.16.11.0/27, 100.64.10.0/24 and 172.16.5.0/24.
    /// Expected: three removals, leaving only the planned templates *1, *2 and *A.
    /// Invariant: containment is evaluated against every consolidated prefix, including the fleet aggregates.
    /// </summary>
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

    /// <summary>
    /// Scenario: the consolidated list includes 2001:db8:11::/64 and a legacy template exists for 2001:db8:11::/80.
    /// Expected: only that IPv6 template (*3) is removed and *A remains.
    /// Invariant: strict-containment cleanup works for IPv6 prefixes as well as IPv4.
    /// </summary>
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

    /// <summary>
    /// Scenario: the router rejects the removal at ordinal 1 while two legacy templates are queued.
    /// Expected: cleanup throws InvalidOperationException, fewer than two templates were removed and a legacy template is still present.
    /// Invariant: a failed deletion fails the migration rather than being treated as cleaned up.
    /// </summary>
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

    /// <summary>
    /// Scenario: IsStrictlyContainedBy is called with a /27 in a /24, an identical /24, a /16 against a /24, and an IPv6 /80 in a /64.
    /// Expected: true for the /27 in the /24 and the IPv6 /80 in /64; false for the equal prefix and for the broader /16.
    /// Invariant: only strictly narrower prefixes count as contained; equal or broader ones do not.
    /// </summary>
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

    /// <summary>
    /// Scenario: post-apply inventory first still holds the five legacy MGMT subnet templates, then holds none.
    /// Expected: the summarisation postconditions report an 'obsolete more-specific management' error for the first and no errors for the second.
    /// Invariant: verification flags leftover management subnets and passes once they are gone.
    /// </summary>
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

    /// <summary>
    /// Creates the real <c>OspfMikrotikOperations</c> with a null logger.
    /// </summary>
    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    /// <summary>
    /// Builds a ROS6-sourced plan with one backhaul, one passive loopback and a single consolidated passive site template, with no provenance for superseded templates.
    /// <param name="siteNetworks">Comma-separated networks carried by the consolidated site template.</param>
    /// </summary>
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

    /// <summary>
    /// Builds a raw RouterOS interface-template row as returned by <c>print</c>, omitting optional properties that are not supplied.
    /// <param name="id">RouterOS <c>.id</c> of the row.</param>
    /// <param name="area">Area name the template belongs to.</param>
    /// <param name="interfaces">Interfaces property; omitted when blank.</param>
    /// <param name="networks">Networks property.</param>
    /// <param name="passive">Whether to emit the <c>passive</c> flag.</param>
    /// <param name="type">Optional network type.</param>
    /// <param name="cost">Optional cost.</param>
    /// <param name="priority">Optional priority.</param>
    /// </summary>
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

    /// <summary>
    /// Builds a v7 inventory matching the plan (instance, three areas, MGMT range and the three planned templates) plus any extra templates.
    /// <param name="plan">Plan whose backhaul, loopback and site template are mirrored in the inventory.</param>
    /// <param name="extras">Additional templates, e.g. leftover more-specific management prefixes.</param>
    /// </summary>
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

    /// <summary>
    /// In-memory interface-template table that starts with the plan's backhaul, loopback and consolidated site
    /// templates and is changed by the fake remove handler.
    /// </summary>
    private sealed class MutableStore
    {
        /// <summary>
        /// Raw interface-template rows currently "on the router"; mutated as the fake processes removals.
        /// </summary>
        public List<Dictionary<string, string>> Templates { get; } = [];

        /// <summary>
        /// Creates a store holding the plan's three templates (backhaul <c>*1</c>, loopback <c>*2</c>, consolidated site template <c>*A</c>).
        /// <param name="plan">Plan to mirror.</param>
        /// </summary>
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

        /// <summary>
        /// Creates a <see cref="CapturingConnection"/> over this store with its fake RouterOS responses attached.
        /// </summary>
        public CapturingConnection Connect()
        {
            CapturingConnection connection = new(this);
            connection.AttachFakeResponses();
            return connection;
        }
    }

    /// <summary>
    /// Connection fake that serves interface-template print and remove from a mutable store, records removed ids
    /// and print counts, and can fail a chosen remove.
    /// </summary>
    private sealed class CapturingConnection : DisposeTrackingConnection
    {
        /// <summary>
        /// Router state backing the fake print/remove responses.
        /// </summary>
        private readonly MutableStore _store;
        /// <summary>
        /// Underlying fake connection that serves scripted responses.
        /// </summary>
        private readonly TikFakeConnection _fake;
        /// <summary>
        /// Most recent interface-template remove command, used to recover the <c>.id</c> being removed.
        /// </summary>
        private ITikCommand? _pendingRemove;
        /// <summary>
        /// Number of remove calls handled so far, used to trigger <see cref="FailRemoveAtOrdinal"/>.
        /// </summary>
        private int _removeOrdinal;

        /// <summary>
        /// Creates a connection over <paramref name="store"/> using a new fake RouterOS connection.
        /// <param name="store">Router state the connection reads from and mutates.</param>
        /// </summary>
        public CapturingConnection(MutableStore store)
            : this(store, new TikFakeConnection())
        {
        }

        /// <summary>
        /// Wraps the supplied fake so the same instance can be kept for registering responses.
        /// <param name="store">Router state the connection reads from and mutates.</param>
        /// <param name="fake">Fake connection that serves scripted responses.</param>
        /// </summary>
        private CapturingConnection(MutableStore store, TikFakeConnection fake)
            : base(fake)
        {
            _store = store;
            _fake = fake;
        }

        /// <summary>
        /// <c>.id</c> values successfully removed, in order.
        /// </summary>
        public List<string> RemovedIds { get; } = [];
        /// <summary>
        /// Number of interface-template <c>print</c> calls served, used to prove inventory is re-read during cleanup.
        /// </summary>
        public int PrintCount { get; private set; }
        /// <summary>
        /// 1-based ordinal of the remove call that should fail with an <see cref="InvalidOperationException"/>; <see langword="null"/> to never fail.
        /// </summary>
        public int? FailRemoveAtOrdinal { get; set; }

        /// <summary>
        /// Registers scripted responses: template print/remove backed by <see cref="MutableStore"/>, instance and area inventory, empty area ranges and empty routing filters.
        /// </summary>
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

        /// <summary>
        /// Creates the command and records it when it is an interface-template remove.
        /// </summary>
        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        /// <summary>
        /// Creates the command (with an explicit parameter format) and records it when it is an interface-template remove.
        /// </summary>
        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        /// <summary>
        /// Creates the command from parameter names/values and records it when it is an interface-template remove.
        /// </summary>
        public override ITikCommand CreateCommandAndParameters(
            string commandTextOrEmpty,
            params string[] parameterNamesAndValues)
        {
            ITikCommand cmd = base.CreateCommandAndParameters(commandTextOrEmpty, parameterNamesAndValues);
            Track(commandTextOrEmpty, cmd);
            return cmd;
        }

        /// <summary>
        /// Creates the command from parameter names/values (with an explicit parameter format) and records it when it is an interface-template remove.
        /// </summary>
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

        /// <summary>
        /// Remembers <paramref name="cmd"/> as the pending remove when <paramref name="commandText"/> is <c>/routing/ospf/interface-template/remove</c>.
        /// <param name="commandText">RouterOS command path being created.</param>
        /// <param name="cmd">Command instance created for that path.</param>
        /// </summary>
        private void Track(string commandText, ITikCommand cmd)
        {
            if (commandText == "/routing/ospf/interface-template/remove")
            {
                _pendingRemove = cmd;
            }
        }
    }
}
