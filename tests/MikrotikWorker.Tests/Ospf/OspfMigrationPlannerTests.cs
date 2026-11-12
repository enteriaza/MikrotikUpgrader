using System.Net;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Ospf;

public sealed class OspfMigrationPlannerTests
{
    [Fact]
    public void V6Snapshot_ClassifiesBackhaulLoopbackAndPreservesSiteMatchers()
    {
        OspfCaptureSnapshot snapshot = CreateV6Baseline();
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);

        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal(OspfCaptureSourceKind.RouterOsV6, result.Plan.SourceKind);
        Assert.Equal("10.255.0.1", result.Plan.RouterId);
        Assert.Single(result.Plan.ActiveBackhauls);
        Assert.Equal("ether1", result.Plan.ActiveBackhauls[0].Interface);
        Assert.Equal("10.0.0.0/30", result.Plan.ActiveBackhauls[0].NetworkCidr);
        Assert.Equal(10, result.Plan.ActiveBackhauls[0].Cost);
        Assert.Equal("ptp", result.Plan.ActiveBackhauls[0].Type);
        Assert.False(result.Plan.ActiveBackhauls[0].Passive);

        Assert.Single(result.Plan.PassiveLoopbacks);
        Assert.Equal("10.255.0.1/32", result.Plan.PassiveLoopbacks[0].NetworkCidr);
        Assert.True(result.Plan.PassiveLoopbacks[0].Passive);
        // Loopback must stay passive without forced PTP.
        Assert.DoesNotContain(result.Plan.ActiveBackhauls, b => b.NetworkCidr.EndsWith("/32", StringComparison.Ordinal));

