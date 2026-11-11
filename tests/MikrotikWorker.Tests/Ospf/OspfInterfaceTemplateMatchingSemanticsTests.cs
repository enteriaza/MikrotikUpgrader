using System.Net;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Documents RouterOS v7 interface-template matching evidence and the planner's fail-closed posture.
/// Official docs describe <c>interfaces=</c> and <c>networks=</c> as alternative matchers
/// ("either by specifying network or interface"). Combined AND semantics are not assumed.
/// Plans therefore emit exact interface subnets for active backhauls and refuse templates whose
/// network matcher would cover an address on any other local interface (OR-safe isolation).
/// </summary>
public sealed class OspfInterfaceTemplateMatchingSemanticsTests
{
    [Fact]
    public void ActiveBackhaul_UsesExactInterfaceSubnet_NotBroaderTemplateNetwork()
    {
        OspfCaptureSnapshot snapshot = V7(
            templates:
            [
                Active("ether1", "10.0.0.0/24"), // broader than the /30 on ether1
                PassiveLoop("10.255.0.1/32"),
                PassiveSite("10.10.0.0/16"),
            ],
            addresses:
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/30", "10.0.0.0", "ether1"),
                Addr("10.10.1.1/24", "10.10.1.0", "bridge-local"),
            ]);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("10.0.0.0/30", result.Plan.ActiveBackhauls[0].NetworkCidr);
        Assert.Contains(result.Plan.NormalisationNotes, n => n.Contains("Narrowing backhaul", StringComparison.Ordinal));
    }

    [Fact]
    public void OverlappingAddressOnOtherInterface_FailsEvenIfInterfacesNamed()
    {
        // Under OR matching, networks= covering the planned backhaul subnet would also activate vlan10.
        // ether1 itself is /24 so the plan cannot narrow away from the overlap.
        OspfCaptureSnapshot snapshot = V7(
            templates:
            [
                Active("ether1", "10.0.0.0/24"),
                PassiveLoop("10.255.0.1/32"),
                PassiveSite("10.10.0.0/16"),
            ],
            addresses:
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/24", "10.0.0.0", "ether1"),
                Addr("10.0.0.50/24", "10.0.0.0", "vlan10"),
            ]);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Plan.ValidationErrors,
            e => e.Contains("unrelated interface", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MultipleDistinctBackhauls_DoNotCrossActivate()
    {
        OspfCaptureSnapshot snapshot = V7(
            templates:
            [
                Active("ether1", "10.0.0.0/30"),
                Active("ether2", "10.0.1.0/30"),
                PassiveLoop("10.255.0.1/32"),
                PassiveSite("10.10.0.0/16"),
            ],
            addresses:
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/30", "10.0.0.0", "ether1"),
                Addr("10.0.1.1/30", "10.0.1.0", "ether2"),
                Addr("10.10.1.1/24", "10.10.1.0", "bridge-local"),
            ]);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal(2, result.Plan.ActiveBackhauls.Count);
        Assert.DoesNotContain(result.Plan.ActiveBackhauls, b => b.Interface == "bridge-local");
        Assert.All(result.Plan.ActiveBackhauls, b =>
        {
            Assert.False(b.Passive);
            Assert.Equal("ptp", b.Type);
            Assert.Equal(10, b.Cost);
        });
    }

    [Fact]
    public void BroadPassiveSiteMatcher_CoveringBackhaul_FailsClosed()
    {
        // interfaces=all + networks covering the backhaul is unsafe under OR-style matching.
        OspfCaptureSnapshot snapshot = V7(
            templates:
            [
                Active("ether1", "10.0.0.0/30"),
                PassiveLoop("10.255.0.1/32"),
                PassiveSite("0.0.0.0/0"),
            ],
            addresses:
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/30", "10.0.0.0", "ether1"),
            ]);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Plan.ValidationErrors,
            e => e.Contains("covers active backhaul", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BroadPassiveSiteMatcher_OutsideBackhaul_IsRetainedAsInterfacesAll()
    {
        OspfCaptureSnapshot snapshot = V7(
            templates:
            [
                Active("ether1", "10.0.0.0/30"),
                PassiveLoop("10.255.0.1/32"),
                PassiveSite("10.10.0.0/16"),
            ],
            addresses:
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/30", "10.0.0.0", "ether1"),
            ]);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        OspfPlanSiteNetworkTemplate site = Assert.Single(result.Plan.PassiveSiteNetworks);
        Assert.Equal("10.10.0.0/16", site.NetworkCidr);
        Assert.Equal("all", site.Interfaces);
        Assert.True(site.Passive);
        Assert.Single(result.Plan.ActiveBackhauls);
        Assert.Equal("ether1", result.Plan.ActiveBackhauls[0].Interface);
    }

    private static OspfCaptureSnapshot V7(
        IReadOnlyList<OspfInterfaceTemplateRow> templates,
        IReadOnlyList<OspfIpAddressRow> addresses) =>
        new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "7.23.8",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.255.0.1", Version = "2" }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Type = "default" },
                new OspfAreaRow { Name = "site", AreaId = "10.0.0.1", Type = "stub" },
            ],
            InterfaceTemplates = templates,
            IpAddresses = addresses,
        };

    private static OspfInterfaceTemplateRow Active(string iface, string network) => new()
    {
        Area = "backbone",
        Interfaces = iface,
        Networks = network,
        Type = "ptp",
        Cost = "10",
        Passive = false,
    };

    private static OspfInterfaceTemplateRow PassiveLoop(string network) => new()
    {
        Area = "backbone",
        Networks = network,
        Passive = true,
    };

    private static OspfInterfaceTemplateRow PassiveSite(string network) => new()
    {
        Area = "site",
        Networks = network,
        Passive = true,
    };

    private static OspfIpAddressRow Addr(string address, string network, string iface) => new()
    {
        Address = address,
        Network = network,
        Interface = iface,
    };
}
