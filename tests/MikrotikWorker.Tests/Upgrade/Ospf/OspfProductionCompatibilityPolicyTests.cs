// -----------------------------------------------------------------------------
// File: OspfProductionCompatibilityPolicyTests.cs
//
// Description:
// Regression fixtures for the controlled production OSPF migration failures
// (POP004/006/007/008/012/013/014) covering legacy RID, instance naming,
// management /24 derivation, selective cleanup, and cost normalisation.
//
// -----------------------------------------------------------------------------

using System.Net;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Focused production-compatibility policy regressions derived from the controlled upgrade exercise.
/// </summary>
public sealed class OspfProductionCompatibilityPolicyTests
{
    [Fact]
    public void Planner_AllowsLegacyHostnameRouterId_AndTargetsLoopbackLiteral()
    {
        OspfCaptureSnapshot snapshot = BaselineV7(
            identity: "BR01.POP004.016.KobusVisser",
            loopback: "10.16.0.4",
            instanceName: "default-v2",
            instanceRouterId: "BR01.POP004.016.KobusV",
            mgmtAddresses:
            [
                Addr("10.16.4.30/27", "10.16.4.0", "Bridge-VLAN-199_MGMT"),
            ],
            backhaulInterface: "eth-01",
            backhaulCidr: "10.16.248.4/30",
            backhaulCost: "10");

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);

