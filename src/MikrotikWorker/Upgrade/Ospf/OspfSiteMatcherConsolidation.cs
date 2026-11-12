namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Consolidates site/customer OSPF matchers into one passive <c>interfaces=all</c> template per
/// site area, folding legacy per-VLAN / per-network statements into fleet-approved aggregates.
/// </summary>
internal static class OspfSiteMatcherConsolidation
{
    public const string AllInterfaces = "all";

    /// <summary>Shared CGNAT / 100.64/10 aggregate used for PPPoE and ACS matchers.</summary>
    public static readonly OspfIpPrefix SharedAddressSpace = ParseRequired("100.64.0.0/10");

    /// <summary>RFC1918 172.16/12 aggregate used for inactive PPPoE-style matchers.</summary>
    public static readonly OspfIpPrefix Private172Aggregate = ParseRequired("172.16.0.0/12");

    public static List<OspfPlanSiteNetworkTemplate> Consolidate(
        IReadOnlyList<OspfPlanSiteNetworkTemplate> rawSites,
        IReadOnlyList<OspfPlanAreaRange> areaRanges,
        List<string> notes) =>
        ConsolidateWithProvenance(rawSites, areaRanges, notes).Consolidated.ToList();

    public static OspfSiteConsolidationResult ConsolidateWithProvenance(
        IReadOnlyList<OspfPlanSiteNetworkTemplate> rawSites,
        IReadOnlyList<OspfPlanAreaRange> areaRanges,
        List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(rawSites);
        ArgumentNullException.ThrowIfNull(areaRanges);
        ArgumentNullException.ThrowIfNull(notes);

        List<OspfPlanSiteNetworkTemplate> consolidated = [];
        List<OspfSupersededTemplateIdentity> superseded = [];
        foreach (IGrouping<string, OspfPlanSiteNetworkTemplate> group in rawSites
                     .GroupBy(static s => s.AreaName, StringComparer.OrdinalIgnoreCase))
        {
            HashSet<string> folded = new(StringComparer.Ordinal);
            int sourceMatcherCount = 0;
            string? comment = null;
            List<OspfPlanSiteNetworkTemplate> retainedSources = [];

            foreach (OspfPlanSiteNetworkTemplate site in group)
            {
                if (!site.Passive)
                {
                    notes.Add(
                        $"Site matcher area='{site.AreaName}' networks='{site.NetworkCidr}' forced Passive=true during consolidation.");
                }

                IReadOnlyList<string> parts = OspfNetworkList.Split(site.NetworkCidr);
                if (parts.Count == 0)
                {
                    continue;
                }

                retainedSources.Add(site);
                sourceMatcherCount += parts.Count;
                comment ??= site.Comment;

                foreach (string part in parts)
                {
                    folded.Add(FoldPrefix(part, group.Key, areaRanges, notes));
                }
            }

            if (folded.Count == 0)
            {
                continue;
            }

            string networks = OspfNetworkList.JoinCanonical(folded);
            if (sourceMatcherCount > 1
                || group.Any(s => !IsAllInterfaces(s.Interfaces))
                || group.Count() > 1)
            {
                notes.Add(
                    $"Consolidated {sourceMatcherCount} site matcher(s) in area '{group.Key}' into interfaces=all networks={networks}.");
            }

            OspfPlanSiteNetworkTemplate target = new()
            {
                AreaName = group.Key,
                Interfaces = AllInterfaces,
                NetworkCidr = networks,
                Comment = comment,
                Passive = true,
            };
            consolidated.Add(target);

            foreach (OspfPlanSiteNetworkTemplate source in retainedSources)
            {
                if (MatchesPlannedSite(
                        source.AreaName,
                        source.Interfaces,
                        source.NetworkCidr,
                        passive: true,
                        disabled: false,
                        target))
                {
                    continue;
                }

                superseded.Add(
                    OspfTemplateSemanticIdentity.FromCaptured(
                        source.AreaName,
                        source.Interfaces,
                        source.NetworkCidr,
                        passive: true,
                        source.CapturedSemantics));
            }
        }

        return new OspfSiteConsolidationResult(consolidated, superseded);
    }

    public static bool IsAllInterfaces(string? interfaces) =>
        string.Equals(interfaces?.Trim(), AllInterfaces, StringComparison.OrdinalIgnoreCase);

    public static bool InterfacesEqual(string? left, string? right) =>
        string.Equals(
            OspfTemplateSemanticIdentity.CanonicalInterfaces(left),
            OspfTemplateSemanticIdentity.CanonicalInterfaces(right),
            StringComparison.Ordinal);

    /// <summary>Matches any planned passive site/LAN template identity (bound or interfaces=all).</summary>
    public static bool MatchesPlannedSite(
        string? area,
        string? interfaces,
        string? networks,
        bool passive,
        bool disabled,
        OspfPlanSiteNetworkTemplate planned) =>
        !disabled
        && passive
        && planned.Passive
        && string.Equals(area, planned.AreaName, StringComparison.OrdinalIgnoreCase)
        && InterfacesEqual(interfaces, planned.Interfaces)
        && OspfNetworkList.SetsEqual(networks, planned.NetworkCidr);

    public static bool MatchesConsolidatedSite(
        string? area,
        string? interfaces,
        string? networks,
        bool passive,
        bool disabled,
        OspfPlanSiteNetworkTemplate planned) =>
        MatchesPlannedSite(area, interfaces, networks, passive, disabled, planned)
        && IsAllInterfaces(planned.Interfaces);

    public static bool MatchesSupersededIdentity(
        OspfTemplateInventoryRow row,
        OspfSupersededTemplateIdentity identity) =>
        OspfTemplateSemanticIdentity.Matches(row, identity);

