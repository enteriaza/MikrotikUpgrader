// -----------------------------------------------------------------------------
// File: OspfConsolidatedPrefixObsolescence.cs
//
// Description:
// Same-area consolidated-prefix obsolescence: a live template whose every
// network is either an exact member of, or a strictly more-specific subnet of,
// a planned consolidated site prefix is obsolete and may be removed. No
// provenance fingerprints or semantic attribute matching are required.
//
// -----------------------------------------------------------------------------

using System.Net;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Same-area consolidated-prefix obsolescence: a live template whose every network is either an
/// exact member of, or a strictly more-specific subnet of, a planned consolidated site prefix is
/// obsolete and may be removed. No provenance fingerprints or semantic attribute matching are
/// required.
/// </summary>
/// <remarks>
/// Exact-equal coverage is required for migration-owned consolidation sources whose prefixes are
/// retained unchanged on the planned <c>interfaces=all</c> matcher (for example a retained public
/// <c>/24</c> that did not fold into an aggregate or area-range). Strict containment continues to
/// cover subdivided management prefixes under a summarised <c>/24</c>.
/// </remarks>
internal static class OspfConsolidatedPrefixObsolescence
{
    /// <summary>
    /// True when <paramref name="row"/> belongs to the same area as a planned
    /// <c>interfaces=all</c> site matcher and every live network is either exactly equal to, or
    /// strictly contained by, one of that matcher's consolidated prefixes (IPv4 or IPv6).
    /// </summary>
    public static bool TryExplain(
        OspfTemplateInventoryRow row,
        OspfMigrationPlan plan,
        out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(row.Area) || string.IsNullOrWhiteSpace(row.Networks))
        {
            return false;
        }

        if (!TryParseNetworks(row.Networks, out List<IPNetwork> liveNets) || liveNets.Count == 0)
        {
            return false;
        }

        foreach (OspfPlanSiteNetworkTemplate planned in plan.PassiveSiteNetworks)
        {
            if (!string.Equals(row.Area, planned.AreaName, StringComparison.OrdinalIgnoreCase)
                || !planned.Passive
                || !OspfSiteMatcherConsolidation.IsAllInterfaces(planned.Interfaces))
            {
                continue;
            }

            if (!TryParseNetworks(planned.NetworkCidr, out List<IPNetwork> plannedNets)
                || plannedNets.Count == 0)
            {
                continue;
            }

            // Exact planned site matcher is protected elsewhere; never treat it as obsolete here.
            if (OspfSiteMatcherConsolidation.MatchesPlannedSite(
                    row.Area,
                    row.Interfaces,
                    row.Networks,
                    row.Passive,
                    row.Disabled,
                    planned))
            {
                continue;
            }

            if (!AllLiveNetworksCoveredByConsolidated(liveNets, plannedNets, out List<string> folds))
            {
                continue;
            }

            reason =
                $"Consolidated-prefix replacement: area={row.Area} interfaces={row.Interfaces ?? "<none>"} "
                + $"networks={row.Networks} is obsolete under planned interfaces=all "
                + $"networks={planned.NetworkCidr} ({string.Join("; ", folds)}).";
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="specific"/> is a strictly longer prefix fully contained by
    /// <paramref name="covering"/> (same address family).
    /// </summary>
    public static bool IsStrictlyContainedBy(IPNetwork specific, IPNetwork covering)
    {
        if (specific.BaseAddress.AddressFamily != covering.BaseAddress.AddressFamily)
        {
            return false;
        }

        if (specific.PrefixLength <= covering.PrefixLength)
        {
            return false;
        }

        // IPNetwork normalises bases; a longer prefix whose base lies in the covering network is
        // entirely contained by that covering CIDR.
        return covering.Contains(specific.BaseAddress);
    }

    /// <summary>
    /// True when <paramref name="live"/> is exactly equal to <paramref name="planned"/> (same
    /// family, base and prefix length).
    /// </summary>
    public static bool IsExactMemberOf(IPNetwork live, IPNetwork planned) =>
        live.BaseAddress.AddressFamily == planned.BaseAddress.AddressFamily
        && live.PrefixLength == planned.PrefixLength
        && live.Equals(planned);

    /// <summary>
    /// True when <paramref name="live"/> is either an exact member of <paramref name="planned"/>
    /// or strictly contained by it.
    /// </summary>
    public static bool IsCoveredByConsolidated(IPNetwork live, IPNetwork planned) =>
        IsExactMemberOf(live, planned) || IsStrictlyContainedBy(live, planned);

    /// <summary>
    /// Parses a comma-separated list of networks.
    /// </summary>
    /// <param name="networks">Comma-separated CIDR list.</param>
    /// <param name="parsed">Receives the parsed networks; empty on failure.</param>
    /// <returns><see langword="true"/> when the list is non-empty and every entry is valid.</returns>
    public static bool TryParseNetworks(string? networks, out List<IPNetwork> parsed)
    {
        parsed = [];
        if (string.IsNullOrWhiteSpace(networks))
        {
            return false;
        }

        foreach (string part in networks.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPNetwork.TryParse(part, out IPNetwork network))
            {
                parsed = [];
                return false;
            }

            parsed.Add(network);
        }

        return parsed.Count > 0;
    }

    /// <summary>
    /// Determines whether every live network is an exact member of, or strictly contained in, some
    /// planned consolidated network.
    /// </summary>
    /// <param name="liveNets">Networks currently on the router.</param>
    /// <param name="plannedNets">Networks in the plan.</param>
    /// <param name="folds">Receives a description of each live network and its covering planned network; empty on failure.</param>
    /// <returns><see langword="true"/> when all live networks are covered.</returns>
    private static bool AllLiveNetworksCoveredByConsolidated(
        IReadOnlyList<IPNetwork> liveNets,
        IReadOnlyList<IPNetwork> plannedNets,
        out List<string> folds)
    {
        folds = [];
        foreach (IPNetwork live in liveNets)
        {
            IPNetwork? cover = null;
            bool exact = false;
            foreach (IPNetwork planned in plannedNets)
            {
                if (IsExactMemberOf(live, planned))
                {
                    cover = planned;
                    exact = true;
                    break;
                }

                if (IsStrictlyContainedBy(live, planned))
                {
                    cover = planned;
                    exact = false;
                    break;
                }
            }

            if (cover is null)
            {
                folds = [];
                return false;
            }

            folds.Add(
                exact
                    ? $"{Format(live)} = {Format(cover.Value)} (exact consolidated member)"
                    : $"{Format(live)} ⊂ {Format(cover.Value)}");
        }

        return folds.Count > 0;
    }

    /// <summary>
    /// Formats a network as base address and prefix length.
    /// </summary>
    /// <param name="network">Network to format.</param>
    /// <returns>The CIDR text.</returns>
    private static string Format(IPNetwork network) =>
        $"{network.BaseAddress}/{network.PrefixLength}";
}
