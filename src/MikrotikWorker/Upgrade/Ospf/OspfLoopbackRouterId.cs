using System.Net;
using System.Net.Sockets;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Authoritative OSPF Router ID derivation from the in-scope Loopback IPv4 <c>/32</c>
/// inside <c>10.0.0.0/8</c>.
/// </summary>
/// <remarks>
/// Supported Loopback interface names from fleet fixtures (exact, case-insensitive):
/// <c>lo</c>, <c>Loopback</c>, <c>Loopback-OSPF</c>. Identification uses <c>/ip/address</c>
/// interface names — never backhaul, management, or PPPoE addresses.
/// RouterOS export may omit <c>/32</c> for host addresses; those are normalised to <c>/32</c>.
/// Multiple Loopback addresses are allowed: outside-range addresses are ignored for migration
/// identity; only unambiguous in-scope (<c>10.0.0.0/8</c>) identity is selected.
/// </remarks>
internal static class OspfLoopbackRouterId
{
    public const string InterfaceNameLo = "lo";
    public const string InterfaceNameLoopback = "Loopback";
    public const string InterfaceNameLoopbackOspf = "Loopback-OSPF";
    public const string InfrastructureScopeCidr = "10.0.0.0/8";

    private static readonly OspfIpPrefix InfrastructureScope = ParseRequired(InfrastructureScopeCidr);

    private static readonly string[] SupportedNames =
    [
        InterfaceNameLo,
        InterfaceNameLoopback,
        InterfaceNameLoopbackOspf,
    ];

    public static bool IsSupportedLoopbackInterfaceName(string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return false;
        }

        string name = interfaceName.Trim();
        return SupportedNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsInfrastructureScopeHost(string host)
    {
        if (!OspfIpPrefix.TryParseHost(host, out uint addr, out _))
        {
            return false;
        }

        return InfrastructureScope.ContainsHost(addr);
    }

