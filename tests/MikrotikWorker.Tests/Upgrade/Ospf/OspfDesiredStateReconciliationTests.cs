using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Desired-state OSPF reconciliation: live templates are removed only when an explicit
/// replacement relationship to the planned consolidated configuration is established.
/// Fixtures are generic — no device-specific harnesses.
/// </summary>
public sealed class OspfDesiredStateReconciliationTests
{
    private static readonly (string Networks, string Interfaces)[] FiveMgmtSubdivisions =
    [
        ("10.16.11.0/27", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.32/27", "Bridge-VLAN-197_MGMT"),
        ("10.16.11.64/26", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.128/26", "Bridge-VLAN-99_MGMT"),
        ("10.16.11.192/26", "Bridge-VLAN-99_MGMT"),
    ];

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

    private static List<OspfTemplateInventoryRow> ToRows(OspfV7Inventory inventory)
    {
        Dictionary<string, string> map = OspfLiveSupersededProvenance.BuildAreaInstanceMap(inventory.Areas);
        return inventory.Templates
            .Select(t => OspfLiveSupersededProvenance.ToInventoryRow(t, map))
            .ToList();
    }
}
