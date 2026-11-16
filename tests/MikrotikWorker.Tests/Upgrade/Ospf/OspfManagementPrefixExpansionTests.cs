// -----------------------------------------------------------------------------
// File: OspfManagementPrefixExpansionTests.cs
//
// Description:
// Same-management-interface expansion without unrestricted transitive merging;
// multi-prefix planner, provenance, verification and plan-copy regressions.
//
// Note: OspfIpAddressRow does not capture RouterOS address comments; acceptance
// is interface-based. Comment text such as "ODU DHCP MGMT Extra" is therefore
// provenance context for operators, not a code dependency.
//
// -----------------------------------------------------------------------------

using System.Net;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Cashane-style management expansion, transitive isolation, and dual-prefix propagation.
/// </summary>
public sealed class OspfManagementPrefixExpansionTests
{
    [Fact]
    public void Cashane_BridgeVlan99_Accepts_10_16_3_And_10_16_103()
    {
        OspfLoopbackRouterIdResult loopback = RequireLoopback("10.16.0.2");
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            CashaneProductionAddresses(),
            loopback,
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(
            ["10.16.103.0/24", "10.16.3.0/24"],
            result.ManagementCidrs.OrderBy(static s => s, StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(result.ManagementCidrs, c => c.StartsWith("10.16.0.0/", StringComparison.Ordinal));
        Assert.Contains(
            result.SelectionDiagnostics,
            d => d.StartsWith("accepted additional management expansion 10.16.103.0/24", StringComparison.Ordinal)
                 && d.Contains("Bridge-VLAN-99_MGMT", StringComparison.Ordinal));
    }

    [Fact]
    public void Cashane_Planner_IncludesBothPrefixes_InRangesMatchersAndNotes()
    {
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(CashanePlannerSnapshot());
        Assert.True(result.Plan.ValidationErrors.Count == 0, string.Join("; ", result.Plan.ValidationErrors));

        Assert.Contains("10.16.3.0/24", result.Plan.DerivedManagementCidrs);
        Assert.Contains("10.16.103.0/24", result.Plan.DerivedManagementCidrs);
        Assert.Equal(2, OspfMigrationPlanner.GetDerivedManagementCidrs(result.Plan).Count);

        Assert.Contains(
            result.Plan.AreaRanges,
            r => r.RangeCidr == "10.16.3.0/24"
                 && string.Equals(r.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Plan.AreaRanges,
            r => r.RangeCidr == "10.16.103.0/24"
                 && string.Equals(r.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase));

        string siteNetworks = string.Join(
            ',',
            result.Plan.PassiveSiteNetworks.SelectMany(s => OspfNetworkList.Split(s.NetworkCidr)));
        Assert.Contains("10.16.3.0/24", siteNetworks, StringComparison.Ordinal);
        Assert.Contains("10.16.103.0/24", siteNetworks, StringComparison.Ordinal);
        Assert.DoesNotContain("10.16.0.0/17", siteNetworks, StringComparison.Ordinal);

        Assert.Empty(
            OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(
                result.Plan,
                InventoryCoveringPlan(result.Plan)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Cashane_Acceptance_IsInterfaceBased_NotCommentDependent(int permutation)
    {
        // OspfIpAddressRow has no Comment property; RouterOS comments (including
        // "ODU DHCP MGMT Extra") are not inputs to Derive. Permute address order instead
        // and assert identical prefix sets — proving interface co-residence, not comment text.
        _ = permutation switch
        {
            0 => "ODU DHCP MGMT Extra",
            1 => "other operator note",
            _ => string.Empty,
        };

        List<OspfIpAddressRow> addresses = CashaneProductionAddresses().ToList();
        if (permutation == 1)
        {
            addresses.Reverse();
        }
        else if (permutation == 2)
        {
            addresses = addresses.OrderBy(static a => a.Address, StringComparer.Ordinal).ToList();
        }

        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            addresses,
            RequireLoopback("10.16.0.2"),
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(
            ["10.16.103.0/24", "10.16.3.0/24"],
            result.ManagementCidrs.OrderBy(static s => s, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void DirectSameInterface_MultiplePrefixes_Accepted()
    {
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.5.126/26", "10.16.5.64", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.5.190/26", "10.16.5.128", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.205.254/24", "10.16.205.0", "Bridge-VLAN-99_MGMT"),
            ],
            RequireLoopback("10.16.0.5"),
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(
            ["10.16.205.0/24", "10.16.5.0/24"],
            result.ManagementCidrs.OrderBy(static s => s, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void SharedBridgePrefix_AcrossInterfaces_DoesNotTransitivelyMergeDistinctExpansions()
    {
        // VLAN-A: P1,P2 · VLAN-B: P2,P3 — P1 must not inherit P3 via bridge P2.
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.10.1/24", "10.16.10.0", "Bridge-VLAN-A_MGMT"),
                Addr("10.16.20.1/24", "10.16.20.0", "Bridge-VLAN-A_MGMT"),
                Addr("10.16.20.2/24", "10.16.20.0", "Bridge-VLAN-B_MGMT"),
                Addr("10.16.30.1/24", "10.16.30.0", "Bridge-VLAN-B_MGMT"),
            ],
            RequireLoopback("10.16.0.2"),
            backhauls: []);

        Assert.False(result.Succeeded, "transitive merge via shared P2 must fail closed");
        Assert.Contains(result.Errors, e => e.Contains("unrelated", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.ManagementCidrs);
    }

    [Fact]
    public void ThreeInterfaceTransitiveChain_FailsClosed()
    {
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.1.1/24", "10.16.1.0", "Bridge-VLAN-A_MGMT"),
                Addr("10.16.2.1/24", "10.16.2.0", "Bridge-VLAN-A_MGMT"),
                Addr("10.16.2.2/24", "10.16.2.0", "Bridge-VLAN-B_MGMT"),
                Addr("10.16.3.1/24", "10.16.3.0", "Bridge-VLAN-B_MGMT"),
                Addr("10.16.3.2/24", "10.16.3.0", "Bridge-VLAN-C_MGMT"),
                Addr("10.16.4.1/24", "10.16.4.0", "Bridge-VLAN-C_MGMT"),
            ],
            RequireLoopback("10.16.0.2"),
            backhauls: []);

        Assert.False(result.Succeeded);
        Assert.Empty(result.ManagementCidrs);
    }

    [Fact]
    public void SubsetOnlySecondInterface_DoesNotBlockMaximalSameInterfaceExpansion()
    {
        // 199 holds {7,107}; 197 only reinforces {7} as a subset — unique maximal is {7,107}.
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.7.30/27", "10.16.7.0", "Bridge-VLAN-199_MGMT"),
                Addr("10.16.7.62/27", "10.16.7.32", "Bridge-VLAN-197_MGMT"),
                Addr("10.16.107.10/24", "10.16.107.0", "Bridge-VLAN-199_MGMT"),
            ],
            RequireLoopback("10.16.0.7"),
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Contains("10.16.7.0/24", result.ManagementCidrs);
        Assert.Contains("10.16.107.0/24", result.ManagementCidrs);
    }

    [Fact]
    public void GenuineAmbiguity_UnrelatedMgmtInterfaces_StillFailsClosed()
    {
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.2.30/27", "10.16.2.0", "Bridge-VLAN-199_MGMT"),
                Addr("10.16.22.126/26", "10.16.22.64", "Bridge-VLAN-197_MGMT"),
            ],
            RequireLoopback("10.16.0.2"),
            backhauls: []);

        Assert.False(result.Succeeded);
        Assert.Empty(result.ManagementCidrs);
    }

    [Fact]
    public void InputOrdering_DoesNotChangeAcceptedSet()
    {
        OspfIpAddressRow[] baseRows =
        [
            Addr("10.16.3.126/26", "10.16.3.64", "Bridge-VLAN-99_MGMT"),
            Addr("10.16.3.190/26", "10.16.3.128", "Bridge-VLAN-99_MGMT"),
            Addr("10.16.3.254/26", "10.16.3.192", "Bridge-VLAN-99_MGMT"),
            Addr("10.16.103.254/24", "10.16.103.0", "Bridge-VLAN-99_MGMT"),
        ];

        OspfLoopbackRouterIdResult loopback = RequireLoopback("10.16.0.2");
        List<string> forward = OspfSiteManagementSummary.Derive(baseRows, loopback, []).ManagementCidrs
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();
        List<string> reverse = OspfSiteManagementSummary.Derive(baseRows.Reverse().ToList(), loopback, []).ManagementCidrs
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(forward, reverse);
        Assert.Equal(["10.16.103.0/24", "10.16.3.0/24"], forward);
    }

    [Fact]
    public void DisabledAdditionalManagementAddress_IsExcludedFromExpansion()
    {
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.3.126/26", "10.16.3.64", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.3.190/26", "10.16.3.128", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.3.254/26", "10.16.3.192", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.103.254/24", "10.16.103.0", "Bridge-VLAN-99_MGMT", disabled: true),
            ],
            RequireLoopback("10.16.0.2"),
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(["10.16.3.0/24"], result.ManagementCidrs.ToList());
        Assert.Contains(
            result.IgnoredDisabledAddresses,
            s => s.Contains("10.16.103.254/24", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownAdminStateAdditionalAddress_IsExcludedFromExpansion()
    {
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.3.126/26", "10.16.3.64", "Bridge-VLAN-99_MGMT"),
                new OspfIpAddressRow
                {
                    Address = "10.16.103.254/24",
                    Network = "10.16.103.0",
                    Interface = "Bridge-VLAN-99_MGMT",
                    Disabled = false,
                    AdminState = OspfAdminState.Unknown,
                },
            ],
            RequireLoopback("10.16.0.2"),
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(["10.16.3.0/24"], result.ManagementCidrs.ToList());
    }

    [Fact]
    public void Provenance_SecondaryManagementPrefix_IsAuthoritativeBoundary()
    {
        Assert.True(OspfIpPrefix.TryParse("10.16.103.128/26", out OspfIpPrefix liveMoreSpecific));
        Assert.True(OspfIpPrefix.TryParse("10.16.3.0/24", out OspfIpPrefix primary));
        Assert.True(OspfIpPrefix.TryParse("10.16.103.0/24", out OspfIpPrefix secondary));
        Assert.True(OspfIpPrefix.TryParse("10.16.200.0/24", out OspfIpPrefix unrelated));

        IReadOnlyList<OspfIpPrefix> plannedNets = [primary, secondary];
        IReadOnlyList<OspfIpPrefix> liveUnder103 = [liveMoreSpecific];

        // Scalar primary alone must not authorise membership under the secondary aggregate.
        Assert.False(
            OspfLiveSupersededProvenance.TryExplainManagementMembership(
                liveUnder103,
                plannedNets,
                [primary],
                liveIsSpecific: true,
                out _));

        // Full DerivedManagementCidrs set treats secondary identically to primary.
        Assert.True(
            OspfLiveSupersededProvenance.TryExplainManagementMembership(
                liveUnder103,
                plannedNets,
                [primary, secondary],
                liveIsSpecific: true,
                out string reason));
        Assert.Contains("10.16.103.0/24", reason, StringComparison.Ordinal);

        Assert.False(
            OspfLiveSupersededProvenance.TryExplainManagementMembership(
                [unrelated],
                plannedNets,
                [primary, secondary],
                liveIsSpecific: true,
                out _));

        OspfMigrationPlan plan = DualMgmtPlan();
        Assert.Equal("10.16.3.0/24", plan.DerivedManagementCidr);
        IReadOnlyList<OspfIpPrefix> fromPlan =
            OspfLiveSupersededProvenance.ParseDerivedManagementBoundaries(plan);
        Assert.Contains(fromPlan, p => p.ToCidrString() == "10.16.103.0/24");
        Assert.Contains(fromPlan, p => p.ToCidrString() == "10.16.3.0/24");
    }

    [Fact]
    public void Verification_RequiresBothManagementPrefixes_Independently()
    {
        OspfMigrationPlan plan = DualMgmtPlan();
        OspfV7Inventory missing103 = InventoryCoveringPlan(plan);
        missing103 = new OspfV7Inventory
        {
            Instances = missing103.Instances,
            Areas = missing103.Areas,
            AreaRanges = missing103.AreaRanges.Where(r => r.Range != "10.16.103.0/24").ToList(),
            Templates = missing103.Templates,
        };

        IReadOnlyList<string> errors =
            OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, missing103);
        Assert.Contains(errors, e => e.Contains("10.16.103.0/24", StringComparison.Ordinal));

        Assert.Empty(
            OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(
                plan,
                InventoryCoveringPlan(plan)));
    }

    [Fact]
    public void DualPrefix_PlanCopies_PreserveFullSet()
    {
        OspfMigrationPlan plan = DualMgmtPlan();

        OspfMigrationPlan viaGet = new()
        {
            ImportId = plan.ImportId,
            JobId = plan.JobId,
            RouterIp = plan.RouterIp,
            Identity = plan.Identity,
            SourceRouterOsVersion = plan.SourceRouterOsVersion,
            Architecture = plan.Architecture,
            SourceKind = plan.SourceKind,
            InstanceName = plan.InstanceName,
            RouterId = plan.RouterId,
            TargetInstanceName = plan.TargetInstanceName,
            TargetBackboneAreaName = plan.TargetBackboneAreaName,
            TargetSiteAreaName = plan.TargetSiteAreaName,
            LoopbackInterfaceName = plan.LoopbackInterfaceName,
            LoopbackCidr = plan.LoopbackCidr,
            ObservedInScopeLoopbackAddresses = plan.ObservedInScopeLoopbackAddresses,
            IgnoredOutsideRangeLoopbackAddresses = plan.IgnoredOutsideRangeLoopbackAddresses,
            DerivedManagementCidr = plan.DerivedManagementCidr,
            DerivedManagementCidrs = OspfMigrationPlanner.GetDerivedManagementCidrs(plan),
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            SupersededTemplates = plan.SupersededTemplates,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = plan.NormalisationNotes,
            ValidationErrors = plan.ValidationErrors,
        };

        Assert.Equal(
            ["10.16.103.0/24", "10.16.3.0/24"],
            OspfMigrationPlanner.GetDerivedManagementCidrs(viaGet).OrderBy(static s => s, StringComparer.Ordinal).ToList());

        // Hash path also embeds the full set.
        string hash1 = OspfMigrationPlanner.ComputeContentHash(plan);
        string hash2 = OspfMigrationPlanner.ComputeContentHash(viaGet);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void DualPrefix_ReconciliationIdempotent_AndPreservesUnrelated()
    {
        OspfMigrationPlan plan = DualMgmtPlan();
        OspfV7Inventory first = InventoryCoveringPlan(plan);
        Assert.Empty(OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, first));

        // Second pass: identical inventory — still clean (idempotent verify).
        Assert.Empty(OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, first));

        // Partial: missing secondary range, then restored — both required again.
        OspfV7Inventory partial = new()
        {
            Instances = first.Instances,
            Areas = first.Areas,
            AreaRanges = first.AreaRanges.Where(r => r.Range != "10.16.103.0/24").ToList(),
            Templates = first.Templates,
        };
        Assert.Contains(
            OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, partial),
            e => e.Contains("10.16.103.0/24", StringComparison.Ordinal));
        Assert.Empty(
            OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(
                plan,
                InventoryCoveringPlan(plan)));

        // Unrelated passive template outside both aggregates remains unclassified as MGMT obsolete.
        OspfV7Inventory withUnrelated = InventoryCoveringPlan(plan);
        List<OspfInterfaceTemplateRow> templates = withUnrelated.Templates.ToList();
        templates.Add(new OspfInterfaceTemplateRow
        {
            Id = "*unrel",
            Area = "site-v2",
            Interfaces = "all",
            Networks = "10.99.0.0/16",
            Passive = true,
        });
        withUnrelated = new OspfV7Inventory
        {
            Instances = withUnrelated.Instances,
            Areas = withUnrelated.Areas,
            AreaRanges = withUnrelated.AreaRanges,
            Templates = templates,
        };

        Assert.DoesNotContain(
            OspfV7MigrationVerifier.VerifyManagementSummarisationPostconditions(plan, withUnrelated),
            e => e.Contains("10.99.0.0/16", StringComparison.Ordinal));
    }

    [Fact]
    public void TransportSlash30_StillExcluded_WhenOnMgmtNamedInterface()
    {
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.3.126/26", "10.16.3.64", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.248.1/30", "10.16.248.0", "Bridge-VLAN-99_MGMT"),
            ],
            RequireLoopback("10.16.0.2"),
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(["10.16.3.0/24"], result.ManagementCidrs.ToList());
    }