        Assert.True(result.Plan.IsValid, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("10.16.0.4", result.Plan.RouterId);
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("differs from Loopback", StringComparison.OrdinalIgnoreCase)
                 && n.Contains("BR01.POP004.016.KobusV", StringComparison.Ordinal));
    }

    [Fact]
    public void Planner_AllowsLegacyMainRouterId()
    {
        OspfCaptureSnapshot snapshot = BaselineV7(
            identity: "BR01.POP007.016.AmbleRidge",
            loopback: "10.16.0.7",
            instanceName: "default",
            instanceRouterId: "main",
            mgmtAddresses:
            [
                Addr("10.16.7.30/27", "10.16.7.0", "Bridge-VLAN-199_MGMT"),
            ],
            backhaulInterface: "bridge-bh",
            backhaulCidr: "10.16.248.8/30",
            backhaulCost: "10",
            neighbourInterface: "bridge-bh");

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);

        Assert.True(result.Plan.IsValid, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("10.16.0.7", result.Plan.RouterId);
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("'main'", StringComparison.Ordinal)
                 && n.Contains("normalise", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LiveResolve_BindsLegacyNamedSoleInstance_WithoutRequiringPreMatchRid()
    {
        OspfMigrationPlan plan = MinimalPlan(
            identity: "BR01.POP006.016.HekpoortVertel",
            routerId: "10.16.0.6",
            instanceName: "default-v2");

        OspfV7Inventory inventory = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default", RouterId = "main", Version = "2" },
            ],
            Areas =
            [
                new OspfAreaRow { Id = "*b", Name = "backbone", AreaId = "0.0.0.0", Instance = "default" },
                new OspfAreaRow
                {
                    Id = "*s",
                    Name = "BR01.POP006.016.HekpoortVertel",
                    AreaId = "10.16.0.6",
                    Instance = "default",
                },
            ],
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            plan,
            inventory,
            OspfTestBaselines.LoopbackSlash32("10.16.0.6", "Loopback"));

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("default", result.Binding!.LiveInstanceName);
        Assert.Equal("10.16.0.6", result.Binding.DerivedLoopbackRouterId);
        Assert.Equal("backbone", result.Binding.Areas.Values.Single(a => a.AreaId == "0.0.0.0").LiveName);
    }

    [Fact]
    public void LiveResolve_Fails_WhenMultipleAmbiguousV2Instances()
    {
        OspfMigrationPlan plan = MinimalPlan(
            identity: "BR01.POP013.016.Wigwam",
            routerId: "10.16.0.13",
            instanceName: "default-v2");

        OspfV7Inventory inventory = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "legacy-a", RouterId = "1.1.1.1", Version = "2" },
                new OspfInstanceRow { Id = "*2", Name = "legacy-b", RouterId = "2.2.2.2", Version = "2" },
            ],
            Areas =
            [
                new OspfAreaRow { Id = "*b1", Name = "backbone-a", AreaId = "0.0.0.0", Instance = "legacy-a" },
                new OspfAreaRow { Id = "*b2", Name = "backbone-b", AreaId = "0.0.0.0", Instance = "legacy-b" },
            ],
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            plan,
            inventory,
            OspfTestBaselines.LoopbackSlash32("10.16.0.13", "Loopback"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("refusing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ManagementDerive_Pop008_ExcludesSlash30AndSelectsMgmtSlash24()
    {
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(
            OspfTestBaselines.LoopbackSlash32("10.16.0.8", "Loopback"));
        Assert.True(loopback.Succeeded, loopback.Detail);

        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.248.62/30", "10.16.248.60", "eth-03_BH02.Tienie-AmbleRidge"),
                Addr("10.16.251.141/30", "10.16.251.140", "Vlan99"),
                Addr("10.16.8.30/27", "10.16.8.0", "Bridge-VLAN-199_MGMT"),
                Addr("10.16.8.62/27", "10.16.8.32", "Bridge-VLAN-197_MGMT"),
                Addr("10.16.8.126/26", "10.16.8.64", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.8.190/26", "10.16.8.128", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.8.254/26", "10.16.8.192", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.9.1/24", "10.16.9.0", "Bridge-UNUSED", disabled: true),
            ],
            loopback,
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.8.0/24", result.ManagementCidr);
        Assert.Contains(result.IgnoredTransportSlash30Addresses, s => s.Contains("eth-03_BH02", StringComparison.Ordinal));
        Assert.Contains(result.IgnoredTransportSlash30Addresses, s => s.Contains("Vlan99", StringComparison.Ordinal));
        Assert.Contains(result.IgnoredDisabledAddresses, s => s.Contains("Bridge-UNUSED", StringComparison.Ordinal));
    }

    [Fact]
    public void TemplateCleanup_PreservesPassiveUnrelatedDynamicTemplate_Pop012()
    {
        OspfMigrationPlan plan = new()
        {
            ImportId = Guid.Empty,
            JobId = "pop012",
            RouterIp = "10.16.0.12",
            Identity = "BR01.POP012.016.Silo",
            SourceRouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            ContentHash = "x",
            InstanceName = "default-v2",
            RouterId = "10.16.0.12",
            TargetInstanceName = "default-v2",
            TargetBackboneAreaName = "backbone-v2",
            TargetSiteAreaName = "BR01.POP012.016.Silo-v2",
            LoopbackInterfaceName = "Loopback",
            LoopbackCidr = "10.16.0.12/32",
            DerivedManagementCidr = "10.16.12.0/24",
            Areas =
            [
                new OspfPlanArea { Name = "backbone-v2", AreaId = "0.0.0.0", IsBackbone = true, TargetType = "default" },
                new OspfPlanArea
                {
                    Name = "BR01.POP012.016.Silo-v2",
                    AreaId = "10.16.0.12",
                    IsBackbone = false,
                    TargetType = "stub",
                    DefaultCost = "1",
                },
            ],
            ActiveBackhauls =
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone-v2",
                    Interface = "eth-04_BH01.Silo-RustenburgKantoor",
                    NetworkCidr = "10.16.248.20/30",
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
                    NetworkCidr = "10.16.0.12/32",
                    Interface = "Loopback",
                    Passive = true,
                },
            ],
            PassiveSiteNetworks =
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "BR01.POP012.016.Silo-v2",
                    Interfaces = "all",
                    NetworkCidr = "10.16.12.0/24,100.64.0.0/10",
                    Passive = true,
                },
            ],
        };

        List<OspfTemplateInventoryRow> rows =
        [
            new()
            {
                Id = "*1",
                Area = "backbone-v2",
                AreaInstanceName = "default-v2",
                Interfaces = "eth-04_BH01.Silo-RustenburgKantoor",
                Networks = "10.16.248.20/30",
                Type = "ptp",
                Cost = "10",
                Passive = false,
            },
            new()
            {
                Id = "*2",
                Area = "backbone-v2",
                AreaInstanceName = "default-v2",
                Interfaces = "Loopback",
                Networks = "10.16.0.12/32",
                Passive = true,
            },
            new()
            {
                Id = "*C",
                Area = "BR01.POP012.016.Silo-v2",
                AreaInstanceName = "default-v2",
                Interfaces = "all",
                Networks = "10.16.12.0/24,100.64.0.0/10",
                Passive = true,
            },
            new()
            {
                Id = "*A",
                Area = "backbone-v2",
                AreaInstanceName = "default-v2",
                Interfaces = "dynamic",
                Networks = "102.66.242.0/24",
                Type = "ptp",
                Cost = "1",
                Passive = true,
            },
        ];

        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, rows);

        Assert.Empty(report.Unknown);
        Assert.Single(report.PreservedUnmanaged);
        Assert.Equal("*A", report.PreservedUnmanaged[0].Row.Id);
        Assert.Empty(report.KnownLegacy);
    }

    [Fact]
    public void ResolveLegacyRouterId_ResolvesRoutingIdObjectToLiteralIp()
    {
        string? resolved = OspfMigrationPlanner.ResolveLegacyRouterIdValue(
            "main",
            [new OspfRoutingIdRow { Id = "*1", Name = "main", Address = "10.16.0.7" }]);

        Assert.Equal("10.16.0.7", resolved);
        Assert.Null(OspfMigrationPlanner.ResolveLegacyRouterIdValue("hostname-shaped", []));
        Assert.Equal("10.16.0.4", OspfMigrationPlanner.ResolveLegacyRouterIdValue("10.16.0.4", []));
    }

    [Fact]
    public void LoopbackDerive_RequiresTenSlashEightScope()
    {
        OspfLoopbackRouterIdResult outside = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "172.16.0.1/32", Network = "172.16.0.1", Interface = "lo" },
        ]);
        Assert.False(outside.Succeeded);

        OspfLoopbackRouterIdResult acrossFormerBoundary = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "10.255.0.1/32", Network = "10.255.0.1", Interface = "lo" },
        ]);
        Assert.True(acrossFormerBoundary.Succeeded, acrossFormerBoundary.Detail);
        Assert.Equal("10.255.0.1", acrossFormerBoundary.RouterId);

        OspfLoopbackRouterIdResult inside = OspfLoopbackRouterId.Derive(
            OspfTestBaselines.LoopbackSlash32("10.16.0.14", "Loopback"));
        Assert.True(inside.Succeeded, inside.Detail);
        Assert.Equal("10.16.0.14", inside.RouterId);
    }

    private static OspfCaptureSnapshot BaselineV7(
        string identity,
        string loopback,
        string instanceName,
        string instanceRouterId,
        IReadOnlyList<OspfIpAddressRow> mgmtAddresses,
        string backhaulInterface,
        string backhaulCidr,
        string backhaulCost,
        string? neighbourInterface = null)
    {
        string site = identity;
        List<OspfIpAddressRow> addresses =
        [
            new() { Address = $"{loopback}/32", Network = loopback, Interface = "Loopback" },
            ..mgmtAddresses,
            Addr(HostInCidr(backhaulCidr), NetworkOf(backhaulCidr), backhaulInterface),
        ];

        return new OspfCaptureSnapshot
        {
            ImportId = Guid.Empty,
            JobId = "job",
            RouterIp = IPAddress.Parse(loopback),
            Identity = identity,
            RouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances =
            [
                new OspfInstanceRow { Name = instanceName, RouterId = instanceRouterId, Version = "2" },
            ],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Instance = instanceName, Type = "default" },
                new OspfAreaRow { Name = site, AreaId = loopback, Instance = instanceName, Type = "stub", DefaultCost = "1" },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow
                {
                    Area = site,
                    Range = DeriveSlash24(mgmtAddresses[0].Address!),
                    Comment = "MGMT",
                },
            ],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = backhaulInterface,
                    Networks = backhaulCidr,
                    Type = "ptp",
                    Cost = backhaulCost,
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "Loopback",
                    Networks = $"{loopback}/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = site,
                    Interfaces = "all",
                    Networks = DeriveSlash24(mgmtAddresses[0].Address!),
                    Passive = true,
                },
            ],
            IpAddresses = addresses,
            Neighbours = string.IsNullOrWhiteSpace(neighbourInterface)
                ? []
                :
                [
                    new OspfNeighbourRow
                    {
                        Address = "10.16.248.1",
                        RouterId = "10.16.0.1",
                        State = "Full",
                        Interface = neighbourInterface,
                    },
                ],
        };
    }

    private static OspfMigrationPlan MinimalPlan(string identity, string routerId, string instanceName) =>
        new()
        {
            ImportId = Guid.Empty,
            JobId = "job",
            RouterIp = routerId,
            Identity = identity,
            SourceRouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            ContentHash = "hash",
            InstanceName = instanceName,
            RouterId = routerId,
            TargetInstanceName = "default-v2",
            TargetBackboneAreaName = "backbone-v2",
            TargetSiteAreaName = OspfFleetNaming.Ros7SiteAreaName(identity),
            LoopbackCidr = $"{routerId}/32",
            Areas =
            [
                new OspfPlanArea { Name = "backbone", AreaId = "0.0.0.0", IsBackbone = true, TargetType = "default" },
                new OspfPlanArea
                {
                    Name = identity,
                    AreaId = routerId,
                    IsBackbone = false,
                    TargetType = "stub",
                    DefaultCost = "1",
                },
            ],
            ActiveBackhauls =
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "eth-01",
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
                    NetworkCidr = $"{routerId}/32",
                    Interface = "Loopback",
                    Passive = true,
                },
            ],
            PassiveSiteNetworks =
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = identity,
                    Interfaces = "all",
                    NetworkCidr = "10.16.0.0/24",
                    Passive = true,
                },
            ],
        };

    private static OspfIpAddressRow Addr(string address, string network, string iface, bool disabled = false) =>
        new()
        {
            Address = address,
            Network = network,
            Interface = iface,
            Disabled = disabled,
            AdminState = disabled ? OspfAdminState.Disabled : OspfAdminState.Enabled,
        };

    private static string DeriveSlash24(string addressCidr)
    {
        string host = addressCidr.Split('/')[0];
        string[] parts = host.Split('.');
        return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
    }

    private static string NetworkOf(string cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p) ? FormatHost(p.NetworkAddress) : cidr;

    private static string HostInCidr(string cidr)
    {
        if (!OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p))
        {
            return cidr;
        }

        return FormatHost(p.NetworkAddress + 1) + "/" + p.PrefixLength;
    }

    private static string FormatHost(uint host) =>
        $"{(host >> 24) & 0xff}.{(host >> 16) & 0xff}.{(host >> 8) & 0xff}.{host & 0xff}";
}
