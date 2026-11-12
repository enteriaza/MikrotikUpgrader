// -----------------------------------------------------------------------------
// File: OspfFleetNaming.cs
//
// Description:
// Deterministic ROS6/ROS7 OSPF object naming from fleet conventions (not
// heuristic discovery).
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Deterministic ROS6/ROS7 OSPF object naming from fleet conventions (not heuristic discovery).
/// </summary>
internal static class OspfFleetNaming
{
    /// <summary>
    /// Name of the OSPF instance on RouterOS 6 routers.
    /// </summary>
    public const string Ros6InstanceName = "default";
    /// <summary>
    /// Name of the OSPF instance on RouterOS 7 routers.
    /// </summary>
    public const string Ros7InstanceName = "default-v2";
    /// <summary>
    /// Name of the backbone area on RouterOS 6 routers.
    /// </summary>
    public const string Ros6BackboneAreaName = "backbone";
    /// <summary>
    /// Name of the backbone area on RouterOS 7 routers.
    /// </summary>
    public const string Ros7BackboneAreaName = "backbone-v2";
    /// <summary>
    /// Area ID of the backbone area.
    /// </summary>
    public const string BackboneAreaId = "0.0.0.0";
    /// <summary>
    /// Fleet-standard <c>default-cost</c> for site stub areas.
    /// </summary>
    public const string SiteDefaultCost = "1";
    /// <summary>
    /// Comment that marks the management area range.
    /// </summary>
    public const string ManagementRangeComment = "MGMT";

    /// <summary>
    /// Deterministic ROS7 site area name for the given site number.
    /// </summary>
    public static string Ros7SiteAreaName(string routerIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routerIdentity);
        string id = routerIdentity.Trim();
        return id.EndsWith("-v2", StringComparison.OrdinalIgnoreCase) ? id : id + "-v2";
    }

    /// <summary>
    /// Canonical source/capture OSPF instance name.
    /// </summary>
    public static string SourceInstanceName(OspfCaptureSourceKind kind) =>
        kind == OspfCaptureSourceKind.RouterOsV7 ? Ros7InstanceName : Ros6InstanceName;

    /// <summary>
    /// Canonical target ROS7 OSPF instance name.
    /// </summary>
    public static string TargetInstanceName() => Ros7InstanceName;

    /// <summary>
    /// Canonical target backbone area name.
    /// </summary>
    public static string TargetBackboneAreaName() => Ros7BackboneAreaName;

    /// <summary>
    /// Canonical target RouterOS 7 site area name for a router; equivalent to <see cref="Ros7SiteAreaName"/>.
    /// </summary>
    /// <param name="routerIdentity">RouterOS identity.</param>
    /// <returns>The site area name.</returns>
    public static string TargetSiteAreaName(string routerIdentity) => Ros7SiteAreaName(routerIdentity);
}