    private static OspfCaptureSnapshot CashanePlannerSnapshot() =>
        new()
        {
            ImportId = Guid.Empty,
            JobId = "cashane-mgmt-expansion",
            RouterIp = IPAddress.Parse("10.16.0.2"),
            Identity = "BR01.POP002.016.Cashane",
            RouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances =
            [
                new OspfInstanceRow { Name = "default", Version = "2", RouterId = "10.16.0.2" },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Name = "backbone",
                    AreaId = "0.0.0.0",
                    Instance = "default",
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Name = "BR01.POP002.016.Cashane",
                    AreaId = "10.16.0.2",
                    Instance = "default",
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "ether1",
                    Networks = "10.16.248.0/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "Loopback-OSPF",
                    Networks = "10.16.0.2/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "BR01.POP002.016.Cashane",
                    Interfaces = "all",
                    Networks = "10.16.3.0/24",
                    Passive = true,
                },
            ],
            IpAddresses = CashaneProductionAddresses()
                .Append(Addr("10.16.248.1/30", "10.16.248.0", "ether1"))
                .ToList(),
            Neighbours =
            [
                new OspfNeighbourRow
                {
                    Address = "10.16.248.2",
                    RouterId = "198.18.255.253",
                    State = "Full",
                    Interface = "ether1",
                },
            ],
        };

    private static OspfMigrationPlan DualMgmtPlan() =>
        new()
        {
            ImportId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            JobId = "cashane-dual",
            RouterIp = "10.16.0.2",
            Identity = "BR01.POP002.016.Cashane",
            SourceRouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            InstanceName = "default-v2",
            RouterId = "10.16.0.2",
            TargetInstanceName = "default-v2",
            TargetBackboneAreaName = "backbone-v2",
            TargetSiteAreaName = "site-v2",
            LoopbackInterfaceName = "Loopback-OSPF",
            LoopbackCidr = "10.16.0.2/32",
            DerivedManagementCidr = "10.16.3.0/24",
            DerivedManagementCidrs = ["10.16.3.0/24", "10.16.103.0/24"],
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
                    AreaName = "site-v2",
                    RangeCidr = "10.16.3.0/24",
                    Comment = OspfFleetNaming.ManagementRangeComment,
                    Advertise = true,
                },
                new OspfPlanAreaRange
                {
                    AreaName = "site-v2",
                    RangeCidr = "10.16.103.0/24",
                    Comment = OspfFleetNaming.ManagementRangeComment,
                    Advertise = true,
                },
            ],
            ActiveBackhauls =
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone-v2",
                    Interface = "ether1",
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
                    AreaName = "backbone-v2",
                    Interface = "Loopback-OSPF",
                    NetworkCidr = "10.16.0.2/32",
                    Passive = true,
                },
            ],
            PassiveSiteNetworks =
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "site-v2",
                    Interfaces = OspfSiteMatcherConsolidation.AllInterfaces,
                    NetworkCidr = "10.16.3.0/24,10.16.103.0/24",
                    Comment = OspfFleetNaming.ManagementRangeComment,
                    Passive = true,
                },
            ],
        };

    private static OspfV7Inventory InventoryCoveringPlan(OspfMigrationPlan plan) =>
        new()
        {
            Instances =
            [
                new OspfInstanceRow
                {
                    Name = plan.TargetInstanceName,
                    Version = "2",
                    RouterId = plan.RouterId,
                },
            ],
            Areas = plan.Areas
                .Select(a => new OspfAreaRow
                {
                    Name = a.Name,
                    AreaId = a.AreaId,
                    Instance = plan.TargetInstanceName,
                    Type = a.TargetType,
                })
                .ToList(),
            AreaRanges = plan.AreaRanges
                .Select(r => new OspfAreaRangeRow
                {
                    Area = r.AreaName,
                    Range = r.RangeCidr,
                    Comment = r.Comment,
                    Advertise = r.Advertise,
                })
                .ToList(),
            Templates =
            [
                .. plan.ActiveBackhauls.Select(bh => new OspfInterfaceTemplateRow
                {
                    Id = "*bh",
                    Area = bh.AreaName,
                    Interfaces = bh.Interface,
                    Networks = bh.NetworkCidr,
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                }),
                .. plan.PassiveLoopbacks.Select(l => new OspfInterfaceTemplateRow
                {
                    Id = "*lo",
                    Area = l.AreaName,
                    Interfaces = l.Interface,
                    Networks = l.NetworkCidr,
                    Passive = true,
                }),
                .. plan.PassiveSiteNetworks.Select(s => new OspfInterfaceTemplateRow
                {
                    Id = "*site",
                    Area = s.AreaName,
                    Interfaces = s.Interfaces,
                    Networks = s.NetworkCidr,
                    Passive = true,
                    Comment = s.Comment,
                }),
            ],
        };

    private static IReadOnlyList<OspfIpAddressRow> CashaneProductionAddresses() =>
    [
        Addr("10.16.0.2/32", "10.16.0.2", "Loopback-OSPF"),
        Addr("10.16.3.126/26", "10.16.3.64", "Bridge-VLAN-99_MGMT"),
        Addr("10.16.3.190/26", "10.16.3.128", "Bridge-VLAN-99_MGMT"),
        Addr("10.16.3.254/26", "10.16.3.192", "Bridge-VLAN-99_MGMT"),
        Addr("10.16.103.254/24", "10.16.103.0", "Bridge-VLAN-99_MGMT"),
    ];

    private static OspfLoopbackRouterIdResult RequireLoopback(string rid, string iface = "Loopback-OSPF")
    {
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(
            [Addr($"{rid}/32", rid, iface)]);
        Assert.True(loopback.Succeeded, loopback.Detail);
        return loopback;
    }

    private static OspfIpAddressRow Addr(
        string address,
        string network,
        string iface,
        bool disabled = false) =>
        new()
        {
            Address = address,
            Network = network,
            Interface = iface,
            Disabled = disabled,
            AdminState = disabled ? OspfAdminState.Disabled : OspfAdminState.Enabled,
        };
}
