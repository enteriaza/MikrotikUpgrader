using System.Net;
using System.Text.Json.Serialization;

namespace MikrotikWorker.Upgrade.Ospf;

internal enum OspfCaptureSourceKind
{
    RouterOsV6 = 0,
    RouterOsV7 = 1,
}

internal sealed class OspfCaptureSnapshot
{
    public required Guid ImportId { get; init; }
    public required string JobId { get; init; }
    public required IPAddress RouterIp { get; init; }
    public required string Identity { get; init; }
    public required string RouterOsVersion { get; init; }
    public required string? Architecture { get; init; }
    public required OspfCaptureSourceKind SourceKind { get; init; }

    public IReadOnlyList<OspfInstanceRow> Instances { get; init; } = [];
    public IReadOnlyList<OspfAreaRow> Areas { get; init; } = [];
    public IReadOnlyList<OspfAreaRangeRow> AreaRanges { get; init; } = [];
    public IReadOnlyList<OspfNetworkRow> Networks { get; init; } = [];
    public IReadOnlyList<OspfInterfaceRow> Interfaces { get; init; } = [];
    public IReadOnlyList<OspfInterfaceTemplateRow> InterfaceTemplates { get; init; } = [];
    public IReadOnlyList<OspfIpAddressRow> IpAddresses { get; init; } = [];
    public IReadOnlyList<OspfRoutingFilterRow> RoutingFilters { get; init; } = [];
    public IReadOnlyList<OspfV3ObjectRef> OspfV3Objects { get; init; } = [];
}

internal sealed class OspfInstanceRow
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? RouterId { get; init; }
    public string? Version { get; init; }
    public bool IsDefault { get; init; }
    public Dictionary<string, string?> RedistributeProperties { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Live <c>in-filter</c> (null/empty means unset).</summary>
    public string? InFilter { get; init; }

    /// <summary>Live <c>out-filter</c> (null/empty means unset).</summary>
    public string? OutFilter { get; init; }
}

internal sealed class OspfAreaRow
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? AreaId { get; init; }
    public string? Instance { get; init; }
    public string? Type { get; init; }
    public string? DefaultCost { get; init; }
    public bool? NoSummaries { get; init; }
    public string? InjectSummaryLsas { get; init; }
}

internal sealed class OspfAreaRangeRow
{
    public string? Id { get; init; }
    public string? Area { get; init; }
    public string? Range { get; init; }
    public string? Comment { get; init; }
    public bool? Advertise { get; init; }
}

internal sealed class OspfNetworkRow
{
    public string? Id { get; init; }
    public string? Area { get; init; }
    public string? Network { get; init; }
    public string? Comment { get; init; }
}

internal sealed class OspfInterfaceRow
{
    public string? Id { get; init; }
    public string? Interface { get; init; }
    public string? NetworkType { get; init; }
    public string? Cost { get; init; }
    public bool Passive { get; init; }
}

internal sealed class OspfInterfaceTemplateRow
{
    public string? Id { get; init; }
    public string? Area { get; init; }
    public string? Interfaces { get; init; }
    public string? Networks { get; init; }
    public string? Type { get; init; }
    public string? Cost { get; init; }
    public bool Passive { get; init; }
    public bool Disabled { get; init; }
    public string? Comment { get; init; }
    /// <summary>RouterOS <c>auth</c> method (simple/md5/sha*); unset means authentication unused.</summary>
    public string? Auth { get; init; }
    public string? AuthId { get; init; }
    /// <summary>Raw <c>auth-key</c> from capture only; never logged. Prefer fingerprint on provenance.</summary>
    public string? AuthKey { get; init; }
    /// <summary>Raw <c>authentication-key</c> alias from capture only; never logged. Prefer fingerprint on provenance.</summary>
    public string? AuthenticationKey { get; init; }
    public string? Priority { get; init; }
    /// <summary>Raw <c>use-bfd</c> API value when present; null means absent.</summary>
    public string? UseBfd { get; init; }
    public string? HelloInterval { get; init; }
    public string? DeadInterval { get; init; }
    public string? RetransmitInterval { get; init; }
    public string? TransmitDelay { get; init; }
    /// <summary>OSPF protocol multi-instance id on the template (not /routing/ospf/instance name).</summary>
    public string? InstanceId { get; init; }
    public string? PrefixList { get; init; }
    public string? VlinkNeighborId { get; init; }
    public string? VlinkTransitArea { get; init; }
}

