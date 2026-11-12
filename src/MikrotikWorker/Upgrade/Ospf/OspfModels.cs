// -----------------------------------------------------------------------------
// File: OspfModels.cs
//
// Description:
// Shared OSPF capture/plan/verification model types used across migration
// planner, reconciler, and services.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Text.Json.Serialization;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Whether OSPF capture came from RouterOS 6 or RouterOS 7 menus.
/// </summary>
internal enum OspfCaptureSourceKind
{
    /// <summary>Capture used RouterOS 6 OSPF menus.</summary>
    RouterOsV6 = 0,

    /// <summary>Capture used RouterOS 7 OSPF menus.</summary>
    RouterOsV7 = 1,
}

/// <summary>
/// Immutable capture of OSPF-related rows from a router at plan time.
/// </summary>
internal sealed class OspfCaptureSnapshot
{
    /// <summary>
    /// Import id of the scheduled job that captured this snapshot/plan.
    /// </summary>
    public required Guid ImportId { get; init; }
    /// <summary>
    /// Scheduled job id.
    /// </summary>
    public required string JobId { get; init; }
    /// <summary>
    /// Router management IP.
    /// </summary>
    public required IPAddress RouterIp { get; init; }
    /// <summary>
    /// RouterOS identity string at capture/plan time.
    /// </summary>
    public required string Identity { get; init; }
    /// <summary>
    /// RouterOS version string at capture/plan time.
    /// </summary>
    public required string RouterOsVersion { get; init; }
    /// <summary>
    /// Device architecture when known.
    /// </summary>
    public required string? Architecture { get; init; }
    /// <summary>
    /// Whether capture used v6 or v7 OSPF menus.
    /// </summary>
    public required OspfCaptureSourceKind SourceKind { get; init; }

    /// <summary>
    /// Captured OSPF instances.
    /// </summary>
    public IReadOnlyList<OspfInstanceRow> Instances { get; init; } = [];
    /// <summary>
    /// Captured OSPF areas.
    /// </summary>
    public IReadOnlyList<OspfAreaRow> Areas { get; init; } = [];
    /// <summary>
    /// Captured OSPF area ranges.
    /// </summary>
    public IReadOnlyList<OspfAreaRangeRow> AreaRanges { get; init; } = [];
    /// <summary>
    /// Captured v6 OSPF network statements.
    /// </summary>
    public IReadOnlyList<OspfNetworkRow> Networks { get; init; } = [];
    /// <summary>
    /// Captured OSPF interfaces when present.
    /// </summary>
    public IReadOnlyList<OspfInterfaceRow> Interfaces { get; init; } = [];
    /// <summary>
    /// Captured v7 interface templates.
    /// </summary>
    public IReadOnlyList<OspfInterfaceTemplateRow> InterfaceTemplates { get; init; } = [];
    /// <summary>
    /// Captured IP address inventory.
    /// </summary>
    public IReadOnlyList<OspfIpAddressRow> IpAddresses { get; init; } = [];
    /// <summary>
    /// Captured routing-filter rows.
    /// </summary>
    public IReadOnlyList<OspfRoutingFilterRow> RoutingFilters { get; init; } = [];
    /// <summary>
    /// Captured OSPFv3 object references.
    /// </summary>
    public IReadOnlyList<OspfV3ObjectRef> OspfV3Objects { get; init; } = [];
}

