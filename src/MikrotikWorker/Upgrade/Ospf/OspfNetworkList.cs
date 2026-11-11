namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Helpers for RouterOS multi-value <c>networks=</c> lists on OSPF interface-templates.
/// </summary>
internal static class OspfNetworkList
{
    public static IReadOnlyList<string> Split(string? networks)
    {
        if (string.IsNullOrWhiteSpace(networks))
        {
            return [];
        }

        List<string> result = [];
        foreach (string part in networks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (OspfIpPrefix.TryParse(part, out OspfIpPrefix prefix))
            {
                result.Add(prefix.ToCidrString());
            }
            else if (!string.IsNullOrWhiteSpace(part))
            {
                result.Add(part.Trim());
            }
        }

        return result;
    }

    public static string JoinCanonical(IEnumerable<string> networks)
    {
        List<string> normalized = [];
        foreach (string network in networks)
        {
            if (OspfIpPrefix.TryParse(network, out OspfIpPrefix prefix))
            {
                normalized.Add(prefix.ToCidrString());
            }
            else if (!string.IsNullOrWhiteSpace(network))
            {
                normalized.Add(network.Trim());
            }
        }

        return string.Join(
            ",",
            normalized
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static n => n, StringComparer.Ordinal));
    }

    public static bool SetsEqual(string? left, string? right)
    {
        HashSet<string> a = Split(left).ToHashSet(StringComparer.Ordinal);
        HashSet<string> b = Split(right).ToHashSet(StringComparer.Ordinal);
        return a.SetEquals(b);
    }

    public static bool TryParseAll(string? networks, out List<OspfIpPrefix> prefixes, out List<string> invalid)
    {
        prefixes = [];
        invalid = [];
        foreach (string part in Split(networks))
        {
            if (OspfIpPrefix.TryParse(part, out OspfIpPrefix prefix))
            {
                prefixes.Add(prefix);
            }
            else
            {
                invalid.Add(part);
            }
        }

        return invalid.Count == 0 && prefixes.Count > 0;
    }
}
