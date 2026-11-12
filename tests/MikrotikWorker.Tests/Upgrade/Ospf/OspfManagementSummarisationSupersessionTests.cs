using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Generic regression coverage for management-network summarisation provenance, cleanup
/// classification, and verification postconditions. Fixtures are sanitised — no device-specific
/// harnesses.
/// </summary>
public sealed class OspfManagementSummarisationSupersessionTests
{
    private static readonly string[] SubdividedMgmt =
    [
        "10.16.11.0/27",
        "10.16.11.32/27",
        "10.16.11.64/26",
        "10.16.11.128/26",
        "10.16.11.192/26",
    ];

    [Fact]
    public void Enrich_FiveSubdividedMgmtPrefixes_BecomeKnownLegacy()
    {
        OspfMigrationPlan plan = ConsolidatedPlan();
        OspfV7Inventory inventory = InventoryWith(
            PlannedRows(plan).Concat(SubdividedMgmtTemplates(plan.PassiveSiteNetworks[0].AreaName)));

        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);
        Assert.Equal(5, enriched.SupersededTemplates.Count);

        List<OspfTemplateInventoryRow> rows = ToRows(inventory);
        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(enriched, rows);
        Assert.Equal(5, report.KnownLegacy.Count);
        Assert.Empty(report.Unknown);
    }

    [Fact]
    public void Enrich_MultipleInterfaces_SameMgmt24_AreSuperseded()
    {
        OspfMigrationPlan plan = ConsolidatedPlan();
        OspfV7Inventory inventory = InventoryWith(
            PlannedRows(plan).Concat(
            [
                SiteTemplate("*3", plan.PassiveSiteNetworks[0].AreaName, "Bridge-VLAN-99_MGMT", "10.16.11.0/27"),
                SiteTemplate("*4", plan.PassiveSiteNetworks[0].AreaName, "Bridge-VLAN-197_MGMT", "10.16.11.32/27"),
            ]));

        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);
        Assert.Equal(2, enriched.SupersededTemplates.Count);
        Assert.Contains(enriched.SupersededTemplates, s => s.Interfaces == "Bridge-VLAN-99_MGMT");
        Assert.Contains(enriched.SupersededTemplates, s => s.Interfaces == "Bridge-VLAN-197_MGMT");
    }

    [Fact]
    public void Enrich_ExistingConsolidated24_IsNotSuperseded()
    {
        OspfMigrationPlan plan = ConsolidatedPlan();
        OspfV7Inventory inventory = InventoryWith(PlannedRows(plan));

        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);
        Assert.Empty(enriched.SupersededTemplates);

        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(
            enriched,
            ToRows(inventory));
        Assert.Empty(report.KnownLegacy);
        Assert.Empty(report.Unknown);
        Assert.Single(report.SiteMatchers);
    }

    [Fact]
    public void Enrich_UnrelatedOutsideAggregate_RemainsUnknown()
    {
        OspfMigrationPlan plan = ConsolidatedPlan();
        OspfV7Inventory inventory = InventoryWith(
            PlannedRows(plan).Append(
                SiteTemplate("*U", plan.PassiveSiteNetworks[0].AreaName, "Bridge-OTHER", "192.0.2.0/24")));

        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);
        Assert.Empty(enriched.SupersededTemplates);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfTemplateCleanupClassifier.ClassifyOrThrow(enriched, ToRows(inventory)));
        Assert.Contains("unknown/unrelated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrich_SamePrefixUnrelatedArea_RemainsUnknown()
    {
        OspfMigrationPlan plan = ConsolidatedPlan();
        OspfV7Inventory inventory = InventoryWith(
            PlannedRows(plan).Append(
                SiteTemplate("*U", "other-site-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27")));

        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);
        Assert.Empty(enriched.SupersededTemplates);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfTemplateCleanupClassifier.ClassifyOrThrow(enriched, ToRows(inventory)));
        Assert.Contains("unknown/unrelated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Enrich_ActiveMoreSpecificUnderConsolidated_IsSuperseded()
    {
        OspfMigrationPlan plan = ConsolidatedPlan();
        OspfInterfaceTemplateRow active = SiteTemplate(
            "*X",
            plan.PassiveSiteNetworks[0].AreaName,
            "Bridge-VLAN-99_MGMT",
            "10.16.11.0/27");
        active = CloneTemplate(active, passive: false, type: "ptp");

        OspfV7Inventory inventory = InventoryWith(PlannedRows(plan).Append(active));
        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);
        Assert.Single(enriched.SupersededTemplates);
    }

    [Fact]
    public void Enrich_IsIdempotent_WhenAlreadyEnriched()
    {
        OspfMigrationPlan plan = ConsolidatedPlan();
        OspfV7Inventory inventory = InventoryWith(
            PlannedRows(plan).Concat(SubdividedMgmtTemplates(plan.PassiveSiteNetworks[0].AreaName)));

        OspfMigrationPlan once = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);
        OspfMigrationPlan twice = OspfLiveSupersededProvenance.EnrichFromLiveInventory(once, inventory);
        Assert.Equal(5, once.SupersededTemplates.Count);
        Assert.Equal(5, twice.SupersededTemplates.Count);
    }

    [Fact]
    public void RemapPlan_RemapsSupersededAreaAndInstance()
    {
        OspfMigrationPlan plan = ConsolidatedPlan(logicalSite: "SITE.Logical");
        plan = ClonePlan(
            plan,
            superseded:
            [
                OspfTemplateSemanticIdentity.FromInventoryRow(
                    new OspfTemplateInventoryRow
                    {
                        Id = "*3",
                        Area = "SITE.Logical",
                        Interfaces = "Bridge-VLAN-99_MGMT",
                        Networks = "10.16.11.0/27",
                        Passive = true,
                        AreaInstanceName = "default",
                    }),
            ]);

        OspfLiveIdentityBinding binding = new()
        {
            LogicalInstanceName = "default",
            LiveInstanceName = "default-v2",
            LiveInstanceId = "*I",
            LiveRouterId = "10.16.0.11",
            DerivedLoopbackRouterId = "10.16.0.11",
            LoopbackInterfaceName = "Loopback",
            Areas = new Dictionary<string, OspfLiveAreaBinding>(StringComparer.OrdinalIgnoreCase)
            {
                ["backbone"] = new()
                {
                    LogicalName = "backbone",
                    LiveName = "backbone-v2",
                    AreaId = "0.0.0.0",
                    LiveObjectId = "*B",
                },
                ["SITE.Logical"] = new()
                {
                    LogicalName = "SITE.Logical",
                    LiveName = "SITE.Logical-v2",
                    AreaId = "10.16.0.11",
                    LiveObjectId = "*S",
                },
            },
        };

        OspfMigrationPlan remapped = OspfLiveIdentityResolver.RemapPlan(plan, binding);
        OspfSupersededTemplateIdentity row = Assert.Single(remapped.SupersededTemplates);
        Assert.Equal("SITE.Logical-v2", row.AreaName);
        Assert.Equal("default-v2", row.AreaInstanceName);
        Assert.Equal("SITE.Logical-v2", remapped.PassiveSiteNetworks[0].AreaName);
    }

    [Fact]
    public void Verification_Fails_WhenMgmtSummaryAbsentFromSiteMatcher()
    {
        OspfMigrationPlan plan = ClonePlan(
            ConsolidatedPlan(),
            sites:
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "site-v2",
                    Interfaces = "all",
                    NetworkCidr = "100.64.0.0/10,172.16.0.0/12",
                    Passive = true,
                },
            ]);

        IReadOnlyList<string> errors = OspfV7Reconciler_Verify(
            plan,
            InventoryWith(PlannedRows(ConsolidatedPlan())));
        Assert.Contains(
            errors,
            e => e.Contains("do not cover derived management summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Verification_AcceptsBroaderSiteCoverage_WithMgmtAreaRange()
    {
        OspfMigrationPlan plan = ClonePlan(
            ConsolidatedPlan(),
            sites:
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "site-v2",
                    Interfaces = "all",
                    NetworkCidr = "10.16.0.0/16,100.64.0.0/10,172.16.0.0/12",
                    Passive = true,
                },
            ]);

        OspfV7Inventory inventory = InventoryWith(
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
                Area = "site-v2",
                Interfaces = "all",
                Networks = "10.16.0.0/16,100.64.0.0/10,172.16.0.0/12",
                Passive = true,
            },
        ]);

        Assert.Empty(OspfV7Reconciler_Verify(plan, inventory));
    }

    [Fact]
    public void Enrich_BroaderPlannedAggregate_RemovesContainedMoreSpecific()
    {
        // When the planned consolidated site includes a broader aggregate, same-area more-specific
        // prefixes under that aggregate are obsolete by consolidated-prefix containment.
        OspfMigrationPlan plan = ClonePlan(
            ConsolidatedPlan(),
            sites:
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "site-v2",
                    Interfaces = "all",
                    NetworkCidr = "10.16.0.0/16,100.64.0.0/10,172.16.0.0/12",
                    Passive = true,
                },
            ]);

        OspfV7Inventory inventory = InventoryWith(
            PlannedRows(plan).Append(
                SiteTemplate("*U", "site-v2", "Bridge-OTHER", "10.16.50.0/24")));

        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);
        Assert.Single(enriched.SupersededTemplates);
        OspfTemplateClassificationReport report =
            OspfTemplateCleanupClassifier.ClassifyOrThrow(enriched, ToRows(inventory));
        Assert.Single(report.KnownLegacy);
        Assert.Empty(report.Unknown);
    }

    [Theory]
    [InlineData("legitimate_auto_converted_ptp_subdivision", true)]
    [InlineData("different_cost", true)]
    [InlineData("different_network_type", true)]
    [InlineData("different_authentication", true)]
    [InlineData("invalid_interface_matching_empty", true)]
    [InlineData("different_passive_active_state", true)]
    [InlineData("additional_hello_interval", true)]
    [InlineData("same_prefix_different_area", false)]
    [InlineData("outside_consolidated_prefix", false)]
    public void Enrich_ConsolidatedPrefixContainment_ClassifiesObsolete(string caseName, bool expectSuperseded)
    {
        OspfInterfaceTemplateRow candidate = CreateSemanticCaseCandidate(caseName);
        OspfMigrationPlan plan = ConsolidatedPlan();

        OspfV7Inventory inventory = InventoryWith(PlannedRows(plan).Append(candidate));
        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, inventory);

        if (expectSuperseded)
        {
            Assert.Single(enriched.SupersededTemplates);
            OspfTemplateClassificationReport report =
                OspfTemplateCleanupClassifier.ClassifyOrThrow(enriched, ToRows(inventory));
            Assert.Single(report.KnownLegacy);
            Assert.Empty(report.Unknown);
            return;
        }

        Assert.Empty(enriched.SupersededTemplates);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfTemplateCleanupClassifier.ClassifyOrThrow(enriched, ToRows(inventory)));
        Assert.Contains("unknown/unrelated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
    }

    private static OspfInterfaceTemplateRow CreateSemanticCaseCandidate(string caseName)
    {
        const string area = "site-v2";
        OspfInterfaceTemplateRow baseline =
            SiteTemplate("*L", area, "Bridge-VLAN-99_MGMT", "10.16.11.0/27", type: "ptp");
        return caseName switch
        {
            "legitimate_auto_converted_ptp_subdivision" => baseline,
            "different_cost" => CloneTemplate(baseline, cost: "10"),
            "different_network_type" => CloneTemplate(baseline, type: "nbma"),
            "different_authentication" => CloneTemplate(baseline, auth: "md5", authKey: "secret"),
            "invalid_interface_matching_empty" => CloneTemplate(baseline, interfaces: " "),
            "different_passive_active_state" => CloneTemplate(baseline, passive: false),
            "additional_hello_interval" => CloneTemplate(baseline, helloInterval: "5s"),
            "same_prefix_different_area" => SiteTemplate(
                "*X",
                "other-site-v2",
                "Bridge-VLAN-99_MGMT",
                "10.16.11.0/27",
                type: "ptp"),
            "outside_consolidated_prefix" => SiteTemplate(
                "*U",
                area,
                "Bridge-OTHER",
                "10.16.50.0/24",
                type: "ptp"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown semantic case."),
        };
    }

    [Fact]
    public void Planner_DoesNotInjectMgmt24_WhenBroaderSiteMatcherAlreadyCoversIt()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = System.Net.IPAddress.Parse("10.0.0.1"),
            Identity = "BR01.POP099.016.Example",
            RouterOsVersion = "6.49.23",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            Instances = [new OspfInstanceRow { Name = "default", RouterId = "10.16.0.99" }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Type = "default" },
                new OspfAreaRow { Name = "site", AreaId = "10.16.0.99", Type = "stub", DefaultCost = "1" },
            ],
            Networks =
            [
                new OspfNetworkRow { Area = "backbone", Network = "10.16.248.0/30" },
                new OspfNetworkRow { Area = "backbone", Network = "10.16.0.99/32" },
                // Broader than management /24 but must not cover the backhaul /30.
                new OspfNetworkRow { Area = "site", Network = "10.16.96.0/19" },
            ],
            Interfaces =
            [
                new OspfInterfaceRow { Interface = "ether1", NetworkType = "ptp", Cost = "10" },
            ],
            IpAddresses =
            [
                new OspfIpAddressRow { Address = "10.16.0.99/32", Network = "10.16.0.99", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.248.1/30", Network = "10.16.248.0", Interface = "ether1" },
                new OspfIpAddressRow { Address = "10.16.99.1/24", Network = "10.16.99.0", Interface = "bridge-mgmt" },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow { Area = "site", Range = "10.16.96.0/19" },
            ],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("10.16.99.0/24", result.Plan.DerivedManagementCidr);
        Assert.DoesNotContain(
            "10.16.99.0/24",
            result.Plan.PassiveSiteNetworks[0].NetworkCidr,
            StringComparison.Ordinal);
        Assert.Contains("10.16.96.0/19", result.Plan.PassiveSiteNetworks[0].NetworkCidr, StringComparison.Ordinal);
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("not injecting a duplicate /24", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Plan.AreaRanges,
            r => r.RangeCidr == "10.16.99.0/24" && r.Comment == "MGMT");
    }

    [Fact]
    public void Verification_Fails_WhenObsoleteMgmtTemplatesRemain()
    {
        OspfMigrationPlan plan = ConsolidatedPlan();
        OspfV7Inventory inventory = InventoryWith(
            PlannedRows(plan).Concat(SubdividedMgmtTemplates(plan.PassiveSiteNetworks[0].AreaName)));

        IReadOnlyList<string> errors = OspfV7Reconciler_Verify(plan, inventory);
        Assert.Contains(
            errors,
            e => e.Contains("obsolete more-specific management", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Verification_Succeeds_WhenSummarisationConfigured()
    {
        OspfMigrationPlan plan = ConsolidatedPlan();
        OspfV7Inventory inventory = InventoryWith(PlannedRows(plan));
        Assert.Empty(OspfV7Reconciler_Verify(plan, inventory));
    }

    [Fact]
    public void EndToEnd_PlannerPlusEnrich_SubdividedToConsolidated()
    {
        // ROS6-style plan intent: consolidated /24 already in PassiveSiteNetworks (no live subdivided
        // provenance). Live ROS7 inventory still has five subdivided templates — enrich + classify
        // must accept cleanup, and verification must fail until they are gone.
        OspfMigrationPlan plan = ConsolidatedPlan(superseded: []);
        Assert.Empty(plan.SupersededTemplates);

        OspfV7Inventory partial = InventoryWith(
            PlannedRows(plan).Concat(SubdividedMgmtTemplates(plan.PassiveSiteNetworks[0].AreaName)));
        OspfMigrationPlan enriched = OspfLiveSupersededProvenance.EnrichFromLiveInventory(plan, partial);

        OspfTemplateClassificationReport duringCleanup =
            OspfTemplateCleanupClassifier.ClassifyOrThrow(enriched, ToRows(partial));
        Assert.Equal(5, duringCleanup.KnownLegacy.Count);
        Assert.Empty(duringCleanup.Unknown);

        Assert.NotEmpty(OspfV7Reconciler_Verify(enriched, partial));

        OspfV7Inventory finalState = InventoryWith(PlannedRows(plan));
        Assert.Empty(OspfV7Reconciler_Verify(enriched, finalState));
        OspfTemplateClassificationReport afterCleanup =
            OspfTemplateCleanupClassifier.ClassifyOrThrow(enriched, ToRows(finalState));
        Assert.Empty(afterCleanup.KnownLegacy);
        Assert.Empty(afterCleanup.Unknown);
    }

    [Fact]
    public void Planner_FoldsSubdividedMgmt_BeforeEnrichment_WhenRangePresent()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = System.Net.IPAddress.Parse("10.0.0.1"),
            Identity = "BR01.POP011.016.Example",
            RouterOsVersion = "7.25.1",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.16.0.11", Version = "2" }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone-v2", AreaId = "0.0.0.0", Instance = "default-v2", Type = "default" },
                new OspfAreaRow
                {
                    Name = "BR01.POP011.016.Example-v2",
                    AreaId = "10.16.0.11",
                    Instance = "default-v2",
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow
                {
                    Area = "BR01.POP011.016.Example-v2",
                    Range = "10.16.11.0/24",
                    Comment = "MGMT",
                },
            ],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone-v2",
                    Interfaces = "eth-bh",
                    Networks = "10.16.248.76/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone-v2",
                    Interfaces = "Loopback",
                    Networks = "10.16.0.11/32",
                    Passive = true,
                },
                ..SubdividedMgmt.Select((cidr, i) => new OspfInterfaceTemplateRow
                {
                    Id = $"*{i + 3}",
                    Area = "BR01.POP011.016.Example-v2",
                    Interfaces = i == 1 ? "Bridge-VLAN-197_MGMT" : "Bridge-VLAN-99_MGMT",
                    Networks = cidr,
                    Passive = true,
                    Type = "ptp",
                }),
                new OspfInterfaceTemplateRow
                {
                    Area = "BR01.POP011.016.Example-v2",
                    Interfaces = "all",
                    Networks = "100.64.0.0/10,172.16.0.0/12",
                    Passive = true,
                },
            ],
            IpAddresses =
            [
                new OspfIpAddressRow { Address = "10.16.248.78/30", Network = "10.16.248.76", Interface = "eth-bh" },
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "Bridge-VLAN-99_MGMT" },
            ],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("10.16.11.0/24", result.Plan.DerivedManagementCidr);
        Assert.Contains("10.16.11.0/24", result.Plan.PassiveSiteNetworks[0].NetworkCidr, StringComparison.Ordinal);
        Assert.True(result.Plan.SupersededTemplates.Count >= 5);
        Assert.All(
            SubdividedMgmt,
            cidr => Assert.Contains(
                result.Plan.SupersededTemplates,
                s => OspfNetworkList.SetsEqual(s.NetworkCidr, cidr)));
    }

    private static IReadOnlyList<string> OspfV7Reconciler_Verify(
        OspfMigrationPlan plan,
        OspfV7Inventory inventory) =>
        OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, inventory);

    private static OspfMigrationPlan ConsolidatedPlan(
        string logicalSite = "site-v2",
        IReadOnlyList<OspfSupersededTemplateIdentity>? superseded = null) =>
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
            TargetSiteAreaName = "BR01.POP011.016.Example-v2",
            LoopbackInterfaceName = "Loopback",
            LoopbackCidr = "10.16.0.11/32",
            DerivedManagementCidr = "10.16.11.0/24",
            Areas =
            [
                new OspfPlanArea { Name = logicalSite == "SITE.Logical" ? "backbone" : "backbone-v2", AreaId = "0.0.0.0", IsBackbone = true, TargetType = "default" },
                new OspfPlanArea
                {
                    Name = logicalSite,
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
                    AreaName = logicalSite,
                    RangeCidr = "10.16.11.0/24",
                    Comment = "MGMT",
                },
            ],
            ActiveBackhauls =
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = logicalSite == "SITE.Logical" ? "backbone" : "backbone-v2",
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
                    AreaName = logicalSite == "SITE.Logical" ? "backbone" : "backbone-v2",
                    Interface = "Loopback",
                    NetworkCidr = "10.16.0.11/32",
                    Passive = true,
                },
            ],
            PassiveSiteNetworks =
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = logicalSite,
                    Interfaces = "all",
                    NetworkCidr = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
                    Passive = true,
                },
            ],
            // Empty by default: cleanup must establish KnownLegacy via desired-state replacement,
            // not only via capture-time superseded fingerprints.
            SupersededTemplates = superseded ?? [],
            ValidationErrors = [],
            NormalisationNotes = [],
            ContentHash = "test",
        };

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
            TargetInstanceName = plan.TargetInstanceName,
            TargetBackboneAreaName = plan.TargetBackboneAreaName,
            TargetSiteAreaName = plan.TargetSiteAreaName,
            LoopbackInterfaceName = plan.LoopbackInterfaceName,
            LoopbackCidr = plan.LoopbackCidr,
            DerivedManagementCidr = plan.DerivedManagementCidr,
            Areas = plan.Areas,
            AreaRanges = sites is null
                ? plan.AreaRanges
                : plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = sites ?? plan.PassiveSiteNetworks,
            SupersededTemplates = superseded ?? plan.SupersededTemplates,
            ValidationErrors = plan.ValidationErrors,
            NormalisationNotes = plan.NormalisationNotes,
        };

    private static IEnumerable<OspfInterfaceTemplateRow> PlannedRows(OspfMigrationPlan plan) =>
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

    private static IEnumerable<OspfInterfaceTemplateRow> SubdividedMgmtTemplates(string area) =>
        SubdividedMgmt.Select((cidr, i) => SiteTemplate(
            $"*{i + 3}",
            area,
            i == 1 ? "Bridge-VLAN-197_MGMT" : "Bridge-VLAN-99_MGMT",
            cidr,
            type: "ptp"));

    private static OspfInterfaceTemplateRow SiteTemplate(
        string id,
        string area,
        string interfaces,
        string networks,
        bool passive = true,
        string? type = null) =>
        new()
        {
            Id = id,
            Area = area,
            Interfaces = interfaces,
            Networks = networks,
            Passive = passive,
            Type = type,
        };

    private static OspfInterfaceTemplateRow CloneTemplate(
        OspfInterfaceTemplateRow row,
        bool? passive = null,
        string? type = null,
        string? cost = null,
        string? auth = null,
        string? authKey = null,
        string? interfaces = null,
        string? helloInterval = null,
        string? prefixList = null) =>
        new()
        {
            Id = row.Id,
            Area = row.Area,
            Interfaces = interfaces ?? row.Interfaces,
            Networks = row.Networks,
            Passive = passive ?? row.Passive,
            Disabled = row.Disabled,
            Type = type ?? row.Type,
            Cost = cost ?? row.Cost,
            Auth = auth ?? row.Auth,
            AuthId = row.AuthId,
            AuthKey = authKey ?? row.AuthKey,
            AuthenticationKey = row.AuthenticationKey,
            Priority = row.Priority,
            UseBfd = row.UseBfd,
            HelloInterval = helloInterval ?? row.HelloInterval,
            DeadInterval = row.DeadInterval,
            RetransmitInterval = row.RetransmitInterval,
            TransmitDelay = row.TransmitDelay,
            InstanceId = row.InstanceId,
            PrefixList = prefixList ?? row.PrefixList,
            VlinkNeighborId = row.VlinkNeighborId,
            VlinkTransitArea = row.VlinkTransitArea,
            Comment = row.Comment,
        };

    private static OspfV7Inventory InventoryWith(IEnumerable<OspfInterfaceTemplateRow> templates)
    {
        List<OspfInterfaceTemplateRow> list = templates.ToList();
        string? site = list.FirstOrDefault(t => t.Passive && t.Networks?.Contains("10.16.11", StringComparison.Ordinal) == true)?.Area
            ?? "site-v2";
        string? backbone = list.FirstOrDefault(t => !t.Passive)?.Area ?? "backbone-v2";
        return new OspfV7Inventory
        {
            Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.16.0.11", Version = "2" }],
            Areas =
            [
                new OspfAreaRow { Name = backbone, AreaId = "0.0.0.0", Instance = "default-v2", Type = "default" },
                new OspfAreaRow { Name = site, AreaId = "10.16.0.11", Instance = "default-v2", Type = "stub", DefaultCost = "1" },
                new OspfAreaRow { Name = "other-site-v2", AreaId = "10.16.0.99", Instance = "default-v2", Type = "stub", DefaultCost = "1" },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow { Area = site, Range = "10.16.11.0/24", Comment = "MGMT" },
            ],
            Templates = list,
        };
    }

    private static List<OspfTemplateInventoryRow> ToRows(OspfV7Inventory inventory)
    {
        Dictionary<string, string> map = OspfLiveSupersededProvenance.BuildAreaInstanceMap(inventory.Areas);
        return inventory.Templates
            .Select(t => OspfLiveSupersededProvenance.ToInventoryRow(t, map))
            .ToList();
    }
}
