// -----------------------------------------------------------------------------
// File: OspfSummaryRedistributionOptions.cs
//
// Description:
// Root-key option RedistributeSummaryviaStatics controlling the optional post-
// OSPF stage that advertises area-range prefixes via redistributed static
// blackhole routes. Bound independently of Mikrotik:Enabled and
// FirmwareUpgrade:Enabled; defaults to false (stage skipped).
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Configuration;

/// <summary>
/// Optional post-OSPF-migration stage that advertises area-range summary prefixes via
/// redistributed static blackhole routes and a tightly scoped outbound filter.
/// </summary>
/// <remarks>
/// Bound from the root configuration key <c>RedistributeSummaryviaStatics</c>.
/// Independent of <c>FirmwareUpgrade:Enabled</c> and <c>MikroTik:Enabled</c>.
/// When false (default), the stage is skipped and performs no RouterOS mutations.
/// </remarks>
public sealed class OspfSummaryRedistributionOptions
{
    /// <summary>
    /// When <c>true</c>, after successful OSPF migration verification and before RouterBOARD
    /// firmware upgrade, install distance-250 blackhole statics for each area-range prefix,
    /// reconcile chain <c>ospf-v2-out</c>, then enable static redistribution on the live OSPFv2
    /// instance. Defaults to <c>false</c> when omitted.
    /// </summary>
    public bool RedistributeSummaryviaStatics { get; set; }
}