/// <summary>
/// Captured OSPF instance row.
/// </summary>
internal sealed class OspfInstanceRow
{
    /// <summary>
    /// RouterOS object <c>.id</c> when present.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// Object name as reported by RouterOS.
    /// </summary>
    public string? Name { get; init; }
    /// <summary>
    /// OSPF Router ID value.
    /// </summary>
    public string? RouterId { get; init; }
    /// <summary>
    /// OSPF protocol version string when present.
    /// </summary>
    public string? Version { get; init; }
    /// <summary>
    /// True when this is the default OSPF instance.
    /// </summary>
    public bool IsDefault { get; init; }
    /// <summary>Redistribution-related properties read from the instance (<c>redistribute</c>, <c>redistribute-static</c>, <c>redistribute-connected</c>, <c>redistribute-other</c>) keyed case-insensitively; only properties present on the router are included.</summary>
    public Dictionary<string, string?> RedistributeProperties { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Live <c>in-filter</c> (null/empty means unset).</summary>
    public string? InFilter { get; init; }

    /// <summary>Live <c>out-filter</c> (null/empty means unset).</summary>
    public string? OutFilter { get; init; }
}

/// <summary>
/// Captured OSPF area row.
/// </summary>
internal sealed class OspfAreaRow
{
    /// <summary>
    /// RouterOS object <c>.id</c> when present.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// Object name as reported by RouterOS.
    /// </summary>
    public string? Name { get; init; }
    /// <summary>
    /// OSPF area-id value.
    /// </summary>
    public string? AreaId { get; init; }
    /// <summary>
    /// Owning OSPF instance name.
    /// </summary>
    public string? Instance { get; init; }
    /// <summary>
    /// OSPF area or interface type string.
    /// </summary>
    public string? Type { get; init; }
    /// <summary>
    /// Area default-cost when present.
    /// </summary>
    public string? DefaultCost { get; init; }
    /// <summary>
    /// Area no-summaries flag when present.
    /// </summary>
    public bool? NoSummaries { get; init; }
    /// <summary>
    /// Area inject-summary-lsas setting when present.
    /// </summary>
    public string? InjectSummaryLsas { get; init; }
}

/// <summary>
/// Captured OSPF area-range row.
/// </summary>
internal sealed class OspfAreaRangeRow
{
    /// <summary>
    /// RouterOS object <c>.id</c> when present.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// OSPF area name or reference.
    /// </summary>
    public string? Area { get; init; }
    /// <summary>
    /// Area-range prefix.
    /// </summary>
    public string? Range { get; init; }
    /// <summary>
    /// RouterOS comment field.
    /// </summary>
    public string? Comment { get; init; }
    /// <summary>
    /// Whether the area-range is advertised.
    /// </summary>
    public bool? Advertise { get; init; }
}

/// <summary>
/// Captured OSPF network statement row (v6).
/// </summary>
internal sealed class OspfNetworkRow
{
    /// <summary>
    /// RouterOS object <c>.id</c> when present.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// OSPF area name or reference.
    /// </summary>
    public string? Area { get; init; }
    /// <summary>
    /// Network prefix or network property value.
    /// </summary>
    public string? Network { get; init; }
    /// <summary>
    /// RouterOS comment field.
    /// </summary>
    public string? Comment { get; init; }
}

/// <summary>
/// Captured OSPF interface row when present.
/// </summary>
internal sealed class OspfInterfaceRow
{
    /// <summary>
    /// RouterOS object <c>.id</c> when present.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// Interface name.
    /// </summary>
    public string? Interface { get; init; }
    /// <summary>
    /// OSPF network type on an interface.
    /// </summary>
    public string? NetworkType { get; init; }
    /// <summary>
    /// Raw RouterOS <c>cost=</c> value of the interface exactly as printed (for example <c>10</c>),
    /// kept as text so a missing or non-numeric value survives capture unchanged. The planner records it
    /// as the backhaul <c>SourceCost</c> and normalises the planned cost to
    /// <see cref="OspfMigrationPlanner.FleetBackhaulCost"/>. <see langword="null"/> when no cost is reported.
    /// </summary>
    public string? Cost { get; init; }
    /// <summary>
    /// Whether the template/interface is passive.
    /// </summary>
    public bool Passive { get; init; }
}

/// <summary>
/// Captured OSPF interface-template row (v7).
/// </summary>
internal sealed class OspfInterfaceTemplateRow
{
    /// <summary>
    /// RouterOS object <c>.id</c> when present.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// OSPF area name or reference.
    /// </summary>
    public string? Area { get; init; }
    /// <summary>
    /// Template <c>interfaces=</c> matcher.
    /// </summary>
    public string? Interfaces { get; init; }
    /// <summary>
    /// Template <c>networks=</c> matcher list.
    /// </summary>
    public string? Networks { get; init; }
    /// <summary>
    /// OSPF area or interface type string.
    /// </summary>
    public string? Type { get; init; }
    /// <summary>
    /// Raw RouterOS <c>cost=</c> of the interface-template as printed, kept as text so an absent or
    /// non-numeric value is captured faithfully. Cleanup classification and V7 reconciliation compare
    /// it with <see cref="OspfMigrationPlanner.FleetBackhaulCost"/> for backhaul templates.
    /// <see langword="null"/> when the template sets no explicit cost.
    /// </summary>
    public string? Cost { get; init; }
    /// <summary>
    /// Whether the template/interface is passive.
    /// </summary>
    public bool Passive { get; init; }
    /// <summary>
    /// Whether the template is disabled.
    /// </summary>
    public bool Disabled { get; init; }
    /// <summary>
    /// RouterOS comment field.
    /// </summary>
    public string? Comment { get; init; }
    /// <summary>RouterOS <c>auth</c> method (simple/md5/sha*); unset means authentication unused.</summary>
    public string? Auth { get; init; }
    /// <summary>
    /// Authentication key id when present.
    /// </summary>
    public string? AuthId { get; init; }
    /// <summary>Raw <c>auth-key</c> from capture only; never logged. Prefer fingerprint on provenance.</summary>
    public string? AuthKey { get; init; }
    /// <summary>Raw <c>authentication-key</c> alias from capture only; never logged. Prefer fingerprint on provenance.</summary>
    public string? AuthenticationKey { get; init; }
    /// <summary>
    /// OSPF priority when present.
    /// </summary>
    public string? Priority { get; init; }
    /// <summary>Raw <c>use-bfd</c> API value when present; null means absent.</summary>
    public string? UseBfd { get; init; }
    /// <summary>
    /// Hello interval when present.
    /// </summary>
    public string? HelloInterval { get; init; }
    /// <summary>
    /// Dead interval when present.
    /// </summary>
    public string? DeadInterval { get; init; }
    /// <summary>
    /// Retransmit interval when present.
    /// </summary>
    public string? RetransmitInterval { get; init; }
    /// <summary>
    /// Transmit delay when present.
    /// </summary>
    public string? TransmitDelay { get; init; }
    /// <summary>OSPF protocol multi-instance id on the template (not /routing/ospf/instance name).</summary>
    public string? InstanceId { get; init; }
    /// <summary>
    /// Prefix-list reference when present.
    /// </summary>
    public string? PrefixList { get; init; }
    /// <summary>
    /// Virtual-link neighbour id when present.
    /// </summary>
    public string? VlinkNeighborId { get; init; }
    /// <summary>
    /// Virtual-link transit area when present.
    /// </summary>
    public string? VlinkTransitArea { get; init; }
}

/// <summary>
/// Captured IP address inventory row used for isolation/role checks.
/// </summary>
internal sealed class OspfIpAddressRow
{
    /// <summary>
    /// RouterOS object <c>.id</c> when present.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// IP address (CIDR or host) from inventory.
    /// </summary>
    public string? Address { get; init; }
    /// <summary>
    /// Network prefix or network property value.
    /// </summary>
    public string? Network { get; init; }
    /// <summary>
    /// Interface name.
    /// </summary>
    public string? Interface { get; init; }
}

/// <summary>
/// Captured routing-filter row relevant to OSPF migration.
/// </summary>
internal sealed class OspfRoutingFilterRow
{
    /// <summary>
    /// RouterOS object <c>.id</c> when present.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// Object name as reported by RouterOS.
    /// </summary>
    public string? Name { get; init; }
}

/// <summary>Captured OSPFv3 object reference (v6 /routing ospf-v3 or v7 version=3 dependents).</summary>
internal sealed class OspfV3ObjectRef
{
    /// <summary>
    /// RouterOS menu path for the referenced object.
    /// </summary>
    public required string Path { get; init; }
    /// <summary>
    /// RouterOS object <c>.id</c> when present.
    /// </summary>
    public string? Id { get; init; }
    /// <summary>
    /// Object name as reported by RouterOS.
    /// </summary>
    public string? Name { get; init; }
    /// <summary>
    /// Object kind classifier for OSPFv3 references.
    /// </summary>
    public string? Kind { get; init; }
}

/// <summary>
/// Immutable fleet-standard OSPF migration plan produced by the planner.
/// </summary>
internal sealed class OspfMigrationPlan
{
    /// <summary>
    /// Import id of the scheduled job that owns this plan.
    /// </summary>
    public required Guid ImportId { get; init; }
    /// <summary>
    /// Scheduled job id.
    /// </summary>
    public required string JobId { get; init; }
    /// <summary>
    /// Router management IP as text.
    /// </summary>
    public required string RouterIp { get; init; }
    /// <summary>
    /// RouterOS identity at plan time.
    /// </summary>
    public required string Identity { get; init; }
    /// <summary>
    /// RouterOS version string when the plan was captured.
    /// </summary>
    public required string SourceRouterOsVersion { get; init; }
    /// <summary>
    /// Device architecture when known.
    /// </summary>
    public required string? Architecture { get; init; }
    /// <summary>
    /// Whether capture used v6 or v7 OSPF menus.
    /// </summary>
    public required OspfCaptureSourceKind SourceKind { get; init; }
    /// <summary>
    /// Hash of plan content used for persistence integrity checks.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Logical/source OSPF instance name (ROS6 <c>default</c> or ROS7 <c>default-v2</c>).</summary>
    public required string InstanceName { get; init; }

