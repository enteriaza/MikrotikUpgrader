// -----------------------------------------------------------------------------
// File: OspfExactEqualConsolidationCleanupTests.cs
//
// Description:
// Production-path coverage for exact-equal consolidation source cleanup:
// migration-owned source templates whose networks are retained unchanged on
// the planned interfaces=all matcher must be removed without provenance, while
// unrelated dynamic / preserved-unmanaged templates stay untouched.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Exact-equal and multi-source consolidation cleanup through
/// <see cref="OspfMikrotikOperations.RemoveObsoleteTemplatesAsync"/> and
/// <see cref="OspfV7MigrationVerifier.VerifyAsync"/>.
/// </summary>
public sealed class OspfExactEqualConsolidationCleanupTests
{
    /// <summary>
    /// Public <c>/24</c> retained unchanged on the consolidated <c>interfaces=all</c> matcher and used as the exact-equal consolidation source under test.
    /// </summary>
    private const string RetainedPublicPrefix = "102.66.242.0/24";

    /// <summary>
    /// Cashane-like multi-prefix site network list (management, CGNAT supernet, retained public) for the consolidated matcher.
    /// </summary>
    private const string ConsolidatedNetworks =
        "10.16.2.0/24,10.16.22.0/24,100.64.0.0/10,102.66.242.0/24";

    /// <summary>
    /// Scenario: site-area <c>interfaces=dynamic</c> source carries exact-equal <c>P/24</c> that is
    /// retained on the consolidated <c>interfaces=all</c> matcher; plan has no superseded provenance
    /// and the consolidated matcher carries <c>comment=MGMT</c> (desired-state semantic mismatch).
    /// Expected: source is KnownLegacy via consolidated-prefix replacement, removed, matcher retains
    /// <c>P/24</c>, and final verification succeeds.
    /// </summary>
    [Fact]
    public async Task ExactEqual_DynamicSource_RemovedWithoutProvenance_VerifySucceeds()
    {
        OspfMigrationPlan plan = CashaneLikePlan(comment: "MGMT");
        Assert.Empty(plan.SupersededTemplates);

        MutableStore store = MutableStore.WithPlanned(plan, siteComment: "MGMT");
        store.Templates.Add(Row(
            "*dyn",
            plan.TargetSiteAreaName,
            "dynamic",
            RetainedPublicPrefix,
            type: "ptp",
            priority: "1"));

        // Classifier path (no provenance, comment mismatch would block desired-state).
        OspfTemplateClassificationReport before = Classify(plan, store);
        OspfClassifiedTemplate legacy = Assert.Single(before.KnownLegacy);
        Assert.Equal("*dyn", legacy.Row.Id);
        Assert.Contains("exact consolidated member", legacy.Reason, StringComparison.Ordinal);
        Assert.Empty(before.PreservedUnmanaged);

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(["*dyn"], capturing.RemovedIds);
        Assert.DoesNotContain(store.Templates, t => t[".id"] == "*dyn");
        Dictionary<string, string> consolidated = Assert.Single(
            store.Templates,
            t => t[".id"] == "*A");
        Assert.Equal("all", consolidated["interfaces"]);
        Assert.Contains(RetainedPublicPrefix, consolidated["networks"], StringComparison.Ordinal);

        OspfVerificationResult verified = await VerifyFromStoreAsync(plan, store);
        Assert.True(verified.Succeeded, verified.Detail);
    }