internal sealed class OspfIpAddressRow
{
    public string? Id { get; init; }
    public string? Address { get; init; }
    public string? Network { get; init; }
    public string? Interface { get; init; }
}

internal sealed class OspfRoutingFilterRow
{
    public string? Id { get; init; }
    public string? Name { get; init; }
}

/// <summary>Captured OSPFv3 object reference (v6 /routing ospf-v3 or v7 version=3 dependents).</summary>
internal sealed class OspfV3ObjectRef
{
    public required string Path { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Kind { get; init; }
}

internal sealed class OspfMigrationPlan
{
    public required Guid ImportId { get; init; }
    public required string JobId { get; init; }
    public required string RouterIp { get; init; }
    public required string Identity { get; init; }
    public required string SourceRouterOsVersion { get; init; }
    public required string? Architecture { get; init; }
    public required OspfCaptureSourceKind SourceKind { get; init; }
    public string ContentHash { get; set; } = string.Empty;

    public required string InstanceName { get; init; }
    public required string RouterId { get; init; }

    public IReadOnlyList<OspfPlanArea> Areas { get; init; } = [];
    public IReadOnlyList<OspfPlanAreaRange> AreaRanges { get; init; } = [];
    public IReadOnlyList<OspfPlanBackhaulTemplate> ActiveBackhauls { get; init; } = [];
    public IReadOnlyList<OspfPlanLoopbackTemplate> PassiveLoopbacks { get; init; } = [];
    public IReadOnlyList<OspfPlanSiteNetworkTemplate> PassiveSiteNetworks { get; init; } = [];

    /// <summary>
    /// Source matchers superseded by planning (e.g. consolidation). Empty means cleanup cannot
    /// positively identify KnownLegacy rows and must treat extras as Unknown.
    /// </summary>
    public IReadOnlyList<OspfSupersededTemplateIdentity> SupersededTemplates { get; init; } = [];

    public bool RemoveAllRoutingFilters { get; init; } = true;
    public bool ClearAllRedistribution { get; init; } = true;
    public bool RemoveAllOspfV3 { get; init; } = true;
    public IReadOnlyList<OspfV3ObjectRef> OspfV3ToRemove { get; init; } = [];

    public IReadOnlyList<string> NormalisationNotes { get; init; } = [];
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];

    [JsonIgnore]
    public bool IsValid => ValidationErrors.Count == 0;
}

internal sealed class OspfPlanArea
{
    public required string Name { get; init; }
    public required string AreaId { get; init; }
    public required bool IsBackbone { get; init; }
    /// <summary>Target ROS7 type: default for backbone, stub for others.</summary>
    public required string TargetType { get; init; }
    public bool NoSummaries { get; init; }
    /// <summary>
    /// Target RouterOS v7 default-cost. Null means the property must be cleared/unset on the device
    /// (never invent; never infer from inject-summary-lsas).
    /// </summary>
    public string? DefaultCost { get; init; }
    public string? SourceDefaultCost { get; init; }
}

internal sealed class OspfPlanAreaRange
{
    public required string AreaName { get; init; }
    public required string RangeCidr { get; init; }
    public string? Comment { get; init; }
    public bool? Advertise { get; init; }
}

internal sealed class OspfPlanBackhaulTemplate
{
    public required string AreaName { get; init; }
    public required string Interface { get; init; }
    public required string NetworkCidr { get; init; }
    public int Cost { get; init; } = 10;
    public string Type { get; init; } = "ptp";
    public bool Passive { get; init; }
    public string? SourceCost { get; init; }
}

internal sealed class OspfPlanLoopbackTemplate
{
    public required string AreaName { get; init; }
    public required string NetworkCidr { get; init; }
    public string? Interface { get; init; }
    public bool Passive { get; init; } = true;
}