    /// <summary>
    /// OSPF Router ID value.
    /// </summary>
    public required string RouterId { get; init; }

    /// <summary>Deterministic ROS7 target instance name (<c>default-v2</c>).</summary>
    public string TargetInstanceName { get; init; } = OspfFleetNaming.Ros7InstanceName;

    /// <summary>Deterministic ROS7 backbone area name (<c>backbone-v2</c>).</summary>
    public string TargetBackboneAreaName { get; init; } = OspfFleetNaming.Ros7BackboneAreaName;

    /// <summary>Deterministic ROS7 site area name (<c>{Identity}-v2</c>).</summary>
    public string TargetSiteAreaName { get; init; } = string.Empty;

    /// <summary>
    /// Selected Loopback interface name when known.
    /// </summary>
    public string? LoopbackInterfaceName { get; init; }

    /// <summary>
    /// Selected Loopback CIDR used for Router ID derivation.
    /// </summary>
    public string? LoopbackCidr { get; init; }

    /// <summary>
    /// Distinct in-scope (<c>10.0.0.0/8</c>) Loopback hosts observed during Router ID selection.
    /// </summary>
    public IReadOnlyList<string> ObservedInScopeLoopbackAddresses { get; init; } = [];

    /// <summary>
    /// Outside-range Loopback hosts ignored for migration identity (configuration is preserved).
    /// </summary>
    public IReadOnlyList<string> IgnoredOutsideRangeLoopbackAddresses { get; init; } = [];

