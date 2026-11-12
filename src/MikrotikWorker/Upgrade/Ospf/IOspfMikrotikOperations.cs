// -----------------------------------------------------------------------------
// File: IOspfMikrotikOperations.cs
//
// Description:
// Narrow RouterOS OSPF/filter operations abstraction for capture, inventory
// reads, and reconciliation mutations.
//
// -----------------------------------------------------------------------------

using tik4net;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Narrow RouterOS OSPF/filter operations for capture and reconciliation.
/// </summary>
internal interface IOspfMikrotikOperations
{
    /// <summary>
    /// Captures the router OSPF configuration, IP addresses, and routing filters as an immutable snapshot, using the v6 or v7 menus selected by the request.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="request">Identity and source-version metadata for the capture.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The captured snapshot.</returns>
    Task<OspfCaptureSnapshot> CaptureAsync(
        ITikConnection connection,
        OspfCaptureRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every routing filter rule using the menus that match the router RouterOS major version.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the filters are removed.</returns>
    Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the routing filter rules present, using the menus that match the router RouterOS major version.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The number of filter rules.</returns>
    Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the <c>redistribute</c> setting on the named OSPFv2 instance. OSPFv3 instances are skipped.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="instanceName">Name of the OSPFv2 instance.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the setting is cleared.</returns>
    Task ClearOspfRedistributionAsync(
        ITikConnection connection,
        string instanceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsets filter-chain properties on the named OSPFv2 instance (resolved live name).
    /// Must not mutate OSPFv3 instances (version=3) or unrelated OSPFv2 instances.
    /// </summary>
    Task ClearOspfInstanceFilterReferencesAsync(
        ITikConnection connection,
        string instanceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates an OSPF area to match the plan, without setting <c>no-summaries</c>.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="instanceName">Name of the owning OSPF instance.</param>
    /// <param name="area">Planned area definition.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the area matches the plan.</returns>
    Task EnsureOspfAreaAsync(
        ITikConnection connection,
        string instanceName,
        OspfPlanArea area,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the planned OSPF area range when no range with the same area and prefix exists.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="range">Planned area range.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the range is present.</returns>
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

    /// <summary>
    /// Ensures the passive backbone loopback interface-template exists and is correct. Fails closed on ambiguous matches or when it would alter a protected backhaul.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="template">Planned passive loopback template.</param>
    /// <param name="protectedBackhauls">Designated backhaul templates that must not be mutated.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the template matches the plan.</returns>
    Task EnsureLoopbackTemplateAsync(
        ITikConnection connection,
        OspfPlanLoopbackTemplate template,
        IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the passive site network interface-template exists and verifies it by re-reading live state. Must not alter a protected backhaul.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="template">Planned passive site matcher.</param>
    /// <param name="protectedBackhauls">Designated backhaul templates that must not be mutated.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the template is present and verified.</returns>
    Task EnsureSiteNetworkTemplateAsync(
        ITikConnection connection,
        OspfPlanSiteNetworkTemplate template,
        IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Independently re-reads live interface-templates and verifies the planned site matcher.
    /// Must succeed before <see cref="RemoveObsoleteTemplatesAsync"/>.
    /// </summary>
    Task AssertSiteNetworkTemplateStateAsync(
        ITikConnection connection,
        OspfPlanSiteNetworkTemplate template,
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

    /// <summary>
    /// Records the backbone template, OSPF neighbour, and management route state before any mutation so later changes can be detected.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="plan">Migration plan that identifies the designated backhaul.</param>
    /// <param name="managementHost">Host address whose route is observed.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The baseline used by <see cref="AssertBackboneStillActiveAsync"/>.</returns>
    Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        string managementHost,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies after a mutation that the backhaul is still active, the neighbour is still Full when it was at baseline, and the management route is unchanged.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="plan">Migration plan that identifies the designated backhauls.</param>
    /// <param name="baseline">State captured before mutation.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when all checks pass.</returns>
    /// <exception cref="OspfBackboneMutationGuardException">Thrown when backbone or management-plane safety is violated.</exception>
    Task AssertBackboneStillActiveAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        OspfManagementPlaneBaseline baseline,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the OSPF neighbour table.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>One field dictionary per neighbour row.</returns>
    Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the OSPFv3 objects selected for removal, failing closed when the selection is unsafe.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="plan">Migration plan being applied.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the objects are removed.</returns>
    Task RemoveOspfV3Async(
        ITikConnection connection,
        OspfMigrationPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the live RouterOS 7 OSPF inventory of instances, areas, ranges, templates, filters, and OSPFv3 objects.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The live inventory snapshot.</returns>
    Task<OspfV7Inventory> ReadV7InventoryAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="routerId"/> to the <c>router-id</c> property of the named OSPF instance by
    /// resolving its RouterOS <c>.id</c> and issuing <c>/routing/ospf/instance/set</c>. Changes only that
    /// one property; areas, templates and other instances are not touched.
    /// </summary>
    /// <param name="connection">Open RouterOS API connection.</param>
    /// <param name="instanceName">Name of the OSPF instance.</param>
    /// <param name="routerId">Router ID to apply.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when the router ID is set.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the instance is not found.</exception>
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

/// <summary>
/// Inputs describing the device/job for an OSPF capture.
/// </summary>
internal sealed class OspfCaptureRequest
{
    /// <summary>
    /// Import id of the scheduled job.
    /// </summary>
    public required Guid ImportId { get; init; }
    /// <summary>
    /// Scheduled job id.
    /// </summary>
    public required string JobId { get; init; }
    /// <summary>
    /// Router management IP.
    /// </summary>
    public required System.Net.IPAddress RouterIp { get; init; }
    /// <summary>
    /// RouterOS identity string.
    /// </summary>
    public required string Identity { get; init; }
    /// <summary>
    /// Installed RouterOS version string.
    /// </summary>
    public required string RouterOsVersion { get; init; }
    /// <summary>
    /// Device architecture when known.
    /// </summary>
    public required string? Architecture { get; init; }
    /// <summary>
    /// Whether capture should use v6 or v7 OSPF menus.
    /// </summary>
    public required OspfCaptureSourceKind SourceKind { get; init; }
}

/// <summary>
/// Live RouterOS 7 OSPF inventory snapshot used during reconcile/verify.
/// </summary>
internal sealed class OspfV7Inventory
{
    /// <summary>
    /// Live OSPF instances.
    /// </summary>
    public IReadOnlyList<OspfInstanceRow> Instances { get; init; } = [];
    /// <summary>
    /// Live OSPF areas.
    /// </summary>
    public IReadOnlyList<OspfAreaRow> Areas { get; init; } = [];
    /// <summary>
    /// Live OSPF area ranges.
    /// </summary>
    public IReadOnlyList<OspfAreaRangeRow> AreaRanges { get; init; } = [];
    /// <summary>
    /// Live OSPF interface templates.
    /// </summary>
    public IReadOnlyList<OspfInterfaceTemplateRow> Templates { get; init; } = [];
    /// <summary>
    /// Live routing filters in scope.
    /// </summary>
    public IReadOnlyList<OspfRoutingFilterRow> Filters { get; init; } = [];
    /// <summary>
    /// Live OSPFv3 object references.
    /// </summary>
    public IReadOnlyList<OspfV3ObjectRef> OspfV3Objects { get; init; } = [];
    /// <summary>
    /// Mutating commands issued during reconcile (for diagnostics).
    /// </summary>
    public IReadOnlyList<string> IssuedMutatingCommands { get; init; } = [];
}