    /// <summary>
    /// Folds a site-matcher prefix into the deterministic consolidation target.
    /// </summary>
    /// <remarks>
    /// Precedence:
    /// <list type="number">
    /// <item>Fleet aggregates (<c>100.64.0.0/10</c>, <c>172.16.0.0/12</c>) win first.</item>
    /// <item>Among same-area area-ranges that contain the prefix, choose the most specific
    /// (longest-prefix) match so management <c>/24</c> boundaries beat broader aggregates
    /// regardless of input ordering.</item>
    /// <item>Equal-length candidates: prefer the explicit MGMT summarisation boundary
    /// (<see cref="OspfFleetNaming.ManagementRangeComment"/>).</item>
    /// <item>Remaining ambiguity fails closed rather than silently picking by list order.</item>
    /// </list>
    /// </remarks>
    internal static string FoldPrefix(
        string cidr,
        string areaName,
        IReadOnlyList<OspfPlanAreaRange> areaRanges,
        List<string> notes)
    {
        if (!OspfIpPrefix.TryParse(cidr, out OspfIpPrefix prefix))
        {
            return cidr.Trim();
        }

        if (SharedAddressSpace.Contains(prefix) || prefix.Equals(SharedAddressSpace))
        {
            if (!prefix.Equals(SharedAddressSpace))
            {
                notes.Add(
                    $"Folding site matcher '{prefix.ToCidrString()}' into fleet aggregate '{SharedAddressSpace.ToCidrString()}'.");
            }

            return SharedAddressSpace.ToCidrString();
        }

        if (Private172Aggregate.Contains(prefix) || prefix.Equals(Private172Aggregate))
        {
            if (!prefix.Equals(Private172Aggregate))
            {
                notes.Add(
                    $"Folding site matcher '{prefix.ToCidrString()}' into fleet aggregate '{Private172Aggregate.ToCidrString()}'.");
            }

            return Private172Aggregate.ToCidrString();
        }

        List<(OspfPlanAreaRange Range, OspfIpPrefix Prefix)> eligible = [];
        foreach (OspfPlanAreaRange range in areaRanges.Where(r =>
                     string.Equals(r.AreaName, areaName, StringComparison.OrdinalIgnoreCase)))
        {
            if (!OspfIpPrefix.TryParse(range.RangeCidr, out OspfIpPrefix rangePrefix))
            {
                continue;
            }

            if (rangePrefix.Contains(prefix) || prefix.Equals(rangePrefix))
            {
                eligible.Add((range, rangePrefix));
            }
        }

        if (eligible.Count == 0)
        {
            return prefix.ToCidrString();
        }

        OspfIpPrefix selected = SelectFoldAreaRange(prefix, eligible, notes);
        if (!prefix.Equals(selected))
        {
            notes.Add(
                $"Folding site matcher '{prefix.ToCidrString()}' into area-range '{selected.ToCidrString()}'.");
        }

        return selected.ToCidrString();
    }

    /// <summary>
    /// Selects the deterministic area-range fold target from eligible containing ranges.
    /// </summary>
    internal static OspfIpPrefix SelectFoldAreaRange(
        OspfIpPrefix prefix,
        IReadOnlyList<(OspfPlanAreaRange Range, OspfIpPrefix Prefix)> eligible,
        List<string>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        if (eligible.Count == 0)
        {
            throw new ArgumentException("At least one eligible area-range is required.", nameof(eligible));
        }

        int longest = eligible.Max(static e => e.Prefix.PrefixLength);
        List<(OspfPlanAreaRange Range, OspfIpPrefix Prefix)> longestMatches = eligible
            .Where(e => e.Prefix.PrefixLength == longest)
            .ToList();

        if (longestMatches.Count == 1)
        {
            return longestMatches[0].Prefix;
        }

        List<(OspfPlanAreaRange Range, OspfIpPrefix Prefix)> mgmtMatches = longestMatches
            .Where(static e =>
                string.Equals(
                    e.Range.Comment,
                    OspfFleetNaming.ManagementRangeComment,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mgmtMatches.Count == 1)
        {
            notes?.Add(
                $"Area-range fold for '{prefix.ToCidrString()}' selected MGMT boundary "
                + $"'{mgmtMatches[0].Prefix.ToCidrString()}' among {longestMatches.Count} equal-length candidates.");
            return mgmtMatches[0].Prefix;
        }

        // Identical CIDR duplicates with a single distinct prefix are still unambiguous once
        // comments/advertise flags are ignored for the fold target itself.
        HashSet<string> distinctCidrs = longestMatches
            .Select(static e => e.Prefix.ToCidrString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (distinctCidrs.Count == 1 && mgmtMatches.Count == 0)
        {
            return longestMatches[0].Prefix;
        }

        string detail = string.Join(
            ", ",
            longestMatches.Select(static e =>
                $"{e.Prefix.ToCidrString()}(comment={e.Range.Comment ?? "<none>"})"));
        throw new InvalidOperationException(
            $"Ambiguous OSPF area-range hierarchy while folding '{prefix.ToCidrString()}': "
            + $"multiple equal-length candidates without a unique MGMT boundary [{detail}]. "
            + "Refusing silent first-match selection.");
    }

    private static OspfIpPrefix ParseRequired(string cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix prefix)
            ? prefix
            : throw new InvalidOperationException($"Invalid fleet aggregate prefix '{cidr}'.");
}

internal sealed class OspfSiteConsolidationResult(
    IReadOnlyList<OspfPlanSiteNetworkTemplate> consolidated,
    IReadOnlyList<OspfSupersededTemplateIdentity> superseded)
{
    public IReadOnlyList<OspfPlanSiteNetworkTemplate> Consolidated { get; } = consolidated;
    public IReadOnlyList<OspfSupersededTemplateIdentity> Superseded { get; } = superseded;
}
