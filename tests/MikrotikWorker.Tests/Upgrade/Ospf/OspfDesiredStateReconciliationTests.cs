// -----------------------------------------------------------------------------
// File: OspfDesiredStateReconciliationTests.cs
//
// Description:
// Desired-state OSPF reconciliation: live templates are removed only when an
// explicit replacement relationship to the planned consolidated configuration
// is established. Fixtures are generic — no device-specific harnesses.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Desired-state OSPF reconciliation: live templates are removed only when an explicit
/// replacement relationship to the planned consolidated configuration is established.
/// Fixtures are generic — no device-specific harnesses.
/// </summary>
public sealed class OspfDesiredStateReconciliationTests
{
    /// <summary>Five management subdivisions of one /24 with their interfaces, as seen on a live router.</summary>
    private static readonly (string Networks, string Interfaces)[] FiveMgmtSubdivisions =
    [
        ("10.16.11.0/27", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.32/27", "Bridge-VLAN-197_MGMT"),
        ("10.16.11.64/26", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.128/26", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.192/26", "Bridge-VLAN-99_MGMT"),
    ];

    /// <summary>
    /// Scenario: the plan has no recorded superseded templates, and live inventory holds the three planned rows plus five management /27-/26 subdivisions of 10.16.11.0/24 in the site area.
    /// Expected: classification succeeds with all five subdivisions as known legacy (reason mentions <c>Consolidated-prefix replacement</c>) and nothing unknown.
    /// Invariant: desired-state containment alone is enough to establish replacement; capture provenance is not required.
    /// </summary>
    [Fact]
    public void DesiredState_FiveMgmtSubdivisions_AreKnownLegacy_WithoutCaptureProvenance()
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        Assert.Empty(plan.SupersededTemplates);

        List<OspfTemplateInventoryRow> inventory = PlannedRows(plan)
            .Concat(FiveMgmtSubdivisions.Select((x, i) =>
                SiteRow($"*{i + 3}", plan.PassiveSiteNetworks[0].AreaName, x.Interfaces, x.Networks)))
            .ToList();

        // Classification must succeed from desired-state replacement alone.
        OspfTemplateClassificationReport report =
            OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, inventory);
        Assert.Equal(5, report.KnownLegacy.Count);
        Assert.Empty(report.Unknown);
        Assert.All(
            report.KnownLegacy,
            item => Assert.Contains("Consolidated-prefix replacement", item.Reason, StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: a live 10.16.11.0/27 template is ptp, cost 1, with the RouterOS 7 default priority of 1.
    /// Expected: it is classified as a single known legacy template and nothing is unknown.
    /// Invariant: RouterOS 7 default attribute values do not stop a contained subdivision from being treated as superseded.
    /// </summary>
    [Fact]
    public void DesiredState_Ros7DefaultPriority1_IsCompatibleWithConsolidation()
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        OspfTemplateInventoryRow subdivision = SiteRow(
            "*3",
            plan.PassiveSiteNetworks[0].AreaName,
            "Bridge-VLAN-99_MGMT",
            "10.16.11.0/27",
            priority: "1",
            type: "ptp",
            cost: "1");

        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(
            plan,
            PlannedRows(plan).Append(subdivision).ToList());
        Assert.Single(report.KnownLegacy);
        Assert.Empty(report.Unknown);
    }

    /// <summary>
    /// Scenario: two live subdivisions (10.16.11.0/27 on Bridge-VLAN-99_MGMT and 10.16.11.32/27 on Bridge-VLAN-197_MGMT) sit under the consolidated <c>interfaces=all</c> template.
    /// Expected: both are known legacy and nothing is unknown.
    /// Invariant: per-interface templates fold into the consolidated all-interfaces template, regardless of their interface names.
    /// </summary>
    [Fact]
    public void DesiredState_MultipleSpecificInterfaces_FoldIntoInterfacesAll()
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        List<OspfTemplateInventoryRow> inventory = PlannedRows(plan)
            .Append(SiteRow("*3", "site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"))
            .Append(SiteRow("*4", "site-v2", "Bridge-VLAN-197_MGMT", "10.16.11.32/27"))
            .ToList();

        OspfTemplateClassificationReport report =
            OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, inventory);
        Assert.Equal(2, report.KnownLegacy.Count);
        Assert.Empty(report.Unknown);
    }

    /// <summary>
    /// Scenario: a retry where the consolidated template exists and only two of the five subdivisions remain; then the remaining two are deleted.
    /// Expected: the partial inventory classifies two known legacy rows; the final inventory has no legacy, no unknown, and a single site matcher.
    /// Invariant: cleanup is resumable after a partial deletion.
    /// </summary>
    [Fact]
    public void DesiredState_Retry_WhenConsolidatedAlreadyPresent_AndSomeSubdivisionsGone()
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        List<OspfTemplateInventoryRow> partial = PlannedRows(plan)
            .Concat(FiveMgmtSubdivisions.Take(2).Select((x, i) =>
                SiteRow($"*{i + 3}", "site-v2", x.Interfaces, x.Networks)))
            .ToList();

        OspfTemplateClassificationReport during =
            OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, partial);
        Assert.Equal(2, during.KnownLegacy.Count);

        List<OspfTemplateInventoryRow> afterSimulatedDeletes = PlannedRows(plan).ToList();
        OspfTemplateClassificationReport final =
            OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, afterSimulatedDeletes);
        Assert.Empty(final.KnownLegacy);
        Assert.Empty(final.Unknown);
        Assert.Single(final.SiteMatchers);
    }

    /// <summary>
    /// Scenario: live inventory contains exactly the three planned templates (backhaul, loopback, consolidated site network).
    /// Expected: no known legacy, no unknown, and a planned-protected count of 3.
    /// Invariant: classifying an already-reconciled router finds nothing to delete.
    /// </summary>
    [Fact]
    public void DesiredState_AlreadyReconciled_IsIdempotent()
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        OspfTemplateClassificationReport report =
            OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, PlannedRows(plan).ToList());
        Assert.Empty(report.KnownLegacy);
        Assert.Empty(report.Unknown);
        Assert.Equal(3, report.PlannedProtectedCount);
    }

    /// <summary>
    /// Scenario: a plan with no superseded list is enriched from a live inventory holding five management subdivisions, then the enriched plan is classified against that inventory.
    /// Expected: enrichment records five superseded templates; classification reports five known legacy and no unknown.
    /// Invariant: the enrich-then-classify pipeline agrees with desired-state classification for the same live rows.
    /// </summary>
    [Fact]
    public void DesiredState_EnrichThenClassify_FiveSubdivisions_Succeeds()
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        OspfV7Inventory inventory = InventoryWith(
            PlannedTemplateRows(plan).Concat(FiveMgmtSubdivisions.Select((x, i) =>
                new OspfInterfaceTemplateRow
                {
                    Id = $"*{i + 3}",
                    Area = "site-v2",
                    Interfaces = x.Interfaces,
                    Networks = x.Networks,
                    Passive = true,
                    Type = "ptp",
                    Cost = "1",
                    Priority = "1",
                })));

        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);
        Assert.Equal(5, enriched.SupersededTemplates.Count);

        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(
            enriched,
            ToRows(inventory));
        Assert.Equal(5, report.KnownLegacy.Count);
        Assert.Empty(report.Unknown);
    }

    /// <summary>
    /// Scenario: a live candidate that is not covered by the consolidated prefix: a different area, a network outside the management /24, an unrelated 192.0.2.0/24, or a /16 larger than the consolidated prefix.
    /// Expected: classification throws <c>InvalidOperationException</c> mentioning "unknown/unrelated" and "No deletion was attempted".
    /// Invariant: only templates contained in the consolidated prefix and area can be treated as obsolete; anything else blocks cleanup.
    /// </summary>
    [Theory]
    [InlineData("different_area")]
    [InlineData("outside_mgmt_24")]
    [InlineData("unknown_outside")]
    [InlineData("larger_than_consolidated")]
    public void ConsolidatedPrefix_NegativeCases_RemainUnknown(string caseName)
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        OspfTemplateInventoryRow candidate = caseName switch
        {
            "different_area" => SiteRow("*U", "other-site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"),
            "outside_mgmt_24" => SiteRow("*U", "site-v2", "Bridge-OTHER", "10.16.50.0/24"),
            "unknown_outside" => SiteRow("*U", "site-v2", "Bridge-OTHER", "192.0.2.0/24"),
            "larger_than_consolidated" => SiteRow("*U", "site-v2", "all", "10.16.0.0/16"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

        List<OspfTemplateInventoryRow> inventory = PlannedRows(plan).Append(candidate).ToList();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, inventory));
        Assert.Contains("unknown/unrelated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: a contained 10.16.11.0/27 template differs from the consolidated one by cost, authentication, type, passive flag, or has a blank interfaces value.
    /// Expected: it is still classified as a single known legacy row with a <c>Consolidated-prefix replacement</c> reason, and nothing is unknown.
    /// Invariant: containment in the consolidated prefix, not attribute equality, decides that a subdivision is obsolete.
    /// </summary>
    [Theory]
    [InlineData("different_cost")]
    [InlineData("different_auth")]
    [InlineData("different_type")]
    [InlineData("different_passive")]
    [InlineData("empty_interfaces")]
    public void ConsolidatedPrefix_AttributeDifferences_StillObsoleteWhenContained(string caseName)
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        OspfTemplateInventoryRow candidate = caseName switch
        {
            "different_cost" => SiteRow("*U", "site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27", cost: "10"),
            "different_auth" => SiteRow(
                "*U",
                "site-v2",
                "Bridge-VLAN-99_MGMT",
                "10.16.11.0/27",
                auth: "md5",
                authKeyPresent: true),
            "different_type" => SiteRow("*U", "site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27", type: "nbma"),
            "different_passive" => SiteRow(
                "*U",
                "site-v2",
                "Bridge-VLAN-99_MGMT",
                "10.16.11.0/27",
                passive: false),
            "empty_interfaces" => SiteRow("*U", "site-v2", " ", "10.16.11.0/27"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(
            plan,
            PlannedRows(plan).Append(candidate).ToList());
        Assert.Single(report.KnownLegacy);
        Assert.Empty(report.Unknown);
        Assert.Contains("Consolidated-prefix replacement", report.KnownLegacy[0].Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: five valid management subdivisions plus one unrelated template (192.0.2.0/24 on Bridge-OTHER).
    /// Expected: classification throws <c>InvalidOperationException</c> mentioning "No deletion was attempted" and 192.0.2.0/24.
    /// Invariant: a single unknown template aborts cleanup entirely, even when other rows are valid legacy.
    /// </summary>
    [Fact]
    public void Safety_OneUnknownPreventsAllDeletion()
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        List<OspfTemplateInventoryRow> inventory = PlannedRows(plan)
            .Concat(FiveMgmtSubdivisions.Select((x, i) =>
                SiteRow($"*{i + 3}", "site-v2", x.Interfaces, x.Networks)))
            .Append(SiteRow("*BAD", "site-v2", "Bridge-OTHER", "192.0.2.0/24"))
            .ToList();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, inventory));
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("192.0.2.0/24", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: the plan has an empty superseded list (so no recorded fingerprint can match), but the live 10.16.11.0/27 row is compatible with the consolidated prefix.
    /// Expected: <c>TryExplainLiveSupersession</c> still returns true.
    /// Invariant: a changed or missing provenance fingerprint does not stop desired-state supersession of a compatible live row.
    /// </summary>
    [Fact]
    public void Safety_ChangedFingerprint_NoLongerMatchesRecordedProvenance_StillDesiredStateIfCompatible()
    {
        // Recorded provenance for a /27, live row still compatible via desired-state even if
        // recorded fingerprint would miss due to empty superseded list.
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        OspfTemplateInventoryRow live = SiteRow(
            "*3",
            "site-v2",
            "Bridge-VLAN-99_MGMT",
            "10.16.11.0/27",
            priority: "1");
        Assert.True(
            OspfLiveSupersededProvenance.TryExplainLiveSupersession(live, plan, out _));
    }

    /// <summary>
    /// Scenario: the post-cleanup inventory contains only the planned templates.
    /// Expected: no legacy or unknown rows; the consolidated site network is present and none of the five subdivision networks remain.
    /// Invariant: the end state has the consolidated template and no more-specific management subdivisions.
    /// </summary>
    [Fact]
    public void Safety_FinalInventory_HasConsolidated_AndNoSubdivisions()
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        List<OspfTemplateInventoryRow> final = PlannedRows(plan).ToList();
        OspfTemplateClassificationReport report =
            OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, final);
        Assert.Empty(report.KnownLegacy);
        Assert.Empty(report.Unknown);
        Assert.Contains(
            final,
            r => OspfNetworkList.SetsEqual(r.Networks, plan.PassiveSiteNetworks[0].NetworkCidr));
        Assert.DoesNotContain(
            final,
            r => FiveMgmtSubdivisions.Any(s => OspfNetworkList.SetsEqual(r.Networks, s.Networks)));
    }

    /// <summary>
    /// Scenario: management-summarisation postconditions are checked first against an inventory still holding five subdivisions, then against a clean inventory.
    /// Expected: the dirty inventory yields an error mentioning "obsolete more-specific management"; the clean one yields no errors.
    /// Invariant: verification fails until every obsolete management subdivision has been removed.
    /// </summary>
    [Fact]
    public void Verification_FailsWhileSubdivisionsRemain_SucceedsWhenGone()
    {
        OspfMigrationPlan plan = DesiredPlan(superseded: []);
        OspfV7Inventory dirty = InventoryWith(
            PlannedTemplateRows(plan).Concat(FiveMgmtSubdivisions.Select((x, i) =>
                new OspfInterfaceTemplateRow
                {
                    Id = $"*{i + 3}",
                    Area = "site-v2",
                    Interfaces = x.Interfaces,
                    Networks = x.Networks,
                    Passive = true,
                    Type = "ptp",
                    Cost = "1",
                    Priority = "1",
                })));

        Assert.Contains(
            OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, dirty),
            e => e.Contains("obsolete more-specific management", StringComparison.OrdinalIgnoreCase));

        OspfV7Inventory clean = InventoryWith(PlannedTemplateRows(plan));
        Assert.Empty(OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, clean));
    }

    /// <summary>
    /// Builds a fully populated Tommy-style migration plan with target names, loopback, areas, templates and the
    /// supplied superseded identities and site networks.
    /// </summary>
    private static OspfMigrationPlan DesiredPlan(
        IReadOnlyList<OspfSupersededTemplateIdentity>? superseded,
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
            SupersededTemplates = superseded ?? [],
            ValidationErrors = [],
            NormalisationNotes = [],
            ContentHash = "test",
        };

    /// <summary>Builds inventory rows that mirror the templates a migration plan intends to create.</summary>
    /// <param name="plan">Plan to mirror.</param>
    private static IEnumerable<OspfTemplateInventoryRow> PlannedRows(OspfMigrationPlan plan) =>
    [
        new OspfTemplateInventoryRow
        {
            Id = "*1",
            Area = plan.ActiveBackhauls[0].AreaName,
            Interfaces = plan.ActiveBackhauls[0].Interface,
            Networks = plan.ActiveBackhauls[0].NetworkCidr,
            Type = "ptp",
            Cost = "10",
            Passive = false,
            AreaInstanceName = plan.InstanceName,
        },
        new OspfTemplateInventoryRow
        {
            Id = "*2",
            Area = plan.PassiveLoopbacks[0].AreaName,
            Interfaces = plan.PassiveLoopbacks[0].Interface,
            Networks = plan.PassiveLoopbacks[0].NetworkCidr,
            Passive = true,
            AreaInstanceName = plan.InstanceName,
        },
        new OspfTemplateInventoryRow
        {
            Id = "*A",
            Area = plan.PassiveSiteNetworks[0].AreaName,
            Interfaces = "all",
            Networks = plan.PassiveSiteNetworks[0].NetworkCidr,
            Passive = true,
            Type = "broadcast",
            Cost = "1",
            Priority = "1",
            AreaInstanceName = plan.InstanceName,
        },
    ];

    /// <summary>Builds interface-template rows that mirror the templates a migration plan intends to create.</summary>
    /// <param name="plan">Plan to mirror.</param>
    private static IEnumerable<OspfInterfaceTemplateRow> PlannedTemplateRows(OspfMigrationPlan plan) =>
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
    ];

    /// <summary>Creates a site-network template inventory row with overridable attributes.</summary>
    /// <param name="id">Template identifier.</param>
    /// <param name="area">Area name.</param>
    /// <param name="interfaces">Interface list.</param>
    /// <param name="networks">Network list.</param>
    /// <param name="passive">Whether the template is passive.</param>
    /// <param name="type">Network type.</param>
    /// <param name="cost">Interface cost.</param>
    /// <param name="priority">Router priority.</param>
    /// <param name="auth">Authentication type, or null for none.</param>
    /// <param name="authKeyPresent">Whether an authentication key is configured (a fingerprint is derived when true).</param>
    /// <param name="hello">Hello interval, or null for the default.</param>
    /// <param name="areaInstance">OSPF instance the area belongs to.</param>
    private static OspfTemplateInventoryRow SiteRow(
        string id,
        string area,
        string interfaces,
        string networks,
        bool passive = true,
        string? type = "ptp",
        string? cost = "1",
        string? priority = "1",
        string? auth = null,
        bool authKeyPresent = false,
        string? hello = null,
        string? areaInstance = "default-v2") =>
        new()
        {
            Id = id,
            Area = area,
            Interfaces = interfaces,
            Networks = networks,
            Passive = passive,
            Type = type,
            Cost = cost,
            Priority = priority,
            Auth = auth,
            AuthKeyPresent = authKeyPresent,
            AuthKeyFingerprint = authKeyPresent
                ? OspfTemplateSemanticIdentity.FingerprintAuthKey("secret")
                : null,
            HelloInterval = hello,
            AreaInstanceName = areaInstance,
        };

    /// <summary>Builds a RouterOS 7 OSPF inventory (instance, areas) holding the supplied templates.</summary>
    /// <param name="templates">Interface templates to include.</param>
    private static OspfV7Inventory InventoryWith(IEnumerable<OspfInterfaceTemplateRow> templates) =>
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
            Templates = templates.ToList(),
        };

    /// <summary>Converts an inventory's templates to provenance-aware inventory rows.</summary>
    /// <param name="inventory">Inventory whose templates are converted.</param>
    private static List<OspfTemplateInventoryRow> ToRows(OspfV7Inventory inventory)
    {
        Dictionary<string, string> map = OspfLiveSupersededProvenance.BuildAreaInstanceMap(inventory.Areas);
        return inventory.Templates
            .Select(t => OspfLiveSupersededProvenance.ToInventoryRow(t, map))
            .ToList();
    }
}