    /// <summary>Mandatory site management summary <c>/24</c> derived from interface addresses.</summary>
    public string? DerivedManagementCidr { get; init; }

    /// <summary>
    /// Planned OSPF areas.
    /// </summary>
    public IReadOnlyList<OspfPlanArea> Areas { get; init; } = [];
    /// <summary>
    /// Planned OSPF area ranges.
    /// </summary>
    public IReadOnlyList<OspfPlanAreaRange> AreaRanges { get; init; } = [];
    /// <summary>
    /// Planned active backhaul templates.
    /// </summary>
    public IReadOnlyList<OspfPlanBackhaulTemplate> ActiveBackhauls { get; init; } = [];
    /// <summary>
    /// Planned passive loopback templates.
    /// </summary>
    public IReadOnlyList<OspfPlanLoopbackTemplate> PassiveLoopbacks { get; init; } = [];
    /// <summary>
    /// Planned passive site/customer templates.
    /// </summary>
    public IReadOnlyList<OspfPlanSiteNetworkTemplate> PassiveSiteNetworks { get; init; } = [];

    /// <summary>
    /// Source matchers superseded by planning (e.g. consolidation). Empty means cleanup cannot
    /// positively identify KnownLegacy rows and must treat extras as Unknown.
    /// </summary>
    public IReadOnlyList<OspfSupersededTemplateIdentity> SupersededTemplates { get; init; } = [];

