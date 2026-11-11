using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Ospf;

internal static class OspfTestBaselines
{
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
}