    /// <summary>
    /// Scenario: VLAN-bound <c>/27</c> sits strictly inside consolidated management <c>/24</c>.
    /// Expected: source removed; consolidated retains <c>/24</c>; unrelated outside prefix preserved.
    /// </summary>
    [Fact]
    public async Task StrictContainment_StillRemoved_UnrelatedPreserved()
    {
        OspfMigrationPlan plan = CashaneLikePlan(
            siteNetworks: "10.16.2.0/24,10.16.22.0/24,100.64.0.0/10",
            comment: "MGMT");
        MutableStore store = MutableStore.WithPlanned(plan, siteComment: "MGMT");
        store.Templates.Add(Row("*sub", plan.TargetSiteAreaName, "Bridge-VLAN-99_MGMT", "10.16.2.0/27"));
        store.Templates.Add(Row("*out", plan.TargetSiteAreaName, "Bridge-OTHER", "10.16.50.0/24"));

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(["*sub"], capturing.RemovedIds);
        Assert.DoesNotContain(store.Templates, t => t[".id"] == "*sub");
        Assert.Contains(store.Templates, t => t[".id"] == "*out");
        Assert.Contains(
            store.Templates,
            t => t[".id"] == "*A" && t["networks"].Contains("10.16.2.0/24", StringComparison.Ordinal));

        OspfVerificationResult verified = await VerifyFromStoreAsync(plan, store);
        Assert.True(verified.Succeeded, verified.Detail);
    }

    /// <summary>
    /// Scenario: three migration-owned sources (exact-equal public, exact-equal CGNAT member, strict
    /// management subdivision) feed one consolidated matcher.
    /// Expected: all three removed; every required consolidated prefix retained; verify succeeds.
    /// </summary>
    [Fact]
    public async Task MultipleSourcePrefixes_AllRemoved_ConsolidatedRetainsAll()
    {
        OspfMigrationPlan plan = CashaneLikePlan(comment: "MGMT");
        MutableStore store = MutableStore.WithPlanned(plan, siteComment: "MGMT");
        store.Templates.Add(Row("*p1", plan.TargetSiteAreaName, "dynamic", RetainedPublicPrefix, type: "ptp"));
        store.Templates.Add(Row("*p2", plan.TargetSiteAreaName, "pppoe-out1", "100.64.10.0/24"));
        store.Templates.Add(Row("*p3", plan.TargetSiteAreaName, "Bridge-VLAN-99_MGMT", "10.16.2.64/26"));

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(3, capturing.RemovedIds.Count);
        Assert.DoesNotContain(store.Templates, t => t[".id"] is "*p1" or "*p2" or "*p3");
        string networks = Assert.Single(store.Templates, t => t[".id"] == "*A")["networks"];
        foreach (string required in OspfNetworkList.Split(ConsolidatedNetworks))
        {
            Assert.Contains(required, networks, StringComparison.Ordinal);
        }

        OspfVerificationResult verified = await VerifyFromStoreAsync(plan, store);
        Assert.True(verified.Succeeded, verified.Detail);
    }

