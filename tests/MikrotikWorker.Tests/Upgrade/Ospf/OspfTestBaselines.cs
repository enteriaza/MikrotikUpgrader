using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
internal static class OspfTestBaselines
{
    /// <summary>
    /// Authoritative Loopback <c>/32</c> address row used by identity resolution fakes.
    /// </summary>
    public static IReadOnlyList<OspfIpAddressRow> LoopbackSlash32(
        string routerId,
        string interfaceName = OspfLoopbackRouterId.InterfaceNameLoopback) =>
    [
        new OspfIpAddressRow
        {
            Address = $"{routerId}/32",
            Network = routerId,
            Interface = interfaceName,
        },
    ];

    public static IReadOnlyList<OspfIpAddressRow> LoopbackSlash32ForPlan(OspfMigrationPlan plan) =>
        LoopbackSlash32(
            plan.RouterId,
            plan.LoopbackInterfaceName
            ?? plan.PassiveLoopbacks.FirstOrDefault()?.Interface
            ?? OspfLoopbackRouterId.InterfaceNameLoopback);

    /// <summary>Eligible management address deriving <paramref name="managementSlash24"/>.</summary>
    public static OspfIpAddressRow ManagementAddress(string hostCidr, string managementSlash24, string iface) =>
        new()
        {
            Address = hostCidr,
            Network = managementSlash24.Contains('/') ? managementSlash24[..managementSlash24.IndexOf('/')] : managementSlash24,
            Interface = iface,
        };

    public static OspfManagementPlaneBaseline Minimal(string managementHost = "198.18.240.11") => new()
    {
        ManagementHost = managementHost,
        BackboneInterface = null,
        BackboneTemplateId = null,
        BackboneArea = null,
        BackboneNetwork = null,
        BackboneWasActive = true,
        BackboneTemplatePassive = false,
        BackboneTemplateDisabled = false,
        NeighbourAddress = null,
        NeighbourRouterId = null,
        NeighbourState = "Full",
        NeighbourWasFull = true,
        ManagementRouteDst = null,
        ManagementRouteGateway = null,
        ManagementRouteImmediateGw = null,
        ManagementOutgoingInterface = null,
        ManagementRouteActive = false,
        ManagementRouteObserved = false,
        ManagementRouteDependsOnBackbone = false,
    };

    public static OspfManagementPlaneBaseline Tommy(string managementHost = "198.18.240.11") => new()
    {
        ManagementHost = managementHost,
        BackboneInterface = "eth-01_BH01.Tommy-Shylock",
        BackboneTemplateId = "*1",
        BackboneArea = "backbone-v2",
        BackboneNetwork = "10.16.248.76/30",
        BackboneWasActive = true,
        BackboneTemplatePassive = false,
        BackboneTemplateDisabled = false,
        NeighbourAddress = "10.16.248.77",
        NeighbourRouterId = "198.18.255.253",
        NeighbourState = "Full",
        NeighbourWasFull = true,
        ManagementRouteDst = "0.0.0.0/0",
        ManagementRouteGateway = "10.16.248.77%eth-01_BH01.Tommy-Shylock",
        ManagementRouteImmediateGw = "10.16.248.77%eth-01_BH01.Tommy-Shylock",
        ManagementOutgoingInterface = "eth-01_BH01.Tommy-Shylock",
        ManagementRouteActive = true,
        ManagementRouteObserved = true,
        ManagementRouteDependsOnBackbone = true,
    };

    /// <summary>
    /// Live inventory using deterministic ROS7 target names from the plan.
    /// </summary>
    public static OspfV7Inventory InventoryMatchingPlan(OspfMigrationPlan plan)
    {
        string liveInstance = string.IsNullOrWhiteSpace(plan.TargetInstanceName)
            ? OspfFleetNaming.Ros7InstanceName
            : plan.TargetInstanceName;
        string backbone = string.IsNullOrWhiteSpace(plan.TargetBackboneAreaName)
            ? OspfFleetNaming.Ros7BackboneAreaName
            : plan.TargetBackboneAreaName;
        // Match OspfLiveIdentityResolver: TargetSiteAreaName, else deterministic {Identity}-v2.
        string site = !string.IsNullOrWhiteSpace(plan.TargetSiteAreaName)
            ? plan.TargetSiteAreaName
            : !string.IsNullOrWhiteSpace(plan.Identity)
                ? OspfFleetNaming.Ros7SiteAreaName(plan.Identity)
                : plan.Areas.FirstOrDefault(static a => !a.IsBackbone)?.Name
                  ?? "site";

        return new OspfV7Inventory
        {
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*inst1",
                    Name = liveInstance,
                    RouterId = plan.RouterId,
                    Version = "2",
                },
            ],
            Areas = plan.Areas
                .Select((a, i) => new OspfAreaRow
                {
                    Id = $"*a{i}",
                    Name = a.IsBackbone ? backbone : site,
                    AreaId = a.AreaId,
                    Instance = liveInstance,
                    Type = a.TargetType,
                    DefaultCost = a.DefaultCost,
                    NoSummaries = a.NoSummaries ? true : false,
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Tommy-style ROS7 conversion inventory for a ROS6 logical plan (<c>default</c>/<c>backbone</c>).
    /// </summary>
    public static OspfV7Inventory TommyConvertedFromRos6Plan(OspfMigrationPlan plan)
    {
        string liveInstance = OspfFleetNaming.Ros7InstanceName;
        string backbone = OspfFleetNaming.Ros7BackboneAreaName;
        string site = string.IsNullOrWhiteSpace(plan.TargetSiteAreaName)
            ? OspfFleetNaming.Ros7SiteAreaName(plan.Identity)
            : plan.TargetSiteAreaName;

        return new OspfV7Inventory
        {
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*1",
                    Name = liveInstance,
                    RouterId = plan.RouterId,
                    Version = "2",
                },
            ],
            Areas = plan.Areas
                .Select(a => new OspfAreaRow
                {
                    Id = a.IsBackbone ? "*a0" : "*a1",
                    Name = a.IsBackbone ? backbone : site,
                    AreaId = a.AreaId,
                    Instance = liveInstance,
                    Type = a.TargetType,
                    DefaultCost = a.DefaultCost,
                })
                .ToList(),
        };
    }
}
