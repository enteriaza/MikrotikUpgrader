namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Deterministic ROS6/ROS7 OSPF object naming from fleet conventions (not heuristic discovery).
/// </summary>
internal static class OspfFleetNaming
{
    public const string Ros6InstanceName = "default";
    public const string Ros7InstanceName = "default-v2";
    public const string Ros6BackboneAreaName = "backbone";
    public const string Ros7BackboneAreaName = "backbone-v2";
    public const string BackboneAreaId = "0.0.0.0";
    public const string SiteDefaultCost = "1";
    public const string ManagementRangeComment = "MGMT";

    public static string Ros7SiteAreaName(string routerIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routerIdentity);
        string id = routerIdentity.Trim();
        return id.EndsWith("-v2", StringComparison.OrdinalIgnoreCase) ? id : id + "-v2";
    }

    public static string SourceInstanceName(OspfCaptureSourceKind kind) =>
        kind == OspfCaptureSourceKind.RouterOsV7 ? Ros7InstanceName : Ros6InstanceName;

    public static string TargetInstanceName() => Ros7InstanceName;

    public static string TargetBackboneAreaName() => Ros7BackboneAreaName;

    public static string TargetSiteAreaName(string routerIdentity) => Ros7SiteAreaName(routerIdentity);
}