    /// <summary>
    /// Scenario: migration-owned dynamic source for retained <c>P/24</c> plus an unrelated dynamic
    /// public prefix that is not a consolidated member.
    /// Expected: only the owned source is removed; unrelated dynamic remains PreservedUnmanaged and
    /// verify succeeds with preserved-unmanaged=1.
    /// </summary>
    [Fact]
    public async Task DynamicOwnedRemoved_UnrelatedDynamicPreserved()
    {
        OspfMigrationPlan plan = CashaneLikePlan(comment: "MGMT");
        MutableStore store = MutableStore.WithPlanned(plan, siteComment: "MGMT");
        store.Templates.Add(Row(
            "*owned",
            plan.TargetSiteAreaName,
            "dynamic",
            RetainedPublicPrefix,
            type: "ptp",
            priority: "1"));
        store.Templates.Add(Row(
            "*unrel",
            plan.TargetBackboneAreaName,
            "dynamic",
            "203.0.113.0/24",
            type: "ptp"));

        OspfTemplateClassificationReport before = Classify(plan, store);
        Assert.Contains(before.KnownLegacy, l => l.Row.Id == "*owned");
        Assert.Contains(before.PreservedUnmanaged, p => p.Row.Id == "*unrel");

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(["*owned"], capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*unrel");

        OspfVerificationResult verified = await VerifyFromStoreAsync(plan, store);
        Assert.True(verified.Succeeded, verified.Detail);
    }

    /// <summary>
    /// Scenario: two templates share <c>P/24</c> but one is in the planned site area and one is in a
    /// different area.
    /// Expected: only the same-area consolidation source is removed.
    /// </summary>
    [Fact]
    public async Task SamePrefixDifferentArea_OnlySiteSourceRemoved()
    {
        OspfMigrationPlan plan = CashaneLikePlan(comment: null);
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*site", plan.TargetSiteAreaName, "dynamic", RetainedPublicPrefix));
        store.Templates.Add(Row("*other", "other-site-v2", "dynamic", RetainedPublicPrefix));

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(["*site"], capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*other");
        Assert.DoesNotContain(store.Templates, t => t[".id"] == "*site");
    }

    /// <summary>
    /// Scenario: same prefix on the planned site area but already the planned <c>interfaces=all</c>
    /// matcher (exact identity) must not be classified obsolete; a distinct interface source for the
    /// same member prefix is removed.
    /// </summary>
    [Fact]
    public async Task InterfaceDistinction_PlannedAllProtected_SourceInterfaceRemoved()
    {
        OspfMigrationPlan plan = CashaneLikePlan(
            siteNetworks: RetainedPublicPrefix,
            comment: null);
        MutableStore store = MutableStore.WithPlanned(plan);
        store.Templates.Add(Row("*src", plan.TargetSiteAreaName, "Bridge-VLAN-X", RetainedPublicPrefix));

        CapturingConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(["*src"], capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*A" && t["interfaces"] == "all");
    }

    /// <summary>
    /// Scenario: first cleanup removes the exact-equal source; second cleanup against the resulting
    /// state issues no further removes; both post-cleanup verifications succeed.
    /// </summary>
    [Fact]
    public async Task IdempotentCleanup_SecondPassNoRemovals_VerifyTwice()
    {
        OspfMigrationPlan plan = CashaneLikePlan(comment: "MGMT");
        MutableStore store = MutableStore.WithPlanned(plan, siteComment: "MGMT");
        store.Templates.Add(Row("*dyn", plan.TargetSiteAreaName, "dynamic", RetainedPublicPrefix, type: "ptp"));

        CapturingConnection first = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(first, plan, CancellationToken.None);
        Assert.Equal(["*dyn"], first.RemovedIds);
        Assert.True((await VerifyFromStoreAsync(plan, store)).Succeeded);

        CapturingConnection second = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(second, plan, CancellationToken.None);
        Assert.Empty(second.RemovedIds);
        Assert.True((await VerifyFromStoreAsync(plan, store)).Succeeded);
    }

    /// <summary>
    /// Scenario: RouterOS remove for the exact-equal source fails.
    /// Expected: cleanup throws; source remains; verification against residual inventory fails.
    /// </summary>
    [Fact]
    public async Task RemovalFailure_CleanupThrows_VerifyDoesNotSucceed()
    {
        OspfMigrationPlan plan = CashaneLikePlan(comment: "MGMT");
        MutableStore store = MutableStore.WithPlanned(plan, siteComment: "MGMT");
        store.Templates.Add(Row("*dyn", plan.TargetSiteAreaName, "dynamic", RetainedPublicPrefix, type: "ptp"));

        CapturingConnection capturing = store.Connect();
        capturing.FailRemoveAtOrdinal = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains(store.Templates, t => t[".id"] == "*dyn");

        OspfVerificationResult verified = await VerifyFromStoreAsync(plan, store);
        Assert.False(verified.Succeeded);
        Assert.Contains(
            verified.Errors,
            e => e.Contains("Unexpected superseded/legacy template remains", StringComparison.Ordinal)
                 || e.Contains("102.66.242.0/24", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: helper coverage for exact-member versus strict-containment predicates.
    /// </summary>
    [Fact]
    public void CoveragePredicates_ExactAndStrict()
    {
        IPNetwork retained = IPNetwork.Parse(RetainedPublicPrefix);
        IPNetwork subnet = IPNetwork.Parse("10.16.2.0/27");
        IPNetwork covering = IPNetwork.Parse("10.16.2.0/24");

        Assert.True(OspfConsolidatedPrefixObsolescence.IsExactMemberOf(retained, retained));
        Assert.False(OspfConsolidatedPrefixObsolescence.IsStrictlyContainedBy(retained, retained));
        Assert.True(OspfConsolidatedPrefixObsolescence.IsCoveredByConsolidated(retained, retained));
        Assert.True(OspfConsolidatedPrefixObsolescence.IsCoveredByConsolidated(subnet, covering));
        Assert.False(OspfConsolidatedPrefixObsolescence.IsCoveredByConsolidated(
            IPNetwork.Parse("10.16.0.0/16"),
            covering));
    }

    /// <summary>
    /// Creates production <see cref="OspfMikrotikOperations"/> with a null logger for cleanup calls against the capturing connection.
    /// </summary>
    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    /// <summary>
    /// Runs <see cref="OspfTemplateCleanupClassifier.ClassifyOrThrow"/> against the store's raw template rows mapped into inventory rows.
    /// </summary>
    /// <param name="plan">Plan whose target instance name is stamped onto each row for classification.</param>
    /// <param name="store">Mutable template table representing current router state.</param>
    private static OspfTemplateClassificationReport Classify(OspfMigrationPlan plan, MutableStore store)
    {
        List<OspfTemplateInventoryRow> rows = store.Templates
            .Select(t => new OspfTemplateInventoryRow
            {
                Id = t[".id"],
                Area = t["area"],
                AreaInstanceName = plan.TargetInstanceName,
                Interfaces = t.GetValueOrDefault("interfaces"),
                Networks = t["networks"],
                Type = t.GetValueOrDefault("type"),
                Cost = t.GetValueOrDefault("cost"),
                Priority = t.GetValueOrDefault("priority"),
                Passive = t.ContainsKey("passive"),
                Disabled = t.GetValueOrDefault("disabled") == "true",
                AdminState = t.GetValueOrDefault("disabled") == "true"
                    ? OspfAdminState.Disabled
                    : OspfAdminState.Enabled,
            })
            .ToList();
        return OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, rows);
    }

    /// <summary>
    /// Builds a complete inventory from <paramref name="store"/> and runs <see cref="OspfV7MigrationVerifier.VerifyAsync"/> via <see cref="FakeVerifyOps"/>.
    /// </summary>
    /// <param name="plan">Migration plan under verification.</param>
    /// <param name="store">Template table after cleanup (or residual state on failure paths).</param>
    private static async Task<OspfVerificationResult> VerifyFromStoreAsync(
        OspfMigrationPlan plan,
        MutableStore store)
    {
        OspfV7Inventory inventory = InventoryFromStore(plan, store);
        FakeVerifyOps ops = new(inventory, plan);
        return await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
    }

    /// <summary>
    /// Builds a capture-complete v7 inventory mirroring the plan's instance, backbone/site/other areas, ranges, and the store's current templates.
    /// </summary>
    /// <param name="plan">Plan whose instance, areas and ranges are mirrored.</param>
    /// <param name="store">Current interface-template table (planned plus any residual sources).</param>
    private static OspfV7Inventory InventoryFromStore(OspfMigrationPlan plan, MutableStore store) =>
        new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*i",
                    Name = plan.TargetInstanceName,
                    RouterId = plan.RouterId,
                    Version = "2",
                },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*ab",
                    Name = plan.TargetBackboneAreaName,
                    AreaId = "0.0.0.0",
                    Instance = plan.TargetInstanceName,
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Id = "*as",
                    Name = plan.TargetSiteAreaName,
                    AreaId = "10.16.0.2",
                    Instance = plan.TargetInstanceName,
                    Type = "stub",
                    DefaultCost = "1",
                },
                new OspfAreaRow
                {
                    Id = "*ao",
                    Name = "other-site-v2",
                    AreaId = "10.16.0.99",
                    Instance = plan.TargetInstanceName,
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
            AreaRanges = plan.AreaRanges
                .Select(r => new OspfAreaRangeRow
                {
                    Area = r.AreaName,
                    Range = r.RangeCidr,
                    Comment = r.Comment,
                })
                .ToList(),
            Templates = store.Templates
                .Select(t => new OspfInterfaceTemplateRow
                {
                    Id = t[".id"],
                    Area = t["area"],
                    Interfaces = t.GetValueOrDefault("interfaces"),
                    Networks = t["networks"],
                    Type = t.GetValueOrDefault("type"),
                    Cost = t.GetValueOrDefault("cost"),
                    Priority = t.GetValueOrDefault("priority"),
                    Passive = t.ContainsKey("passive"),
                    Disabled = false,
                    AdminState = OspfAdminState.Enabled,
                    Comment = t.GetValueOrDefault("comment"),
                })
                .ToList(),
            Filters = [],
        };

    /// <summary>
    /// Cashane-shaped migration plan with five backbone backhauls, loopback, consolidated site matcher, and no superseded provenance.
    /// </summary>
    /// <param name="siteNetworks">Comma-separated networks for the planned <c>interfaces=all</c> site template; defaults to <see cref="ConsolidatedNetworks"/>.</param>
    /// <param name="comment">Optional comment on the site template (e.g. <c>MGMT</c>) to exercise semantic mismatch paths; <see langword="null"/> omits it.</param>
    private static OspfMigrationPlan CashaneLikePlan(
        string siteNetworks = ConsolidatedNetworks,
        string? comment = "MGMT") =>
        new()
        {
            ImportId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            JobId = "job-cashane-like",
            RouterIp = "10.16.0.2",
            Identity = "BR01.POP002.016.Cashane",
            SourceRouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            InstanceName = "default-v2",
            RouterId = "10.16.0.2",
            TargetInstanceName = "default-v2",
            TargetBackboneAreaName = "backbone",
            TargetSiteAreaName = "BR01.POP002.016.Cashane",
            LoopbackInterfaceName = "Loopback-OSPF",
            LoopbackCidr = "10.16.0.2/32",
            DerivedManagementCidr = "10.16.2.0/24",
            DerivedManagementCidrs = ["10.16.2.0/24", "10.16.22.0/24"],
            Areas =
            [
                new OspfPlanArea
                {
                    Name = "backbone",
                    AreaId = "0.0.0.0",
                    IsBackbone = true,
                    TargetType = "default",
                },
                new OspfPlanArea
                {
                    Name = "BR01.POP002.016.Cashane",
                    AreaId = "10.16.0.2",
                    IsBackbone = false,
                    TargetType = "stub",
                    DefaultCost = "1",
                },
            ],
            AreaRanges =
            [
                new OspfPlanAreaRange
                {
                    AreaName = "BR01.POP002.016.Cashane",
                    RangeCidr = "100.99.100.0/24",
                    Advertise = true,
                },
                new OspfPlanAreaRange
                {
                    AreaName = "BR01.POP002.016.Cashane",
                    RangeCidr = "10.16.2.0/24",
                    Comment = "MGMT",
                    Advertise = true,
                },
                new OspfPlanAreaRange
                {
                    AreaName = "BR01.POP002.016.Cashane",
                    RangeCidr = "10.16.22.0/24",
                    Comment = "MGMT",
                    Advertise = true,
                },
            ],
            ActiveBackhauls =
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "eth-01_BH03",
                    NetworkCidr = "10.16.248.24/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "VLAN-50_DATA",
                    NetworkCidr = "10.16.248.52/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "eth-04_BH02",
                    NetworkCidr = "10.16.248.28/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "sfpp-01_BH01",
                    NetworkCidr = "10.16.248.140/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "VLAN-1197_DATA_TEMP",
                    NetworkCidr = "10.16.248.0/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
            ],
            PassiveLoopbacks =
            [
                new OspfPlanLoopbackTemplate
                {
                    AreaName = "backbone",
                    Interface = "Loopback-OSPF",
                    NetworkCidr = "10.16.0.2/32",
                    Passive = true,
                },
            ],
            PassiveSiteNetworks =
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "BR01.POP002.016.Cashane",
                    Interfaces = "all",
                    NetworkCidr = siteNetworks,
                    Comment = comment,
                    Passive = true,
                },
            ],
            SupersededTemplates = [],
            ValidationErrors = [],
            NormalisationNotes = [],
            ContentHash = "exact-equal-consolidation",
        };

