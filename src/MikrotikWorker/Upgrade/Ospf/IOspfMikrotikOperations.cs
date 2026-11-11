using tik4net;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Narrow RouterOS OSPF/filter operations for capture and reconciliation.
/// </summary>
internal interface IOspfMikrotikOperations
{
    Task<OspfCaptureSnapshot> CaptureAsync(
        ITikConnection connection,
        OspfCaptureRequest request,
        CancellationToken cancellationToken = default);

    Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default);

    Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default);

    Task ClearOspfRedistributionAsync(
        ITikConnection connection,
        string instanceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsets <c>in-filter</c> and <c>out-filter</c> on every OSPFv2 instance.
    /// Must not mutate OSPFv3 instances (version=3).
    /// </summary>
    Task ClearOspfInstanceFilterReferencesAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default);

    Task EnsureOspfAreaAsync(
        ITikConnection connection,
        string instanceName,
        OspfPlanArea area,
        CancellationToken cancellationToken = default);

    Task EnsureOspfAreaRangeAsync(
        ITikConnection connection,
        OspfPlanAreaRange range,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the designated active backhaul template. Must never enable the RouterOS <c>passive</c> flag.
    /// Performs read-back; throws <see cref="OspfBackboneMutationGuardException"/> on safety failure.
    /// </summary>
    Task EnsureBackhaulTemplateAsync(
        ITikConnection connection,
        OspfPlanBackhaulTemplate template,
        CancellationToken cancellationToken = default);

    Task EnsureLoopbackTemplateAsync(
        ITikConnection connection,
        OspfPlanLoopbackTemplate template,
        IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
        CancellationToken cancellationToken = default);

    Task EnsureSiteNetworkTemplateAsync(
        ITikConnection connection,
        OspfPlanSiteNetworkTemplate template,
        IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes obsolete OSPF interface-templates that are not the planned backhaul, loopback, or
    /// consolidated site matcher. Disabled leftovers are removed — the final inventory must contain
    /// exactly the planned templates.
    /// </summary>
    Task RemoveObsoleteTemplatesAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        CancellationToken cancellationToken = default);

    Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        string managementHost,
        CancellationToken cancellationToken = default);

    Task AssertBackboneStillActiveAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        OspfManagementPlaneBaseline baseline,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default);

    Task RemoveOspfV3Async(
        ITikConnection connection,
        OspfMigrationPlan plan,
        CancellationToken cancellationToken = default);

    Task<OspfV7Inventory> ReadV7InventoryAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default);

    Task SetInstanceRouterIdAsync(
        ITikConnection connection,
        string instanceName,
        string routerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads <c>/ip/address</c> (all assigned IPv4 addresses, including those on disabled interfaces
    /// when RouterOS still exposes them). Failures must propagate — callers fail closed.
    /// </summary>
    Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default);
}

internal sealed class OspfCaptureRequest
{
    public required Guid ImportId { get; init; }
    public required string JobId { get; init; }
    public required System.Net.IPAddress RouterIp { get; init; }
    public required string Identity { get; init; }
    public required string RouterOsVersion { get; init; }
    public required string? Architecture { get; init; }
    public required OspfCaptureSourceKind SourceKind { get; init; }
}

internal sealed class OspfV7Inventory
{
    public IReadOnlyList<OspfInstanceRow> Instances { get; init; } = [];
    public IReadOnlyList<OspfAreaRow> Areas { get; init; } = [];
    public IReadOnlyList<OspfAreaRangeRow> AreaRanges { get; init; } = [];
    public IReadOnlyList<OspfInterfaceTemplateRow> Templates { get; init; } = [];
    public IReadOnlyList<OspfRoutingFilterRow> Filters { get; init; } = [];
    public IReadOnlyList<OspfV3ObjectRef> OspfV3Objects { get; init; } = [];
    public IReadOnlyList<string> IssuedMutatingCommands { get; init; } = [];
}