    /// <summary>
    /// When true, migration removes in-scope routing filters.
    /// </summary>
    public bool RemoveAllRoutingFilters { get; init; } = true;
    /// <summary>
    /// When true, migration clears OSPFv2 redistribution properties.
    /// </summary>
    public bool ClearAllRedistribution { get; init; } = true;
    /// <summary>
    /// When true, migration removes selected OSPFv3 objects.
    /// </summary>
    public bool RemoveAllOspfV3 { get; init; } = true;
    /// <summary>
    /// OSPFv3 objects selected for removal.
    /// </summary>
    public IReadOnlyList<OspfV3ObjectRef> OspfV3ToRemove { get; init; } = [];

    /// <summary>
    /// Non-fatal normalisation notes produced while building the plan.
    /// </summary>
    public IReadOnlyList<string> NormalisationNotes { get; init; } = [];
    /// <summary>
    /// Validation error messages when the plan is invalid.
    /// </summary>
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];

    /// <summary>True when the plan carries no <see cref="ValidationErrors"/>. Computed, and excluded from JSON serialisation.</summary>
    [JsonIgnore]
    public bool IsValid => ValidationErrors.Count == 0;
}

/// <summary>
/// Planned OSPF area in the migration plan.
/// </summary>
internal sealed class OspfPlanArea
{
    /// <summary>
    /// Object name as reported by RouterOS.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// OSPF area-id value.
    /// </summary>
    public required string AreaId { get; init; }
    /// <summary>
    /// True when this planned area is the backbone area.
    /// </summary>
    public required bool IsBackbone { get; init; }
    /// <summary>Target ROS7 type: default for backbone, stub for others.</summary>
    public required string TargetType { get; init; }
    /// <summary>
    /// Area no-summaries flag when present.
    /// </summary>
    public bool NoSummaries { get; init; }
    /// <summary>
    /// Target RouterOS v7 default-cost. Null means the property must be cleared/unset on the device
    /// (never invent; never infer from inject-summary-lsas).
    /// </summary>
    public string? DefaultCost { get; init; }
    /// <summary>
    /// Original captured default-cost before target normalisation.
    /// </summary>
    public string? SourceDefaultCost { get; init; }
}

/// <summary>
/// Planned OSPF area-range in the migration plan.
/// </summary>
internal sealed class OspfPlanAreaRange
{
    /// <summary>
    /// Planned area name that owns this range.
    /// </summary>
    public required string AreaName { get; init; }
    /// <summary>
    /// Area-range CIDR.
    /// </summary>
    public required string RangeCidr { get; init; }
    /// <summary>
    /// RouterOS comment field.
    /// </summary>
    public string? Comment { get; init; }
    /// <summary>
    /// Whether the area-range is advertised.
    /// </summary>
    public bool? Advertise { get; init; }
}

/// <summary>
/// Planned active backhaul interface-template.
/// </summary>
internal sealed class OspfPlanBackhaulTemplate
{
    /// <summary>
    /// Area name for the backhaul template.
    /// </summary>
    public required string AreaName { get; init; }
    /// <summary>
    /// Interface name.
    /// </summary>
    public required string Interface { get; init; }
    /// <summary>
    /// Backhaul network CIDR (typically /30).
    /// </summary>
    public required string NetworkCidr { get; init; }
    /// <summary>
    /// Numeric OSPF cost to apply to the backhaul interface-template. Defaults to <c>10</c>, matching
    /// <see cref="OspfMigrationPlanner.FleetBackhaulCost"/>; ensure operations refuse any other value
    /// so the fleet backhaul metric cannot drift.
    /// </summary>
    public int Cost { get; init; } = 10;
    /// <summary>
    /// OSPF area or interface type string.
    /// </summary>
    public string Type { get; init; } = "ptp";
    /// <summary>
    /// Whether the template/interface is passive.
    /// </summary>
    public bool Passive { get; init; }
    /// <summary>
    /// Original captured cost before planning defaults.
    /// </summary>
    public string? SourceCost { get; init; }
}