    /// <summary>
    /// Builds a raw RouterOS interface-template row as returned by <c>print</c>, omitting optional properties that are not supplied.
    /// </summary>
    /// <param name="id">RouterOS <c>.id</c> of the row.</param>
    /// <param name="area">Area name the template belongs to.</param>
    /// <param name="interfaces">Interfaces property; omitted when blank.</param>
    /// <param name="networks">Networks property.</param>
    /// <param name="passive">Whether to emit the <c>passive</c> flag.</param>
    /// <param name="type">Optional network type.</param>
    /// <param name="cost">Optional cost.</param>
    /// <param name="priority">Optional priority.</param>
    /// <param name="comment">Optional comment (e.g. <c>MGMT</c>); omitted when <see langword="null"/>.</param>
    private static Dictionary<string, string> Row(
        string id,
        string area,
        string interfaces,
        string networks,
        bool passive = true,
        string? type = null,
        string? cost = null,
        string? priority = null,
        string? comment = null)
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

        if (comment is not null)
        {
            d["comment"] = comment;
        }

        return d;
    }

    /// <summary>
    /// In-memory interface-template table that starts with the plan's backhauls, loopback and consolidated site
    /// matcher and is mutated by the fake remove handler.
    /// </summary>
    private sealed class MutableStore
    {
        /// <summary>
        /// Raw interface-template rows currently "on the router"; mutated as the fake processes removals.
        /// </summary>
        public List<Dictionary<string, string>> Templates { get; } = [];

        /// <summary>
        /// Creates a store holding every planned backhaul, the loopback, and consolidated site template <c>*A</c>.
        /// </summary>
        /// <param name="plan">Plan whose templates are mirrored into the store.</param>
        /// <param name="siteComment">Optional override for the site matcher comment (defaults to the plan's site comment).</param>
        public static MutableStore WithPlanned(OspfMigrationPlan plan, string? siteComment = null)
        {
            MutableStore store = new();
            int i = 1;
            foreach (OspfPlanBackhaulTemplate bh in plan.ActiveBackhauls)
            {
                store.Templates.Add(Row(
                    $"*{i++}",
                    bh.AreaName,
                    bh.Interface,
                    bh.NetworkCidr,
                    passive: false,
                    type: "ptp",
                    cost: "10"));
            }

            OspfPlanLoopbackTemplate loop = plan.PassiveLoopbacks[0];
            store.Templates.Add(Row(
                $"*{i++}",
                loop.AreaName,
                loop.Interface!,
                loop.NetworkCidr));

            OspfPlanSiteNetworkTemplate site = plan.PassiveSiteNetworks[0];
            Dictionary<string, string> all = Row(
                "*A",
                site.AreaName,
                "all",
                site.NetworkCidr,
                comment: siteComment ?? site.Comment);
            store.Templates.Add(all);
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
    /// Connection fake that serves interface-template print and remove from a mutable store, records removed ids,
    /// and can fail a chosen remove ordinal. Models the production cleanup RouterOS surface only.
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
        /// </summary>
        /// <param name="store">Router state the connection reads from and mutates.</param>
        public CapturingConnection(MutableStore store)
            : this(store, new TikFakeConnection())
        {
        }

        /// <summary>
        /// Wraps the supplied fake so the same instance can be kept for registering responses.
        /// </summary>
        /// <param name="store">Router state the connection reads from and mutates.</param>
        /// <param name="fake">Fake connection that serves scripted responses.</param>
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
        /// 1-based ordinal of the remove call that should fail with an <see cref="InvalidOperationException"/>; <see langword="null"/> to never fail.
        /// </summary>
        public int? FailRemoveAtOrdinal { get; set; }

        /// <summary>
        /// Registers scripted responses: template print/remove backed by <see cref="MutableStore"/>, Cashane-like instance and area inventory, empty area ranges and empty routing filters.
        /// </summary>
        public void AttachFakeResponses()
        {
            _fake.WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    [
                        .._store.Templates.Select(static row =>
                            new TikFakeReSentence(new Dictionary<string, string>(row))),
                        new TikFakeDoneSentence(),
                    ])
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
                    _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*i",
                            ["name"] = "default-v2",
                            ["version"] = "2",
                            ["router-id"] = "10.16.0.2",
                        }),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/area/print",
                    _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*ab",
                            ["name"] = "backbone",
                            ["instance"] = "default-v2",
                            ["area-id"] = "0.0.0.0",
                        }),
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*as",
                            ["name"] = "BR01.POP002.016.Cashane",
                            ["instance"] = "default-v2",
                            ["area-id"] = "10.16.0.2",
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
        /// <param name="commandText">RouterOS command path.</param>
        /// <param name="parameters">Command parameters.</param>
        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        /// <summary>
        /// Creates the command (with an explicit parameter format) and records it when it is an interface-template remove.
        /// </summary>
        /// <param name="commandText">RouterOS command path.</param>
        /// <param name="defaultParameterFormat">Parameter encoding format.</param>
        /// <param name="parameters">Command parameters.</param>
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
        /// <param name="commandTextOrEmpty">RouterOS command path.</param>
        /// <param name="parameterNamesAndValues">Alternating parameter names and values.</param>
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
        /// <param name="commandTextOrEmpty">RouterOS command path.</param>
        /// <param name="defaultParameterFormat">Parameter encoding format.</param>
        /// <param name="parameterNamesAndValues">Alternating parameter names and values.</param>
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
        /// </summary>
        /// <param name="commandText">RouterOS command path being created.</param>
        /// <param name="cmd">Command instance created for that path.</param>
        private void Track(string commandText, ITikCommand cmd)
        {
            if (commandText == "/routing/ospf/interface-template/remove")
            {
                _pendingRemove = cmd;
            }
        }
    }

    /// <summary>
    /// Verify-only <see cref="IOspfMikrotikOperations"/> fake: returns a canned inventory and IP addresses derived from
    /// the plan; mutation and capture APIs are no-ops or intentionally unsupported because the verifier never needs them here.
    /// </summary>
    /// <param name="inventory">Canned v7 inventory returned by <see cref="ReadV7InventoryAsync"/>.</param>
    /// <param name="plan">Plan used to synthesize loopback, management and backhaul IP rows.</param>
    private sealed class FakeVerifyOps(OspfV7Inventory inventory, OspfMigrationPlan plan) : IOspfMikrotikOperations
    {
        /// <summary>
        /// Returns the canned inventory supplied at construction; ignores the connection.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(inventory);

        /// <summary>
        /// Synthesizes loopback, dual management and backhaul IP address rows from the plan so live-identity checks can resolve.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            List<OspfIpAddressRow> rows =
            [
                new()
                {
                    Address = plan.LoopbackCidr!,
                    Network = plan.RouterId,
                    Interface = plan.LoopbackInterfaceName!,
                },
                new()
                {
                    Address = "10.16.2.1/24",
                    Network = "10.16.2.0",
                    Interface = "Bridge-VLAN-99_MGMT",
                },
                new()
                {
                    Address = "10.16.22.1/24",
                    Network = "10.16.22.0",
                    Interface = "Bridge-VLAN-99_MGMT",
                },
            ];

            foreach (OspfPlanBackhaulTemplate bh in plan.ActiveBackhauls)
            {
                string network = bh.NetworkCidr.Split('/', 2)[0];
                rows.Add(new OspfIpAddressRow
                {
                    Address = bh.NetworkCidr,
                    Network = network,
                    Interface = bh.Interface,
                });
            }

            return Task.FromResult<IReadOnlyList<OspfIpAddressRow>>(rows);
        }

        /// <summary>
        /// Reports zero routing filters; the verifier only needs a non-blocking filter count.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<int> CountRoutingFiltersAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// Intentionally unsupported; capture is never invoked on the verify-only path under test.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="request">Capture request (unused).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfCaptureSnapshot> CaptureAsync(
            ITikConnection connection,
            OspfCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op stub for filter wipe; verify does not mutate filters.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task RemoveAllRoutingFiltersAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for redistribution clear; verify does not mutate instances.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task ClearOspfRedistributionAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for instance filter-reference clear; verify does not mutate instances.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task ClearOspfInstanceFilterReferencesAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for area ensure; verify does not create areas.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="area">Area plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureOspfAreaAsync(
            ITikConnection connection,
            string instanceName,
            OspfPlanArea area,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for area-range ensure; verify does not create ranges.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="range">Area-range plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureOspfAreaRangeAsync(
            ITikConnection connection,
            OspfPlanAreaRange range,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for backhaul template ensure; verify does not create templates.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Backhaul template (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureBackhaulTemplateAsync(
            ITikConnection connection,
            OspfPlanBackhaulTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for loopback template ensure; verify does not create templates.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Loopback template (ignored).</param>
        /// <param name="protectedBackhauls">Protected backhauls (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureLoopbackTemplateAsync(
            ITikConnection connection,
            OspfPlanLoopbackTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for site-network template ensure; verify does not create templates.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Site-network template (ignored).</param>
        /// <param name="protectedBackhauls">Protected backhauls (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureSiteNetworkTemplateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for site-network state assertion; inventory already encodes desired state for the verifier.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Site-network template (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task AssertSiteNetworkTemplateStateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for obsolete-template cleanup; cleanup is exercised via production ops on the capturing connection instead.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="migrationPlan">Migration plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task RemoveObsoleteTemplatesAsync(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Intentionally unsupported; management-plane baseline capture is not part of the verify path under test.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="migrationPlan">Migration plan (ignored).</param>
        /// <param name="managementHost">Management host (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            string managementHost,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op stub for backbone liveness assertion; verify tests do not exercise management-plane guards.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="migrationPlan">Migration plan (ignored).</param>
        /// <param name="baseline">Management-plane baseline (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <param name="requireFleetCost">Whether fleet cost is required (ignored).</param>
        public Task AssertBackboneStillActiveAsync(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            OspfManagementPlaneBaseline baseline,
            CancellationToken cancellationToken = default,
            bool requireFleetCost = true) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for instance rename; verify does not rename instances.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="currentName">Current instance name (ignored).</param>
        /// <param name="targetName">Target instance name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureOspfInstanceNameAsync(
            ITikConnection connection,
            string currentName,
            string targetName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for unused routing-id cleanup; verify does not mutate <c>/routing/id</c>.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="routingIdName">Routing-id name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task TryRemoveUnusedRoutingIdAsync(
            ITikConnection connection,
            string? routingIdName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns an empty neighbour list; neighbour checks are not asserted in these consolidation tests.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        /// <summary>
        /// No-op stub for OSPFv3 removal; verify does not mutate OSPFv3 objects.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="migrationPlan">Migration plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task RemoveOspfV3Async(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for router-id set; verify does not mutate the instance router-id.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="routerId">Router-id value (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task SetInstanceRouterIdAsync(
            ITikConnection connection,
            string instanceName,
            string routerId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
