// -----------------------------------------------------------------------------
// File: OspfNetworkList.cs
//
// Description:
// Helpers for RouterOS multi-value networks= lists on OSPF
// interface-templates.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Helpers for RouterOS multi-value <c>networks=</c> lists on OSPF interface-templates.
/// </summary>
internal static class OspfNetworkList
{
    /// <summary>
    /// Splits a RouterOS multi-value networks= list into tokens.
    /// </summary>
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

    /// <summary>
    /// Joins network tokens into a canonical comma-separated networks= value.
    /// </summary>
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

    /// <summary>
    /// Compares two network lists as sets, ignoring order and duplicates.
    /// </summary>
    /// <param name="left">First list.</param>
    /// <param name="right">Second list.</param>
    /// <returns><see langword="true"/> when both contain the same entries.</returns>
    public static bool SetsEqual(string? left, string? right)
    {
        HashSet<string> a = Split(left).ToHashSet(StringComparer.Ordinal);
        HashSet<string> b = Split(right).ToHashSet(StringComparer.Ordinal);
        return a.SetEquals(b);
    }

    /// <summary>
    /// Parses every entry of a network list.
    /// </summary>
    /// <param name="networks">Network list text.</param>
    /// <param name="prefixes">Receives the entries that parsed.</param>
    /// <param name="invalid">Receives the entries that did not parse.</param>
    /// <returns><see langword="true"/> when no entry is invalid.</returns>
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
