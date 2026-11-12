using System.Net;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Simple same-area consolidated-prefix obsolescence: a live template whose every network is a
/// strictly more-specific subnet of a planned consolidated site prefix is obsolete and may be
/// removed. No provenance fingerprints or semantic attribute matching are required.
/// </summary>
internal static class OspfConsolidatedPrefixObsolescence
{
    /// <summary>
    /// True when <paramref name="row"/> belongs to the same area as a planned
    /// <c>interfaces=all</c> site matcher and every live network is strictly contained by one of
    /// that matcher's consolidated prefixes (IPv4 or IPv6).
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

            if (!AllLiveNetworksStrictlyContained(liveNets, plannedNets, out List<string> folds))
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

    private static bool AllLiveNetworksStrictlyContained(
        IReadOnlyList<IPNetwork> liveNets,
        IReadOnlyList<IPNetwork> plannedNets,
        out List<string> folds)
    {
        folds = [];
        foreach (IPNetwork live in liveNets)
        {
            IPNetwork? cover = null;
            foreach (IPNetwork planned in plannedNets)
            {
                if (IsStrictlyContainedBy(live, planned))
                {
                    cover = planned;
                    break;
                }
            }

            if (cover is null)
            {
                folds = [];
                return false;
            }

            folds.Add($"{Format(live)} ⊂ {Format(cover.Value)}");
        }

        return folds.Count > 0;
    }

    private static string Format(IPNetwork network) =>
        $"{network.BaseAddress}/{network.PrefixLength}";
}