    /// <summary>
    /// Derive the migration Router ID from Loopback inventory.
    /// </summary>
    /// <param name="ipAddresses">Captured <c>/ip/address</c> rows.</param>
    /// <param name="corroboratingRouterId">
    /// Optional authoritative OSPF instance or plan Router ID used only when multiple
    /// distinct in-scope Loopback hosts remain after filtering.
    /// </param>
    public static OspfLoopbackRouterIdResult Derive(
        IReadOnlyList<OspfIpAddressRow> ipAddresses,
        string? corroboratingRouterId = null)
    {
        ArgumentNullException.ThrowIfNull(ipAddresses);

        List<(OspfIpAddressRow Row, string Host, string Interface)> parsed = [];
        List<string> ignoredOutside = [];

        foreach (OspfIpAddressRow row in ipAddresses.Where(static a => IsSupportedLoopbackInterfaceName(a.Interface)))
        {
            if (!TryParseLoopbackHostAddress(row.Address, row.Network, out string host, out string? parseError))
            {
                return Failed(
                    parseError
                    ?? $"Loopback interface '{row.Interface}' has invalid address data '{row.Address}'.");
            }

            if (!IsInfrastructureScopeHost(host))
            {
                ignoredOutside.Add($"{row.Interface}={host}");
                continue;
            }

            parsed.Add((row, host, row.Interface!.Trim()));
        }

        if (parsed.Count == 0)
        {
            if (ignoredOutside.Count > 0)
            {
                return Failed(
                    "No in-scope (10.0.0.0/8) Loopback IPv4 address found for migration identity. "
                    + "Ignored outside-range Loopback address(es): "
                    + string.Join(", ", ignoredOutside)
                    + ".");
            }

            return Failed(
                "Loopback interface is missing: no IPv4 address is assigned to a supported Loopback "
                + "interface ('lo', 'Loopback', or 'Loopback-OSPF').");
        }

        List<string> uniqueHosts = parsed
            .Select(static p => p.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string selectedHost;
        if (uniqueHosts.Count == 1)
        {
            selectedHost = uniqueHosts[0];
        }
        else if (!string.IsNullOrWhiteSpace(corroboratingRouterId)
                 && uniqueHosts.Count(h => EqualsRouterId(h, corroboratingRouterId)) == 1)
        {
            selectedHost = uniqueHosts.First(h => EqualsRouterId(h, corroboratingRouterId));
        }
        else
        {
            return Failed(
                "Multiple distinct in-scope (10.0.0.0/8) Loopback addresses present ("
                + string.Join(", ", uniqueHosts)
                + "); refusing to select a Router ID without unambiguous corroborating OSPF router-id."
                + (string.IsNullOrWhiteSpace(corroboratingRouterId)
                    ? string.Empty
                    : $" Corroborating router-id '{corroboratingRouterId}' does not uniquely match."));
        }

        List<string> interfacesForHost = parsed
            .Where(p => EqualsRouterId(p.Host, selectedHost))
            .Select(static p => p.Interface)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (interfacesForHost.Count != 1)
        {
            return Failed(
                $"Selected in-scope Loopback Router ID '{selectedHost}' is assigned on multiple Loopback "
                + $"interfaces ({string.Join(", ", interfacesForHost.Select(static n => $"'{n}'"))}); refusing.");
        }

        return new OspfLoopbackRouterIdResult
        {
            Succeeded = true,
            RouterId = selectedHost,
            LoopbackInterfaceName = interfacesForHost[0],
            LoopbackCidr = $"{selectedHost}/32",
            IgnoredOutsideRangeAddresses = ignoredOutside,
            ObservedInScopeAddresses = uniqueHosts,
        };
    }

    public static bool EqualsRouterId(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts explicit <c>/32</c>, or RouterOS host form without a prefix (optionally with
    /// <c>network=</c> equal to the host), normalised as <c>/32</c>.
    /// </summary>
    internal static bool TryParseLoopbackHostAddress(
        string? address,
        string? network,
        out string host,
        out string? error)
    {
        host = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(address))
        {
            error = "Loopback IPv4 address is missing.";
            return false;
        }

        string trimmed = address.Trim();
        int slash = trimmed.IndexOf('/');
        if (slash < 0)
        {
            if (!IPAddress.TryParse(trimmed, out IPAddress? bare)
                || bare.AddressFamily != AddressFamily.InterNetwork)
            {
                error = $"Loopback address '{trimmed}' is not a valid IPv4 address.";
                return false;
            }

            // RouterOS export host form: address=10.16.0.11 (no mask) with network=10.16.0.11.
            if (!string.IsNullOrWhiteSpace(network)
                && !string.Equals(network.Trim(), bare.ToString(), StringComparison.OrdinalIgnoreCase)
                && !IPAddress.TryParse(network.Trim(), out IPAddress? _))
            {
                error = $"Loopback address '{trimmed}' has inconsistent network '{network}'.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(network)
                && IPAddress.TryParse(network.Trim(), out IPAddress? parsedNet)
                && parsedNet.AddressFamily == AddressFamily.InterNetwork
                && !string.Equals(parsedNet.ToString(), bare.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                error =
                    $"Loopback address '{trimmed}' is not a host /32 (network '{network}' differs from address).";
                return false;
            }

            host = bare.ToString();
            return true;
        }

        string addrPart = trimmed[..slash];
        string lenPart = trimmed[(slash + 1)..];
        if (!int.TryParse(lenPart, out int prefixLength))
        {
            error = $"Loopback address '{trimmed}' has a malformed prefix length.";
            return false;
        }

        if (prefixLength != 32)
        {
            error = $"Loopback address '{trimmed}' must be a /32 (found /{prefixLength}).";
            return false;
        }

        if (!IPAddress.TryParse(addrPart, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"Loopback address '{trimmed}' is not a valid IPv4 address.";
            return false;
        }

        host = ip.ToString();
        return true;
    }

    private static OspfIpPrefix ParseRequired(string cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p)
            ? p
            : throw new InvalidOperationException(cidr);

    private static OspfLoopbackRouterIdResult Failed(string error) =>
        new()
        {
            Succeeded = false,
            Errors = [error],
        };
}

internal sealed class OspfLoopbackRouterIdResult
{
    public required bool Succeeded { get; init; }

    public string? RouterId { get; init; }

    public string? LoopbackInterfaceName { get; init; }

    public string? LoopbackCidr { get; init; }

    /// <summary>Outside-range Loopback hosts ignored for migration identity (not deleted).</summary>
    public IReadOnlyList<string> IgnoredOutsideRangeAddresses { get; init; } = [];

    /// <summary>Distinct in-scope Loopback hosts considered during selection.</summary>
    public IReadOnlyList<string> ObservedInScopeAddresses { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public string? Detail => Succeeded ? null : string.Join("; ", Errors);
}