/// <summary>
/// Planned passive loopback interface-template.
/// </summary>
internal sealed class OspfPlanLoopbackTemplate
{
    /// <summary>
    /// Area name for the loopback template.
    /// </summary>
    public required string AreaName { get; init; }
    /// <summary>
    /// Loopback network CIDR (typically /32).
    /// </summary>
    public required string NetworkCidr { get; init; }
    /// <summary>
    /// Interface name.
    /// </summary>
    public string? Interface { get; init; }
    /// <summary>
    /// Whether the template/interface is passive.
    /// </summary>
    public bool Passive { get; init; } = true;
}

/// <summary>
/// Planned passive site/customer network interface-template.
/// </summary>
internal sealed class OspfPlanSiteNetworkTemplate
{
    /// <summary>
    /// Area name for the site/customer template.
    /// </summary>
    public required string AreaName { get; init; }
    /// <summary>RouterOS <c>interfaces=</c> matcher. Fleet standard is <c>all</c>.</summary>
    public string Interfaces { get; init; } = OspfSiteMatcherConsolidation.AllInterfaces;
    /// <summary>
    /// One or more CIDRs for RouterOS <c>networks=</c> (comma-separated when consolidated).
    /// </summary>
    public required string NetworkCidr { get; init; }
    /// <summary>
    /// RouterOS comment field.
    /// </summary>
    public string? Comment { get; init; }
    /// <summary>
    /// Whether the template/interface is passive.
    /// </summary>
    public bool Passive { get; init; } = true;

    /// <summary>
    /// Optional captured semantic fields from the source template (pre-consolidation).
    /// Used only to build <see cref="OspfSupersededTemplateIdentity"/> provenance.
    /// </summary>
    public OspfCapturedTemplateSemantics? CapturedSemantics { get; init; }
}

/// <summary>Source template semantics retained long enough to emit cleanup provenance.</summary>
internal sealed class OspfCapturedTemplateSemantics
{
    /// <summary>
    /// Whether the template is disabled.
    /// </summary>
    public bool Disabled { get; init; }
    /// <summary>
    /// OSPF area or interface type string.
    /// </summary>
    public string? Type { get; init; }
    /// <summary>
    /// Raw <c>cost=</c> value of the source template as captured (text, not parsed), retained so the
    /// superseded-template identity can tell templates apart by cost. <see langword="null"/> when the
    /// source template had no explicit cost.
    /// </summary>
    public string? Cost { get; init; }
    /// <summary>
    /// Captured auth method when present.
    /// </summary>
    public string? Auth { get; init; }
    /// <summary>
    /// Authentication key id when present.
    /// </summary>
    public string? AuthId { get; init; }
    /// <summary>
    /// Fingerprint of captured auth key material; never plaintext.
    /// </summary>
    public string? AuthKeyFingerprint { get; init; }
    /// <summary>
    /// True when an auth key was present on the source template.
    /// </summary>
    public bool AuthKeyPresent { get; init; }
    /// <summary>
    /// True when conflicting auth-key aliases were present.
    /// </summary>
    public bool AuthKeyAmbiguous { get; init; }
    /// <summary>
    /// OSPF priority when present.
    /// </summary>
    public string? Priority { get; init; }
    /// <summary>Raw <c>use-bfd</c> value; null means absent (canonical false).</summary>
    public string? UseBfd { get; init; }
    /// <summary>
    /// Hello interval when present.
    /// </summary>
    public string? HelloInterval { get; init; }
    /// <summary>
    /// Dead interval when present.
    /// </summary>
    public string? DeadInterval { get; init; }
    /// <summary>
    /// Retransmit interval when present.
    /// </summary>
    public string? RetransmitInterval { get; init; }
    /// <summary>
    /// Transmit delay when present.
    /// </summary>
    public string? TransmitDelay { get; init; }
    /// <summary>
    /// Captured protocol instance-id when present.
    /// </summary>
    public string? InstanceId { get; init; }
    /// <summary>Resolved <c>/routing/ospf/area instance=</c> for the template's area at capture time.</summary>
    public string? AreaInstanceName { get; init; }
    /// <summary>
    /// Prefix-list reference when present.
    /// </summary>
    public string? PrefixList { get; init; }
    /// <summary>
    /// Virtual-link neighbour id when present.
    /// </summary>
    public string? VlinkNeighborId { get; init; }
    /// <summary>
    /// Virtual-link transit area when present.
    /// </summary>
    public string? VlinkTransitArea { get; init; }
    /// <summary>Exact operator comment; null means absent/empty on RouterOS.</summary>
    public string? Comment { get; init; }
}