        OspfPlanSiteNetworkTemplate site = Assert.Single(result.Plan.PassiveSiteNetworks);
        Assert.Equal("10.10.0.0/16", site.NetworkCidr);
        Assert.Equal("all", site.Interfaces);
        Assert.True(site.Passive);
        Assert.All(result.Plan.Areas.Where(a => !a.IsBackbone), a =>
        {
            Assert.Equal("stub", a.TargetType);
            Assert.False(a.NoSummaries);
        });
        Assert.Contains(result.Plan.AreaRanges, r => r.RangeCidr == "10.10.0.0/16");
    }

    [Fact]
    public void V6Snapshot_ConsolidatesMultipleSiteNetworksIntoOneInterfacesAllTemplate()
    {
        OspfCaptureSnapshot snapshot = CreateV6Baseline(
            networks:
            [
                Net("backbone", "10.255.0.1/32"),
                Net("backbone", "10.0.0.0/30"),
                Net("site", "10.10.0.0/16"),
                Net("site", "100.99.0.0/16"),
                Net("site", "100.67.16.0/24"),
                Net("site", "172.26.0.0/16"),
            ]);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        OspfPlanSiteNetworkTemplate site = Assert.Single(result.Plan.PassiveSiteNetworks);
        Assert.Equal("all", site.Interfaces);
        Assert.True(site.Passive);
        Assert.Equal("10.10.0.0/16,100.64.0.0/10,172.16.0.0/12", site.NetworkCidr);
        Assert.False(result.Plan.ActiveBackhauls[0].Passive);
    }

    [Fact]
    public void V7Snapshot_ConsolidatesLegacyIfaceBoundMgmtTemplates_ViaAreaRange()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "7.23.8",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.255.0.1", Version = "2" }],
            Areas = [Backbone(), SiteArea(defaultCost: "1")],
            AreaRanges = [new OspfAreaRangeRow { Area = "site", Range = "10.10.0.0/16" }],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "ether1",
                    Networks = "10.0.0.0/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "lo",
                    Networks = "10.255.0.1/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "site",
                    Interfaces = "Bridge-VLAN-99",
                    Networks = "10.10.0.0/27",
                    Type = "ptp",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "site",
                    Interfaces = "Bridge-VLAN-197",
                    Networks = "10.10.0.32/27",
                    Type = "ptp",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "site",
                    Networks = "100.99.0.0/16",
                    Passive = true,
                },
            ],
            IpAddresses =
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/30", "10.0.0.0", "ether1"),
                Addr("10.10.0.1/27", "10.10.0.0", "Bridge-VLAN-99"),
            ],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Single(result.Plan.ActiveBackhauls);
        Assert.Single(result.Plan.PassiveLoopbacks);
        OspfPlanSiteNetworkTemplate site = Assert.Single(result.Plan.PassiveSiteNetworks);
        Assert.Equal("all", site.Interfaces);
        Assert.Equal("10.10.0.0/16,100.64.0.0/10", site.NetworkCidr);
    }

    [Fact]
    public void DefaultCost_PreservedWhenParsable_UnsetWhenAbsent_FailsWhenUnsafe()
    {
        OspfCaptureSnapshot ok = CreateV6Baseline();
        ok = WithAreas(ok,
        [
            Backbone(),
            new OspfAreaRow
            {
                Name = "site",
                AreaId = "10.255.0.1",
                Type = "stub",
                DefaultCost = "20",
                InjectSummaryLsas = "yes",
            },
        ]);

        OspfPlanValidationResult preserved = OspfMigrationPlanner.Build(ok);
        Assert.True(preserved.Succeeded, string.Join("; ", preserved.Plan.ValidationErrors));
        OspfPlanArea site = Assert.Single(preserved.Plan.Areas, a => a.Name == "site");
        Assert.Equal(OspfFleetNaming.SiteDefaultCost, site.DefaultCost);
        Assert.Equal("20", site.SourceDefaultCost);
        Assert.Contains(
            preserved.Plan.NormalisationNotes,
            n => n.Contains("normalised to", StringComparison.OrdinalIgnoreCase)
                 && n.Contains(OspfFleetNaming.SiteDefaultCost, StringComparison.Ordinal));
        // Never infer default-route origination from inject-summary-lsas.
        Assert.DoesNotContain(
            preserved.Plan.NormalisationNotes,
            n => n.Contains("inject-summary", StringComparison.OrdinalIgnoreCase));

        OspfCaptureSnapshot unset = WithAreas(CreateV6Baseline(), [Backbone(), SiteArea(defaultCost: null)]);
        OspfPlanValidationResult unsetResult = OspfMigrationPlanner.Build(unset);
        Assert.True(unsetResult.Succeeded);
        Assert.Equal(
            OspfFleetNaming.SiteDefaultCost,
            Assert.Single(unsetResult.Plan.Areas, a => a.Name == "site").DefaultCost);

        OspfCaptureSnapshot bad = WithAreas(CreateV6Baseline(), [Backbone(), SiteArea(defaultCost: "not-a-number")]);
        OspfPlanValidationResult badResult = OspfMigrationPlanner.Build(bad);
        Assert.False(badResult.Succeeded);
        Assert.Contains(badResult.Plan.ValidationErrors, e => e.Contains("default-cost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MultipleBackhauls_ExactSubnets_Succeed()
    {
        OspfCaptureSnapshot snapshot = CreateV6Baseline(
            addresses:
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/30", "10.0.0.0", "ether1"),
                Addr("10.0.1.1/30", "10.0.1.0", "ether2"),
                Addr("10.10.1.1/24", "10.10.1.0", "bridge-local"),
            ],
            interfaces:
            [
                Iface("ether1", "ptp", "100"),
                Iface("ether2", "point-to-point", "50"),
            ],
            networks:
            [
                Net("backbone", "10.255.0.1/32"),
                Net("backbone", "10.0.0.0/30"),
                Net("backbone", "10.0.1.0/30"),
                Net("site", "10.10.0.0/16"),
            ]);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal(2, result.Plan.ActiveBackhauls.Count);
        Assert.All(result.Plan.ActiveBackhauls, b => Assert.Equal(10, b.Cost));
    }

    [Fact]
    public void BroadBackhaulNetwork_CoveringUnrelatedInterface_FailsClosed()
    {
        // Planned backhaul uses the interface subnet; if that subnet also covers another local
        // address, activation is unsafe under OR-style template matching.
        OspfCaptureSnapshot snapshot = CreateV6Baseline(
            addresses:
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/24", "10.0.0.0", "ether1"),
                Addr("10.0.0.50/24", "10.0.0.0", "bridge-local"),
            ],
            interfaces: [Iface("ether1", "ptp", "10")],
            networks:
            [
                Net("backbone", "10.255.0.1/32"),
                Net("backbone", "10.0.0.0/24"),
                Net("site", "10.10.0.0/16"),
            ]);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Plan.ValidationErrors,
            e => e.Contains("unrelated interface", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void V7Snapshot_RequiresClassifiableActiveBackhaul_NeverInvents()
    {
        OspfCaptureSnapshot emptyTemplates = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "7.23.8",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.255.0.1" }],
            Areas = [Backbone(), SiteArea()],
            InterfaceTemplates = [],
            IpAddresses = [Addr("10.255.0.1/32", "10.255.0.1", "lo")],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(emptyTemplates);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Plan.ValidationErrors, e => e.Contains("No valid active backbone PTP", StringComparison.Ordinal)
            || e.Contains("interface-templates", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.Plan.ActiveBackhauls);
    }

    [Fact]
    public void V7Snapshot_NormalisesActiveLoopbackAndSiteMatchersToPassive()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "7.23.8",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.255.0.1", Version = "2" }],
            Areas = [Backbone(), SiteArea()],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "ether1",
                    Networks = "10.0.0.0/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "lo",
                    Networks = "10.255.0.1",
                    Type = "ptp",
                    Cost = "1",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "site",
                    Interfaces = "bridge-local",
                    Networks = "10.10.0.0/16",
                    Type = "ptp",
                    Cost = "1",
                    Passive = false,
                },
            ],
            IpAddresses =
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/30", "10.0.0.0", "ether1"),
                Addr("10.10.1.1/24", "10.10.1.0", "bridge-local"),
            ],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.True(Assert.Single(result.Plan.PassiveLoopbacks).Passive);
        Assert.Equal("10.255.0.1/32", result.Plan.PassiveLoopbacks[0].NetworkCidr);
        OspfPlanSiteNetworkTemplate site = Assert.Single(result.Plan.PassiveSiteNetworks);
        Assert.Equal("10.10.0.0/16", site.NetworkCidr);
        Assert.Equal("all", site.Interfaces);
        Assert.True(site.Passive);
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("active to passive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void V7Snapshot_ClassifiesTemplates_AndQueuesOspfV3Removal()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "7.23.8",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.255.0.1", Version = "2" }],
            Areas = [Backbone(), SiteArea(defaultCost: "5")],
            AreaRanges = [new OspfAreaRangeRow { Area = "site", Range = "10.10.0.0/16" }],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "ether1",
                    Networks = "10.0.0.0/30",
                    Type = "ptp",
                    Cost = "100",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Networks = "10.255.0.1/32",
                    Interfaces = "lo",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "site",
                    Networks = "10.10.0.0/16",
                    Passive = true,
                },
            ],
            IpAddresses =
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/30", "10.0.0.0", "ether1"),
                Addr("10.10.1.1/24", "10.10.1.0", "bridge-local"),
            ],
            OspfV3Objects =
            [
                new OspfV3ObjectRef { Path = "/routing/ospf/instance", Id = "*3", Name = "default-v3", Kind = "instance-v3" },
            ],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal(OspfCaptureSourceKind.RouterOsV7, result.Plan.SourceKind);
        Assert.Equal(10, result.Plan.ActiveBackhauls[0].Cost);
        Assert.Equal(OspfFleetNaming.SiteDefaultCost, Assert.Single(result.Plan.Areas, a => a.Name == "site").DefaultCost);
        Assert.Single(result.Plan.OspfV3ToRemove);
        Assert.Contains(result.Plan.NormalisationNotes, n => n.Contains("OSPFv3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyOspf_IsSeriousConfigurationError()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "6.49.23",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Plan.ValidationErrors, e => e.Contains("instance is missing", StringComparison.OrdinalIgnoreCase));
    }

    private static OspfCaptureSnapshot CreateV6Baseline(
        IReadOnlyList<OspfIpAddressRow>? addresses = null,
        IReadOnlyList<OspfInterfaceRow>? interfaces = null,
        IReadOnlyList<OspfNetworkRow>? networks = null) =>
        new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "import:1",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01.POP001.001.access",
            RouterOsVersion = "6.49.23",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            Instances = [new OspfInstanceRow { Name = "default", RouterId = "10.255.0.1", IsDefault = true }],
            Areas = [Backbone(), SiteArea()],
            AreaRanges = [new OspfAreaRangeRow { Area = "site", Range = "10.10.0.0/16" }],
            Networks = networks ??
            [
                Net("backbone", "10.255.0.1/32"),
                Net("backbone", "10.0.0.0/30"),
                Net("site", "10.10.0.0/16"),
            ],
            Interfaces = interfaces ?? [Iface("ether1", "ptp", "100")],
            IpAddresses = addresses ??
            [
                Addr("10.255.0.1/32", "10.255.0.1", "lo"),
                Addr("10.0.0.1/30", "10.0.0.0", "ether1"),
                Addr("10.10.1.1/24", "10.10.1.0", "bridge-local"),
            ],
            RoutingFilters = [new OspfRoutingFilterRow { Id = "*1", Name = "ospf-out" }],
            OspfV3Objects = [],
        };

    private static OspfCaptureSnapshot WithAreas(OspfCaptureSnapshot source, IReadOnlyList<OspfAreaRow> areas) =>
        new()
        {
            ImportId = source.ImportId,
            JobId = source.JobId,
            RouterIp = source.RouterIp,
            Identity = source.Identity,
            RouterOsVersion = source.RouterOsVersion,
            Architecture = source.Architecture,
            SourceKind = source.SourceKind,
            Instances = source.Instances,
            Areas = areas,
            AreaRanges = source.AreaRanges,
            Networks = source.Networks,
            Interfaces = source.Interfaces,
            InterfaceTemplates = source.InterfaceTemplates,
            IpAddresses = source.IpAddresses,
            RoutingFilters = source.RoutingFilters,
            OspfV3Objects = source.OspfV3Objects,
        };

    private static OspfAreaRow Backbone() => new()
    {
        Name = "backbone",
        AreaId = "0.0.0.0",
        Type = "default",
    };

    private static OspfAreaRow SiteArea(string? defaultCost = null) => new()
    {
        Name = "site",
        AreaId = "10.255.0.1",
        Type = "stub",
        DefaultCost = defaultCost,
    };

    private static OspfNetworkRow Net(string area, string network) => new() { Area = area, Network = network };

    private static OspfInterfaceRow Iface(string name, string type, string cost) => new()
    {
        Interface = name,
        NetworkType = type,
        Cost = cost,
        Passive = false,
    };

    private static OspfIpAddressRow Addr(string address, string network, string iface) => new()
    {
        Address = address,
        Network = network,
        Interface = iface,
    };
}
