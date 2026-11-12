// -----------------------------------------------------------------------------
// File: OspfFleetFixtureMatrixTests.cs
//
// Description:
// Regression matrix derived from the five authoritative C:\Temp RouterOS
// exports. Snapshots mirror captured inventory fields (not live API).
//
// -----------------------------------------------------------------------------

using System.Net;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Regression matrix derived from the five authoritative C:\Temp RouterOS exports.
/// Snapshots mirror captured inventory fields (not live API).
/// </summary>
public sealed class OspfFleetFixtureMatrixTests
{
    /// <summary>
    /// Scenario: plan the Tommy fixture (identity BR01.POP011.016.Tommy, Loopback 10.16.0.11/32, one backhaul, ACS and PPPoE networks).
    /// Expected: v6/v7 instance and area names, router-id 10.16.0.11, a stub site area with default cost 1, one cost-10 ptp backhaul on eth-01_BH01.Tommy-Shylock, MGMT 10.16.11.0/24 plus the 100.99.76.16/28 and 172.26.197.4/30 ranges, and a single passive 'all' matcher that folds ACS/PPPoE into 100.64.0.0/10 and includes 172.16.0.0/12.
    /// Invariant: the full plan (identity, topology and MGMT /24) is derived deterministically from the captured fixture.
    /// </summary>
    [Fact]
    public void Tommy_DerivesDeterministicIdentityTopologyAndMgmt24()
    {
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(TommySnapshot());
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        OspfMigrationPlan plan = result.Plan;

        Assert.Equal("BR01.POP011.016.Tommy", plan.Identity);
        Assert.Equal("Loopback", plan.LoopbackInterfaceName);
        Assert.Equal("10.16.0.11/32", plan.LoopbackCidr);
        Assert.Equal("10.16.0.11", plan.RouterId);
        Assert.Equal(OspfFleetNaming.Ros6InstanceName, plan.InstanceName);
        Assert.Equal(OspfFleetNaming.Ros7InstanceName, plan.TargetInstanceName);
        Assert.Equal(OspfFleetNaming.Ros7BackboneAreaName, plan.TargetBackboneAreaName);
        Assert.Equal("BR01.POP011.016.Tommy-v2", plan.TargetSiteAreaName);
        Assert.Equal("10.16.11.0/24", plan.DerivedManagementCidr);

        Assert.Equal("backbone", Assert.Single(plan.Areas, a => a.IsBackbone).Name);
        Assert.Equal("0.0.0.0", Assert.Single(plan.Areas, a => a.IsBackbone).AreaId);
        OspfPlanArea site = Assert.Single(plan.Areas, a => !a.IsBackbone);
        Assert.Equal("BR01.POP011.016.Tommy", site.Name);
        Assert.Equal("10.16.0.11", site.AreaId);
        Assert.Equal("stub", site.TargetType);
        Assert.Equal("1", site.DefaultCost);

        Assert.Single(plan.ActiveBackhauls);
        Assert.Equal("eth-01_BH01.Tommy-Shylock", plan.ActiveBackhauls[0].Interface);
        Assert.Equal("10.16.248.76/30", plan.ActiveBackhauls[0].NetworkCidr);
        Assert.Equal(10, plan.ActiveBackhauls[0].Cost);
        Assert.False(plan.ActiveBackhauls[0].Passive);

        Assert.Equal("10.16.0.11/32", Assert.Single(plan.PassiveLoopbacks).NetworkCidr);
        Assert.True(plan.PassiveLoopbacks[0].Passive);

        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "10.16.11.0/24" && r.Comment == "MGMT");
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "100.99.76.16/28");
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "172.26.197.4/30");

        OspfPlanSiteNetworkTemplate siteNet = Assert.Single(plan.PassiveSiteNetworks);
        Assert.Equal("all", siteNet.Interfaces);
        Assert.True(siteNet.Passive);
        Assert.Contains("10.16.11.0/24", siteNet.NetworkCidr, StringComparison.Ordinal);
        // ACS + active PPPoE fold into the shared 100.64.0.0/10 aggregate.
        Assert.Contains("100.64.0.0/10", siteNet.NetworkCidr, StringComparison.Ordinal);
        Assert.Contains("172.16.0.0/12", siteNet.NetworkCidr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: the Kopefontein fixture whose v6 site area uses an underscore (BR01.POP009.016_Kopefontein) while /system identity uses a dot.
    /// Expected: the source area name keeps the underscore, the target site area is BR01.POP009.016.Kopefontein-v2, MGMT is 10.16.9.0/24, two backhauls are planned and a note records that the area differs from the identity.
    /// Invariant: source names are preserved as captured but the v7 target is always derived from the identity.
    /// </summary>
    [Fact]
    public void Kopefontein_PreservesUnderscoreSiteArea_ButTargetsIdentityV2()
    {
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(KopefonteinSnapshot());
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("BR01.POP009.016.Kopefontein", result.Plan.Identity);
        Assert.Equal("BR01.POP009.016_Kopefontein", Assert.Single(result.Plan.Areas, a => !a.IsBackbone).Name);
        Assert.Equal("BR01.POP009.016.Kopefontein-v2", result.Plan.TargetSiteAreaName);
        Assert.Equal("10.16.9.0/24", result.Plan.DerivedManagementCidr);
        Assert.Equal(2, result.Plan.ActiveBackhauls.Count);
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("differs from /system identity", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: the ThirstyFalls fixture with one backhaul.
    /// Expected: router-id 10.16.0.10, MGMT 10.16.10.0/24, a single backhaul on eth-01_BH01.ThirstyFalls-Koperfontein and target area BR01.POP010.016.ThirstyFalls-v2.
    /// Invariant: single-backhaul sites derive identity-based names and the MGMT /24 correctly.
    /// </summary>
    [Fact]
    public void ThirstyFalls_SingleBackhaul_AndMgmt24()
    {
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(ThirstyFallsSnapshot());
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("BR01.POP010.016.ThirstyFalls", result.Plan.Identity);
        Assert.Equal("10.16.0.10", result.Plan.RouterId);
        Assert.Equal("10.16.10.0/24", result.Plan.DerivedManagementCidr);
        Assert.Equal("eth-01_BH01.ThirstyFalls-Koperfontein", Assert.Single(result.Plan.ActiveBackhauls).Interface);
        Assert.Equal("BR01.POP010.016.ThirstyFalls-v2", result.Plan.TargetSiteAreaName);
    }

    /// <summary>
    /// Scenario: the Hekpoort fixture with three backhauls and a Loopback-OSPF interface.
    /// Expected: loopback 10.16.0.14/32 on Loopback-OSPF, MGMT 10.16.14.0/24, three cost-10 backhauls (10.16.248.16/30, .68/30, .0/30), the MGMT and 100.99.72.0/26 ranges, and target area BR01.POP014.016.HekpoortTPA-v2.
    /// Invariant: the loopback interface name is taken from the router and every backhaul is normalised to the fleet cost.
    /// </summary>
    [Fact]
    public void Hekpoort_MultipleBackhauls_LoopbackOspf_AndMgmt24()
    {
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(HekpoortSnapshot());
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("BR01.POP014.016.HekpoortTPA", result.Plan.Identity);
        Assert.Equal("Loopback-OSPF", result.Plan.LoopbackInterfaceName);
        Assert.Equal("10.16.0.14/32", result.Plan.LoopbackCidr);
        Assert.Equal("10.16.0.14", result.Plan.RouterId);
        Assert.Equal("10.16.14.0/24", result.Plan.DerivedManagementCidr);
        Assert.Equal(3, result.Plan.ActiveBackhauls.Count);
        Assert.All(result.Plan.ActiveBackhauls, b => Assert.Equal(10, b.Cost));
        Assert.Contains(result.Plan.ActiveBackhauls, b => b.NetworkCidr == "10.16.248.16/30");
        Assert.Contains(result.Plan.ActiveBackhauls, b => b.NetworkCidr == "10.16.248.68/30");
        Assert.Contains(result.Plan.ActiveBackhauls, b => b.NetworkCidr == "10.16.248.0/30");
        Assert.Contains(result.Plan.AreaRanges, r => r.RangeCidr == "10.16.14.0/24" && r.Comment == "MGMT");
        Assert.Contains(result.Plan.AreaRanges, r => r.RangeCidr == "100.99.72.0/26");
        Assert.Equal("BR01.POP014.016.HekpoortTPA-v2", result.Plan.TargetSiteAreaName);
    }

    /// <summary>
    /// Scenario: a RouterOS 7 Loopback holding 172.16.150.253, 102.66.242.100 and 10.16.254.1.
    /// Expected: router-id 10.16.254.1 is chosen, two outside-range addresses are recorded as ignored, and the v2 naming helpers return default-v2, backbone-v2 and '&lt;identity&gt;-v2'.
    /// Invariant: only the in-scope 10/8 loopback address can become the router-id; extras are ignored rather than fatal.
    /// </summary>
    [Fact]
    public void RustenburgRos7_MultipleLoopbackAddresses_SelectsInScopeIdentity()
    {
        // ROS7 naming reference fixture — outside-range Loopbacks are ignored; sole 10/8 wins.
        OspfLoopbackRouterIdResult rid = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "172.16.150.253", Network = "172.16.150.253", Interface = "Loopback" },
            new OspfIpAddressRow { Address = "102.66.242.100", Network = "102.66.242.100", Interface = "Loopback" },
            new OspfIpAddressRow { Address = "10.16.254.1", Network = "10.16.254.1", Interface = "Loopback" },
        ]);
        Assert.True(rid.Succeeded, rid.Detail);
        Assert.Equal("10.16.254.1", rid.RouterId);
        Assert.Equal(2, rid.IgnoredOutsideRangeAddresses.Count);

        Assert.Equal("default-v2", OspfFleetNaming.Ros7InstanceName);
        Assert.Equal("backbone-v2", OspfFleetNaming.Ros7BackboneAreaName);
        Assert.Equal(
            "BR01.POP001.016.RustenburgKantoorServers-v2",
            OspfFleetNaming.Ros7SiteAreaName("BR01.POP001.016.RustenburgKantoorServers"));
    }

    /// <summary>
    /// Scenario: plan the Rustenburg fixture whose Loopback carries out-of-range addresses besides 10.16.254.1.
    /// Expected: the plan succeeds with router-id 10.16.254.1, loopback 10.16.254.1/32, instance default-v2, target area BR01.POP001.016.RustenburgKantoorServers-v2, two ignored addresses and an 'Ignored outside-range Loopback' note.
    /// Invariant: extra loopback addresses are noted but do not fail planning.
    /// </summary>
    [Fact]
    public void RustenburgRos7_PlannerSelectsInScopeLoopbackWithoutFailingOnExtras()
    {
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(RustenburgSnapshot());
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("BR01.POP001.016.RustenburgKantoorServers", result.Plan.Identity);
        Assert.Equal("10.16.254.1", result.Plan.RouterId);
        Assert.Equal("10.16.254.1/32", result.Plan.LoopbackCidr);
        Assert.Equal("default-v2", result.Plan.InstanceName);
        Assert.Equal("BR01.POP001.016.RustenburgKantoorServers-v2", result.Plan.TargetSiteAreaName);
        Assert.Equal(2, result.Plan.IgnoredOutsideRangeLoopbackAddresses.Count);
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("Ignored outside-range Loopback", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: a VLAN-99_MGMT host address of 10.16.11.30/27, 10.16.11.70/26 or 10.16.11.126/26, alongside the loopback and a backhaul /30.
    /// Expected: derivation succeeds and the management CIDR is 10.16.11.0/24.
    /// Invariant: any subdivision of the management /24 maps back to that same /24.
    /// </summary>
    [Theory]
    [InlineData("10.16.11.30/27", "10.16.11.0/24")]
    [InlineData("10.16.11.70/26", "10.16.11.0/24")]
    [InlineData("10.16.11.126/26", "10.16.11.0/24")]
    public void Management24_DerivesFromEligibleInterfaceAddress(string hostCidr, string expected24)
    {
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(
            [new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" }]);
        Assert.True(loopback.Succeeded, loopback.Detail);

        OspfSiteManagementSummaryResult mgmt = OspfSiteManagementSummary.Derive(
            [
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.248.78/30", Network = "10.16.248.76", Interface = "eth-bh" },
                new OspfIpAddressRow
                {
                    Address = hostCidr,
                    Network = expected24[..expected24.IndexOf('/')],
                    Interface = "VLAN-99_MGMT",
                },
            ],
            loopback,
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "eth-bh",
                    NetworkCidr = "10.16.248.76/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
            ]);

        Assert.True(mgmt.Succeeded, mgmt.Detail);
        Assert.Equal(expected24, mgmt.ManagementCidr);
    }

    /// <summary>
    /// Scenario: two interfaces (vlan99 in 10.16.11.0/27 and vlan12 in 10.16.12.0/24) both qualify and there is no other evidence.
    /// Expected: derivation fails with a 'Multiple distinct' error.
    /// Invariant: an ambiguous management /24 is rejected rather than guessed.
    /// </summary>
    [Fact]
    public void Management24_Fails_WhenMultipleDistinctEligible24s()
    {
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(
            [new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" }]);
        OspfSiteManagementSummaryResult mgmt = OspfSiteManagementSummary.Derive(
            [
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "vlan99" },
                new OspfIpAddressRow { Address = "10.16.12.1/24", Network = "10.16.12.0", Interface = "vlan12" },
            ],
            loopback,
            []);
        Assert.False(mgmt.Succeeded);
        Assert.Contains(mgmt.Errors, e => e.Contains("Multiple distinct", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: the same two candidate /24s as the ambiguous case, but 10.16.11.0/24 is supplied as corroborating MGMT evidence.
    /// Expected: derivation succeeds with 10.16.11.0/24.
    /// Invariant: explicit MGMT evidence resolves an otherwise ambiguous candidate set.
    /// </summary>
    [Fact]
    public void Management24_ResolvesMultipleCandidates_UsingMgmtCorroboration()
    {
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(
            [new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" }]);
        OspfSiteManagementSummaryResult mgmt = OspfSiteManagementSummary.Derive(
            [
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "vlan99" },
                new OspfIpAddressRow { Address = "10.16.12.1/24", Network = "10.16.12.0", Interface = "vlan12" },
            ],
            loopback,
            [],
            ["10.16.11.0/24"]);
        Assert.True(mgmt.Succeeded, mgmt.Detail);
        Assert.Equal("10.16.11.0/24", mgmt.ManagementCidr);
    }

    /// <summary>
    /// Scenario: a pppoe-out1 interface holds 10.99.1.1/24, inside 10/8, next to the VLAN-99_MGMT address.
    /// Expected: the management CIDR is 10.16.11.0/24 and the pppoe-out1 address is listed in IgnoredPppoeAddresses.
    /// Invariant: PPPoE interfaces never contribute to management derivation, whatever their address range.
    /// </summary>
    [Fact]
    public void Management24_IgnoresPppoeRoleInterface_EvenInsideTenSlash8()
    {
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(
            [new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" }]);
        OspfSiteManagementSummaryResult mgmt = OspfSiteManagementSummary.Derive(
            [
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "VLAN-99_MGMT" },
                new OspfIpAddressRow { Address = "10.99.1.1/24", Network = "10.99.1.0", Interface = "pppoe-out1" },
            ],
            loopback,
            []);
        Assert.True(mgmt.Succeeded, mgmt.Detail);
        Assert.Equal("10.16.11.0/24", mgmt.ManagementCidr);
        Assert.Contains(mgmt.IgnoredPppoeAddresses, a => a.Contains("pppoe-out1", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: an ether-mgmt-overlay interface holds 198.18.240.11/24, outside the fleet 10/8 range, and is not a PPPoE interface.
    /// Expected: the management CIDR is 10.16.11.0/24 and the overlay address is recorded in IgnoredOutsideRangeAddresses.
    /// Invariant: outside-range addresses are ignored and reported, never treated as an error or something to delete.
    /// </summary>
    [Fact]
    public void Management24_IgnoresOutsideRangeNonPppoeWithoutDeleting()
    {
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(
            [new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" }]);
        OspfSiteManagementSummaryResult mgmt = OspfSiteManagementSummary.Derive(
            [
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "vlan99" },
                new OspfIpAddressRow { Address = "198.18.240.11/24", Network = "198.18.240.0", Interface = "ether-mgmt-overlay" },
            ],
            loopback,
            []);
        Assert.True(mgmt.Succeeded, mgmt.Detail);
        Assert.Equal("10.16.11.0/24", mgmt.ManagementCidr);
        Assert.Contains(mgmt.IgnoredOutsideRangeAddresses, a => a.Contains("198.18.240.11", StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds the ROS6 capture for BR01.POP011.016.Tommy: one backhaul to Shylock, loopback 10.16.0.11/32, management 10.16.11.0/24 and ACS/PPPoE networks with HEROTEL pool ranges.
    /// </summary>
    private static OspfCaptureSnapshot TommySnapshot() => Ros6(
        identity: "BR01.POP011.016.Tommy",
        routerId: "10.16.0.11",
        loopbackIface: "Loopback",
        siteArea: "BR01.POP011.016.Tommy",
        siteDefaultCost: "1",
        areasExtra: [],
        networks:
        [
            Net("backbone", "10.16.248.76/30"),
            Net("backbone", "10.16.0.11/32"),
            Net("BR01.POP011.016.Tommy", "10.16.11.0/24"),
            Net("BR01.POP011.016.Tommy", "100.67.16.0/24", "ACS-Provisioning Subnet"),
            Net("BR01.POP011.016.Tommy", "100.99.0.0/16", "Active PPPoE Range"),
            Net("BR01.POP011.016.Tommy", "172.26.0.0/16", "Inactive PPPoE Range"),
        ],
        interfaces:
        [
            new OspfInterfaceRow { NetworkType = "point-to-point", Cost = "1", Passive = true },
            Iface("eth-01_BH01.Tommy-Shylock", "point-to-point"),
        ],
        addresses:
        [
            Addr("10.16.248.78/30", "10.16.248.76", "eth-01_BH01.Tommy-Shylock"),
            Addr("10.16.0.11", "10.16.0.11", "Loopback"),
            Addr("10.16.11.30/27", "10.16.11.0", "VLAN-99_MGMT_wlan"),
            Addr("10.16.11.62/27", "10.16.11.32", "Bridge-VLAN-197_MGMT"),
            Addr("10.16.11.126/26", "10.16.11.64", "Bridge-VLAN-99_MGMT"),
        ],
        ranges:
        [
            Range("BR01.POP011.016.Tommy", "10.16.11.0/24", "MGMT"),
            Range("BR01.POP011.016.Tommy", "100.99.76.16/28", "HEROTEL_ACTIVE_POOL"),
            Range("BR01.POP011.016.Tommy", "172.26.197.4/30", "HEROTEL_INACTIVE_POOL"),
        ]);

    /// <summary>
    /// Builds the ROS6 capture for BR01.POP009.016.Kopefontein: two point-to-point backhauls (Shylock and ThirstyFalls), loopback 10.16.0.9/32 and management 10.16.9.0/24.
    /// </summary>
    private static OspfCaptureSnapshot KopefonteinSnapshot() => Ros6(
        identity: "BR01.POP009.016.Kopefontein",
        routerId: "10.16.0.9",
        loopbackIface: "Loopback",
        siteArea: "BR01.POP009.016_Kopefontein",
        siteDefaultCost: "1",
        areasExtra: [],
        networks:
        [
            Net("backbone", "10.16.248.148/30"),
            Net("backbone", "10.16.248.72/30"),
            Net("backbone", "10.16.0.9/32"),
            Net("BR01.POP009.016_Kopefontein", "10.16.9.0/24", "MGMT"),
            Net("BR01.POP009.016_Kopefontein", "100.67.16.0/24"),
            Net("BR01.POP009.016_Kopefontein", "100.99.0.0/16", "Active PPPoE Range"),
            Net("BR01.POP009.016_Kopefontein", "172.26.0.0/16", "Inactive PPPoE Range"),
        ],
        interfaces:
        [
            Iface("eth-07_BR01.POP005.016.Shylock_eth-5", "point-to-point"),
            Iface("eth-08_BR01.POP010.016.ThirstyFalls_eth-01_[monitor:65]", "point-to-point"),
            new OspfInterfaceRow { NetworkType = "point-to-point", Cost = "1", Passive = true },
        ],
        addresses:
        [
            Addr("10.16.0.9", "10.16.0.9", "Loopback"),
            Addr("10.16.248.74/30", "10.16.248.72", "eth-07_BR01.POP005.016.Shylock_eth-5"),
            Addr("10.16.248.149/30", "10.16.248.148", "eth-08_BR01.POP010.016.ThirstyFalls_eth-01_[monitor:65]"),
            Addr("10.16.9.30/27", "10.16.9.0", "Bridge-VLAN-199_MGMT"),
        ],
        ranges:
        [
            Range("BR01.POP009.016_Kopefontein", "100.99.73.0/26", "HEROTEL_ACTIVE_POOL"),
            Range("BR01.POP009.016_Kopefontein", "172.26.195.64/28", "HEROTEL_INACTIVE_POOL"),
            Range("BR01.POP009.016_Kopefontein", "10.16.9.0/24", "MGMT"),
        ]);

    /// <summary>
    /// Builds the ROS6 capture for BR01.POP010.016.ThirstyFalls: a single backhaul to Kopefontein, loopback 10.16.0.10/32 and an already-aggregated 100.64.0.0/10 site network.
    /// </summary>
    private static OspfCaptureSnapshot ThirstyFallsSnapshot() => Ros6(
        identity: "BR01.POP010.016.ThirstyFalls",
        routerId: "10.16.0.10",
        loopbackIface: "Loopback",
        siteArea: "BR01.POP010.016.ThirstyFalls",
        siteDefaultCost: "1",
        areasExtra: [],
        networks:
        [
            Net("backbone", "10.16.248.148/30"),
            Net("backbone", "10.16.0.10/32"),
            Net("BR01.POP010.016.ThirstyFalls", "10.16.10.0/24"),
            Net("BR01.POP010.016.ThirstyFalls", "100.64.0.0/10"),
            Net("BR01.POP010.016.ThirstyFalls", "100.99.0.0/16", "Active PPPoE Range"),
            Net("BR01.POP010.016.ThirstyFalls", "172.26.0.0/16", "Inactive PPPoE Range"),
        ],
        interfaces:
        [
            Iface("eth-01_BH01.ThirstyFalls-Koperfontein", "point-to-point"),
            new OspfInterfaceRow { NetworkType = "point-to-point", Cost = "1", Passive = true },
        ],
        addresses:
        [
            Addr("10.16.0.10", "10.16.0.10", "Loopback"),
            Addr("10.16.248.150/30", "10.16.248.148", "eth-01_BH01.ThirstyFalls-Koperfontein"),
            Addr("10.16.10.30/27", "10.16.10.0", "Bridge-VLAN-199_MGMT"),
        ],
        ranges:
        [
            Range("BR01.POP010.016.ThirstyFalls", "10.16.10.0/24"),
            Range("BR01.POP010.016.ThirstyFalls", "100.99.76.0/28", "HEROTEL_ACTIVE_POOL"),
            Range("BR01.POP010.016.ThirstyFalls", "172.26.197.0/30", "HEROTEL_INACTIVE_POOL"),
        ]);

    /// <summary>
    /// Builds the ROS6 capture for BR01.POP014.016.HekpoortTPA: three backhauls including a temporary VLAN backhaul, loopback on <c>Loopback-OSPF</c>, extra legacy site networks, and a site area with no type or default cost configured.
    /// </summary>
    private static OspfCaptureSnapshot HekpoortSnapshot() => Ros6(
        identity: "BR01.POP014.016.HekpoortTPA",
        routerId: "10.16.0.14",
        loopbackIface: "Loopback-OSPF",
        siteArea: "BR01.POP014.016.HekpoortTPA",
        siteDefaultCost: null,
        siteType: null,
        areasExtra: [],
        networks:
        [
            Net("backbone", "10.16.248.16/30"),
            Net("backbone", "10.16.0.14/32"),
            Net("backbone", "10.16.248.68/30"),
            Net("backbone", "10.16.248.0/30"),
            Net("BR01.POP014.016.HekpoortTPA", "10.16.14.0/24"),
            Net("BR01.POP014.016.HekpoortTPA", "100.99.0.0/16"),
            Net("BR01.POP014.016.HekpoortTPA", "172.26.0.0/16"),
            Net("BR01.POP014.016.HekpoortTPA", "102.66.242.0/24"),
            Net("BR01.POP014.016.HekpoortTPA", "100.64.0.0/16"),
        ],
        interfaces:
        [
            Iface("eth-09_BH01.HekpoortTPA-HekpoortVertel Ligowave", "point-to-point"),
            new OspfInterfaceRow { NetworkType = "point-to-point", Cost = "1", Passive = true },
            Iface("eth-06", "point-to-point", cost: "11"),
            Iface("VLAN-1197_DATA_TEMP_eth-09", "point-to-point"),
        ],
        addresses:
        [
            Addr("10.16.0.14", "10.16.0.14", "Loopback-OSPF"),
            Addr("10.16.248.18/30", "10.16.248.16", "eth-09_BH01.HekpoortTPA-HekpoortVertel Ligowave"),
            Addr("10.16.248.70/30", "10.16.248.68", "eth-06"),
            Addr("10.16.248.2/30", "10.16.248.0", "VLAN-1197_DATA_TEMP_eth-09"),
            Addr("10.16.14.30/27", "10.16.14.0", "Bridge-VLAN-199_MGMT"),
            Addr("10.16.14.62/27", "10.16.14.32", "Bridge-VLAN-197_MGMT"),
        ],
        ranges:
        [
            Range("BR01.POP014.016.HekpoortTPA", "100.99.72.0/26"),
            Range("BR01.POP014.016.HekpoortTPA", "172.26.195.0/28"),
            Range("BR01.POP014.016.HekpoortTPA", "10.16.14.0/24", "MGMT"),
        ]);

    /// <summary>
    /// Builds a RouterOS 7.16.2 capture snapshot modelled on the Rustenburg Kantoor Servers router, with its
    /// instance, areas, area range, templates and loopback and management addresses.
    /// </summary>
    private static OspfCaptureSnapshot RustenburgSnapshot() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "fixture",
            RouterIp = IPAddress.Parse("10.16.254.1"),
            Identity = "BR01.POP001.016.RustenburgKantoorServers",
            RouterOsVersion = "7.16.2",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances =
            [
                new OspfInstanceRow
                {
                    Name = "default-v2",
                    RouterId = "10.16.254.1",
                    Version = "2",
                    IsDefault = true,
                },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Name = "backbone-v2",
                    AreaId = "0.0.0.0",
                    Type = "default",
                    Instance = "default-v2",
                },
                new OspfAreaRow
                {
                    Name = "BR01.POP001.016.RustenburgKantoorServers-v2",
                    AreaId = "10.16.254.1",
                    Type = "stub",
                    DefaultCost = "1",
                    Instance = "default-v2",
                },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow
                {
                    Area = "BR01.POP001.016.RustenburgKantoorServers-v2",
                    Range = "10.16.254.0/24",
                    Comment = "MGMT",
                },
            ],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Interfaces = "eth-bh",
                    Area = "backbone-v2",
                    Networks = "10.16.248.4/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Interfaces = "all",
                    Area = "backbone-v2",
                    Networks = "10.16.254.1/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Interfaces = "all",
                    Area = "BR01.POP001.016.RustenburgKantoorServers-v2",
                    Networks = "10.16.254.0/24",
                    Passive = true,
                },
            ],
            IpAddresses =
            [
                Addr("172.16.150.253", "172.16.150.253", "Loopback"),
                Addr("102.66.242.100", "102.66.242.100", "Loopback"),
                Addr("10.16.254.1", "10.16.254.1", "Loopback"),
                Addr("10.16.248.6/30", "10.16.248.4", "eth-bh"),
                Addr("10.16.254.30/27", "10.16.254.0", "Bridge-VLAN-199_MGMT"),
            ],
        };

    /// <summary>
    /// Builds a RouterOS 6.49.18 (mipsbe) capture snapshot from the supplied fixture fields, with a <c>backbone</c> area and a site area named after <paramref name="siteArea"/>.
    /// <param name="identity">System identity of the router.</param>
    /// <param name="routerId">Router id, also used as the router IP and site area id.</param>
    /// <param name="loopbackIface">Name of the loopback interface.</param>
    /// <param name="siteArea">Name of the site area.</param>
    /// <param name="siteDefaultCost">Site area default cost as captured; <see langword="null"/> when unset.</param>
    /// <param name="networks">Captured OSPF network rows.</param>
    /// <param name="interfaces">Captured OSPF interface rows.</param>
    /// <param name="addresses">Captured IP address rows.</param>
    /// <param name="ranges">Captured area range rows.</param>
    /// <param name="areasExtra">Optional additional areas.</param>
    /// <param name="siteType">Site area type as captured; <see langword="null"/> when unset.</param>
    /// </summary>
    private static OspfCaptureSnapshot Ros6(
        string identity,
        string routerId,
        string loopbackIface,
        string siteArea,
        string? siteDefaultCost,
        IReadOnlyList<OspfNetworkRow> networks,
        IReadOnlyList<OspfInterfaceRow> interfaces,
        IReadOnlyList<OspfIpAddressRow> addresses,
        IReadOnlyList<OspfAreaRangeRow> ranges,
        IReadOnlyList<OspfAreaRow>? areasExtra = null,
        string? siteType = "stub") =>
        new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "fixture",
            RouterIp = IPAddress.Parse(routerId),
            Identity = identity,
            RouterOsVersion = "6.49.18",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            Instances = [new OspfInstanceRow { Name = "default", RouterId = routerId, IsDefault = true }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Type = "default" },
                new OspfAreaRow
                {
                    Name = siteArea,
                    AreaId = routerId,
                    Type = siteType,
                    DefaultCost = siteDefaultCost,
                    InjectSummaryLsas = "no",
                },
                ..(areasExtra ?? []),
            ],
            AreaRanges = ranges,
            Networks = networks,
            Interfaces = interfaces,
            IpAddresses = addresses,
        };

    /// <summary>
    /// Creates an OSPF network row.
    /// <param name="area">Area the network belongs to.</param>
    /// <param name="network">Network in CIDR notation.</param>
    /// <param name="comment">Optional comment.</param>
    /// </summary>
    private static OspfNetworkRow Net(string area, string network, string? comment = null) =>
        new() { Area = area, Network = network, Comment = comment };

    /// <summary>
    /// Creates a non-passive OSPF interface row.
    /// <param name="name">Interface name.</param>
    /// <param name="type">OSPF network type, for example <c>point-to-point</c>.</param>
    /// <param name="cost">Interface cost.</param>
    /// </summary>
    private static OspfInterfaceRow Iface(string name, string type, string cost = "10") =>
        new() { Interface = name, NetworkType = type, Cost = cost, Passive = false };

    /// <summary>
    /// Creates an IP address row.
    /// <param name="address">Address, with or without prefix length.</param>
    /// <param name="network">Network the address belongs to.</param>
    /// <param name="iface">Interface the address is assigned to.</param>
    /// </summary>
    private static OspfIpAddressRow Addr(string address, string network, string iface) =>
        new() { Address = address, Network = network, Interface = iface };

    /// <summary>
    /// Creates an OSPF area range row.
    /// <param name="area">Area the range belongs to.</param>
    /// <param name="range">Range in CIDR notation.</param>
    /// <param name="comment">Optional comment.</param>
    /// </summary>
    private static OspfAreaRangeRow Range(string area, string range, string? comment = null) =>
        new() { Area = area, Range = range, Comment = comment };
}