/// <summary>
/// Migration-owned complete semantic identity recorded when the planner supersedes a source matcher.
/// Used as positive provenance for KnownLegacy cleanup. Does not include RouterOS <c>.id</c>.
/// </summary>
internal sealed class OspfSupersededTemplateIdentity
{
    /// <summary>
    /// Area name of the superseded source template.
    /// </summary>
    public required string AreaName { get; init; }
    /// <summary>Canonical source <c>interfaces</c> matcher (empty/null means network-only).</summary>
    public string? Interfaces { get; init; }
    /// <summary>Canonical source <c>networks</c> list before folding/consolidation.</summary>
    public required string NetworkCidr { get; init; }
    /// <summary>
    /// Whether the template/interface is passive.
    /// </summary>
    public bool Passive { get; init; } = true;
    /// <summary>
    /// Whether the template is disabled.
    /// </summary>
    public bool Disabled { get; init; }
    /// <summary>Normalized type token: <c>ptp</c>, <c>default</c>, or other explicit non-default.</summary>
    public string Type { get; init; } = OspfTemplateSemanticIdentity.DefaultTypeToken;
    /// <summary>Normalized cost token, or <see cref="OspfTemplateSemanticIdentity.DefaultCostToken"/>.</summary>
    public string Cost { get; init; } = OspfTemplateSemanticIdentity.DefaultCostToken;
    /// <summary>Normalized auth method token, or <see cref="OspfTemplateSemanticIdentity.DefaultAuthToken"/>.</summary>
    public string Auth { get; init; } = OspfTemplateSemanticIdentity.DefaultAuthToken;
    /// <summary>
    /// Authentication key id when present.
    /// </summary>
    public string? AuthId { get; init; }
    /// <summary>SHA-256 hex of auth-key when present; never the plaintext secret.</summary>
    public string? AuthKeyFingerprint { get; init; }
    /// <summary>
    /// True when the superseded template had auth key material.
    /// </summary>
    public bool AuthKeyPresent { get; init; }
    /// <summary>True when <c>auth-key</c> and <c>authentication-key</c> both present with unequal values.</summary>
    public bool AuthKeyAmbiguous { get; init; }
    /// <summary>
    /// OSPF priority when present.
    /// </summary>
    public string Priority { get; init; } = OspfTemplateSemanticIdentity.DefaultPriorityToken;
    /// <summary>Normalized use-bfd token: <c>false</c>, <c>true</c>, or <c>invalid:...</c>.</summary>
    public string UseBfd { get; init; } = OspfTemplateSemanticIdentity.DefaultUseBfdToken;
    /// <summary>
    /// Hello interval when present.
    /// </summary>
    public string HelloInterval { get; init; } = OspfTemplateSemanticIdentity.DefaultHelloToken;
    /// <summary>
    /// Dead interval when present.
    /// </summary>
    public string DeadInterval { get; init; } = OspfTemplateSemanticIdentity.DefaultDeadToken;
    /// <summary>
    /// Retransmit interval when present.
    /// </summary>
    public string RetransmitInterval { get; init; } = OspfTemplateSemanticIdentity.DefaultRetransmitToken;
    /// <summary>
    /// Transmit delay when present.
    /// </summary>
    public string TransmitDelay { get; init; } = OspfTemplateSemanticIdentity.DefaultTransmitDelayToken;
    /// <summary>Normalized protocol <c>instance-id</c> (default 0).</summary>
    public string InstanceId { get; init; } = OspfTemplateSemanticIdentity.DefaultInstanceIdToken;
    /// <summary>Area's OSPF routing-instance name at capture/inventory time (via area.instance).</summary>
    public string? AreaInstanceName { get; init; }
    /// <summary>
    /// Prefix-list reference when present.
    /// </summary>
    public string? PrefixList { get; init; }
    /// <summary>
    /// Virtual-link neighbour id when present.
    /// </summary>
    public string? VlinkNeighborId { get; init; }
    /// <summary>
    /// Virtual-link transit area when present.
    /// </summary>
    public string? VlinkTransitArea { get; init; }
    /// <summary>Exact comment text; null means absent/empty annotation.</summary>
    public string? Comment { get; init; }
}