internal sealed class OspfPlanSiteNetworkTemplate
{
    public required string AreaName { get; init; }
    /// <summary>RouterOS <c>interfaces=</c> matcher. Fleet standard is <c>all</c>.</summary>
    public string Interfaces { get; init; } = OspfSiteMatcherConsolidation.AllInterfaces;
    /// <summary>
    /// One or more CIDRs for RouterOS <c>networks=</c> (comma-separated when consolidated).
    /// </summary>
    public required string NetworkCidr { get; init; }
    public string? Comment { get; init; }
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
    public bool Disabled { get; init; }
    public string? Type { get; init; }
    public string? Cost { get; init; }
    public string? Auth { get; init; }
    public string? AuthId { get; init; }
    public string? AuthKeyFingerprint { get; init; }
    public bool AuthKeyPresent { get; init; }
    public bool AuthKeyAmbiguous { get; init; }
    public string? Priority { get; init; }
    /// <summary>Raw <c>use-bfd</c> value; null means absent (canonical false).</summary>
    public string? UseBfd { get; init; }
    public string? HelloInterval { get; init; }
    public string? DeadInterval { get; init; }
    public string? RetransmitInterval { get; init; }
    public string? TransmitDelay { get; init; }
    public string? InstanceId { get; init; }
    /// <summary>Resolved <c>/routing/ospf/area instance=</c> for the template's area at capture time.</summary>
    public string? AreaInstanceName { get; init; }
    public string? PrefixList { get; init; }
    public string? VlinkNeighborId { get; init; }
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
    public required string AreaName { get; init; }
    /// <summary>Canonical source <c>interfaces</c> matcher (empty/null means network-only).</summary>
    public string? Interfaces { get; init; }
    /// <summary>Canonical source <c>networks</c> list before folding/consolidation.</summary>
    public required string NetworkCidr { get; init; }
    public bool Passive { get; init; } = true;
    public bool Disabled { get; init; }
    /// <summary>Normalized type token: <c>ptp</c>, <c>default</c>, or other explicit non-default.</summary>
    public string Type { get; init; } = OspfTemplateSemanticIdentity.DefaultTypeToken;
    /// <summary>Normalized cost token, or <see cref="OspfTemplateSemanticIdentity.DefaultCostToken"/>.</summary>
    public string Cost { get; init; } = OspfTemplateSemanticIdentity.DefaultCostToken;
    /// <summary>Normalized auth method token, or <see cref="OspfTemplateSemanticIdentity.DefaultAuthToken"/>.</summary>
    public string Auth { get; init; } = OspfTemplateSemanticIdentity.DefaultAuthToken;
    public string? AuthId { get; init; }
    /// <summary>SHA-256 hex of auth-key when present; never the plaintext secret.</summary>
    public string? AuthKeyFingerprint { get; init; }
    public bool AuthKeyPresent { get; init; }
    /// <summary>True when <c>auth-key</c> and <c>authentication-key</c> both present with unequal values.</summary>
    public bool AuthKeyAmbiguous { get; init; }
    public string Priority { get; init; } = OspfTemplateSemanticIdentity.DefaultPriorityToken;
    /// <summary>Normalized use-bfd token: <c>false</c>, <c>true</c>, or <c>invalid:...</c>.</summary>
    public string UseBfd { get; init; } = OspfTemplateSemanticIdentity.DefaultUseBfdToken;
    public string HelloInterval { get; init; } = OspfTemplateSemanticIdentity.DefaultHelloToken;
    public string DeadInterval { get; init; } = OspfTemplateSemanticIdentity.DefaultDeadToken;
    public string RetransmitInterval { get; init; } = OspfTemplateSemanticIdentity.DefaultRetransmitToken;
    public string TransmitDelay { get; init; } = OspfTemplateSemanticIdentity.DefaultTransmitDelayToken;
    /// <summary>Normalized protocol <c>instance-id</c> (default 0).</summary>
    public string InstanceId { get; init; } = OspfTemplateSemanticIdentity.DefaultInstanceIdToken;
    /// <summary>Area's OSPF routing-instance name at capture/inventory time (via area.instance).</summary>
    public string? AreaInstanceName { get; init; }
    public string? PrefixList { get; init; }
    public string? VlinkNeighborId { get; init; }
    public string? VlinkTransitArea { get; init; }
    /// <summary>Exact comment text; null means absent/empty annotation.</summary>
    public string? Comment { get; init; }
}

internal sealed class OspfPlanValidationResult
{
    public required OspfMigrationPlan Plan { get; init; }
    public bool Succeeded => Plan.IsValid;
}
