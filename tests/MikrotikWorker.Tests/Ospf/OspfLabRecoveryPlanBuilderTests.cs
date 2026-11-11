using System.Net;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Ospf;

public sealed class OspfLabRecoveryPlanBuilderTests
{
    [Fact]
    public void ParseReferenceExport_ReadsNetworksAndRangesWithContinuations()
    {
        // Use explicit concatenation so the MikroTik trailing '\' continuation is unambiguous in source.
        string export =
            "/routing ospf area range\n"
            + "add area=BR01.POP011.016.Tommy comment=MGMT range=10.16.11.0/24\n"
            + "add area=BR01.POP011.016.Tommy comment=HEROTEL_ACTIVE_POOL range=\\\n"
            + "    100.99.76.16/28\n"
            + "/routing ospf network\n"
            + "add area=backbone network=10.16.248.76/30\n"
            + "add area=backbone network=10.16.0.11/32\n"
            + "add area=BR01.POP011.016.Tommy network=10.16.11.0/24\n"
            + "add area=BR01.POP011.016.Tommy comment=\"Active PPPoE Range\" network=\\\n"
            + "    100.99.0.0/16\n"
            + "/snmp\n"
            + "set enabled=true\n";

        OspfLabRecoveryPlanBuilder.ReferenceTopology topo =
            OspfLabRecoveryPlanBuilder.ParseReferenceExport(export);

        Assert.Equal(2, topo.Ranges.Count);
        Assert.Equal(4, topo.Networks.Count);
        Assert.Contains(topo.Networks, n => n.Network == "100.99.0.0/16" && n.Comment == "Active PPPoE Range");
        Assert.Contains(topo.Ranges, r => r.Range == "100.99.76.16/28");
    }

    [Fact]
    public void BuildFromLiveAndReference_MapsV2AreaNamesAndRestoresMatchers()
    {
        string temp = Path.Combine(Path.GetTempPath(), "ospf-ref-" + Guid.NewGuid().ToString("N") + ".rsc");
        File.WriteAllText(
            temp,
            """
            /routing ospf area range
            add area=site comment=MGMT range=10.10.0.0/16
            /routing ospf network
            add area=backbone network=10.0.0.0/30
            add area=backbone network=10.255.0.1/32
            add area=site comment="Active PPPoE Range" network=100.99.0.0/16
            add area=site network=10.10.0.0/16
            """);

        try
        {
            OspfV7Inventory live = new()
            {
                Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.255.0.1", Version = "2" }],
                Areas =
                [
                    new OspfAreaRow { Name = "backbone-v2", AreaId = "0.0.0.0", Instance = "default-v2", Type = "default" },
                    new OspfAreaRow
                    {
                        Name = "site-v2",
                        AreaId = "10.0.0.1",
                        Instance = "default-v2",
                        Type = "stub",
                        DefaultCost = "1",
                    },
                ],
                AreaRanges = [],
                Templates = [],
                Filters = [],
                OspfV3Objects = [],
            };

            OspfPlanValidationResult result = OspfLabRecoveryPlanBuilder.BuildFromLiveAndReference(
                Guid.NewGuid(),
                "job",
                IPAddress.Parse("10.0.0.1"),
                "AP01",
                "7.23.8",
                "mipsbe",
                live,
                [
                    new OspfIpAddressRow { Address = "10.255.0.1/32", Network = "10.255.0.1", Interface = "lo" },
                    new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
                ],
                temp);

            Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
            Assert.Equal("backbone-v2", result.Plan.ActiveBackhauls[0].AreaName);
            Assert.Equal("site-v2", Assert.Single(result.Plan.AreaRanges).AreaName);
            OspfPlanSiteNetworkTemplate site = Assert.Single(result.Plan.PassiveSiteNetworks);
            Assert.Equal("all", site.Interfaces);
            Assert.Equal("10.10.0.0/16,100.64.0.0/10", site.NetworkCidr);
            Assert.False(Assert.Single(result.Plan.Areas, a => a.Name == "site-v2").NoSummaries);
            Assert.Equal("default-v2", result.Plan.InstanceName);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