/// <summary>
/// Planner output: plan plus success/validation errors.
/// </summary>
internal sealed class OspfPlanValidationResult
{
    /// <summary>The plan produced by the planner; inspect <see cref="OspfMigrationPlan.ValidationErrors"/> for failures.</summary>
    public required OspfMigrationPlan Plan { get; init; }
    /// <summary>True when the plan has no validation errors.</summary>
    public bool Succeeded => Plan.IsValid;
}

/// <summary>
/// Canonical content-hash payload. Property names/types must remain stable so
/// <see cref="OspfMigrationPlanner.ComputeContentHash"/> stays compatible with existing plans.
/// </summary>
internal sealed class OspfMigrationPlanHashPayload
{
    /// <summary>Import id of the scheduled job that owns the plan.</summary>
    public required Guid ImportId { get; init; }
    /// <summary>Scheduled job id.</summary>
    public required string JobId { get; init; }
    /// <summary>Router management IP as text.</summary>
    public required string RouterIp { get; init; }
    /// <summary>RouterOS identity at plan time.</summary>
    public required string Identity { get; init; }
    /// <summary>Logical/source OSPF instance name (ROS6 <c>default</c> or ROS7 <c>default-v2</c>).</summary>
    public required string InstanceName { get; init; }
    /// <summary>
    /// OSPF Router ID value.
    /// </summary>
    public required string RouterId { get; init; }
    /// <summary>Name of the OSPF instance the plan targets.</summary>
    public required string TargetInstanceName { get; init; }
    /// <summary>Name of the backbone area in the target configuration.</summary>
    public required string TargetBackboneAreaName { get; init; }
    /// <summary>Name of the site area in the target configuration.</summary>
    public required string TargetSiteAreaName { get; init; }
    /// <summary>Selected loopback interface name when known.</summary>
    public string? LoopbackInterfaceName { get; init; }
    /// <summary>Selected loopback CIDR used for Router ID derivation.</summary>
    public string? LoopbackCidr { get; init; }
    /// <summary>Distinct in-scope (<c>10.0.0.0/8</c>) loopback hosts observed during Router ID selection.</summary>
    public required IReadOnlyList<string> ObservedInScopeLoopbackAddresses { get; init; }
    /// <summary>Outside-range loopback hosts ignored for migration identity (configuration is preserved).</summary>
    public required IReadOnlyList<string> IgnoredOutsideRangeLoopbackAddresses { get; init; }
    /// <summary>Mandatory site management summary <c>/24</c> derived from interface addresses.</summary>
    public string? DerivedManagementCidr { get; init; }
    /// <summary>Planned OSPF areas.</summary>
    public required IReadOnlyList<OspfPlanArea> Areas { get; init; }
    /// <summary>Planned area ranges (summaries).</summary>
    public required IReadOnlyList<OspfPlanAreaRange> AreaRanges { get; init; }
    /// <summary>Planned active backhaul interface templates.</summary>
    public required IReadOnlyList<OspfPlanBackhaulTemplate> ActiveBackhauls { get; init; }
    /// <summary>Planned passive loopback templates.</summary>
    public required IReadOnlyList<OspfPlanLoopbackTemplate> PassiveLoopbacks { get; init; }
    /// <summary>Planned passive site-network templates.</summary>
    public required IReadOnlyList<OspfPlanSiteNetworkTemplate> PassiveSiteNetworks { get; init; }
    /// <summary>Semantic identities of the legacy templates the plan supersedes.</summary>
    public required IReadOnlyList<OspfSupersededTemplateIdentity> SupersededTemplates { get; init; }
    /// <summary>OSPFv3 objects slated for removal.</summary>
    public required IReadOnlyList<OspfV3ObjectRef> OspfV3ToRemove { get; init; }
}
