// -----------------------------------------------------------------------------
// File: OspfLoopbackRouterId.cs
//
// Description:
// Accepts explicit /32, or RouterOS host form without a prefix (optionally
// with /// network= equal to the host), normalised as /32.
//
// -----------------------------------------------------------------------------

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
    /// <summary>
    /// Loopback interface name <c>lo</c>, matched case-insensitively.
    /// </summary>
    public const string InterfaceNameLo = "lo";
    /// <summary>
    /// Loopback interface name <c>Loopback</c>, matched case-insensitively.
    /// </summary>
    public const string InterfaceNameLoopback = "Loopback";
    /// <summary>
    /// Loopback interface name <c>Loopback-OSPF</c>, matched case-insensitively.
    /// </summary>
    public const string InterfaceNameLoopbackOspf = "Loopback-OSPF";
    /// <summary>
    /// CIDR of the infrastructure address space; only Loopback hosts inside it can supply the Router ID.
    /// </summary>
    public const string InfrastructureScopeCidr = "10.0.0.0/8";

    /// <summary>
    /// Parsed form of <see cref="InfrastructureScopeCidr"/> used for host containment checks.
    /// </summary>
    private static readonly OspfIpPrefix InfrastructureScope = ParseRequired(InfrastructureScopeCidr);

    /// <summary>
    /// Interface names recognised as the fleet Loopback.
    /// </summary>
    private static readonly string[] SupportedNames =
    [
        InterfaceNameLo,
        InterfaceNameLoopback,
        InterfaceNameLoopbackOspf,
    ];

    /// <summary>
    /// Determines whether an interface name is one of the supported Loopback names.
    /// </summary>
    /// <param name="interfaceName">Interface name from <c>/ip/address</c>; may be null or blank.</param>
    /// <returns><see langword="true"/> when the trimmed name matches a supported Loopback name, ignoring case.</returns>
    public static bool IsSupportedLoopbackInterfaceName(string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return false;
        }

        string name = interfaceName.Trim();
        return SupportedNames.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Determines whether a host address lies inside <see cref="InfrastructureScopeCidr"/>.
    /// </summary>
    /// <param name="host">IPv4 address, with or without a prefix length.</param>
    /// <returns><see langword="true"/> when the address parses and is inside the infrastructure scope; otherwise <see langword="false"/>.</returns>
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
    /// <returns>A successful result with the selected Router ID, or a failed result describing why none could be chosen.</returns>
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

    /// <summary>
    /// Compares two Router ID strings after trimming, ignoring case.
    /// </summary>
    /// <param name="left">First Router ID.</param>
    /// <param name="right">Second Router ID.</param>
    /// <returns><see langword="true"/> when both are non-blank and equal.</returns>
    public static bool EqualsRouterId(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Accepts explicit <c>/32</c>, or RouterOS host form without a prefix (optionally with
    /// <c>network=</c> equal to the host), normalised as <c>/32</c>.
    /// </summary>
    /// <param name="address">Address text from <c>/ip/address</c>, with or without a prefix length.</param>
    /// <param name="network">Optional <c>network=</c> value used to cross-check host-form addresses.</param>
    /// <param name="host">Receives the normalised host address on success.</param>
    /// <param name="error">Receives the failure reason when parsing fails.</param>
    /// <returns><see langword="true"/> when the address is a valid host /32; otherwise <see langword="false"/>.</returns>
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

    /// <summary>
    /// Parses a compile-time constant CIDR, failing fast if it is invalid.
    /// </summary>
    /// <param name="cidr">CIDR text to parse.</param>
    /// <returns>The parsed prefix.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="cidr"/> cannot be parsed.</exception>
    private static OspfIpPrefix ParseRequired(string cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p)
            ? p
            : throw new InvalidOperationException(cidr);

    /// <summary>
    /// Creates a failed derivation result carrying a single error.
    /// </summary>
    /// <param name="error">Description of why derivation failed.</param>
    /// <returns>A result with <c>Succeeded</c> set to <see langword="false"/>.</returns>
    private static OspfLoopbackRouterIdResult Failed(string error) =>
        new()
        {
            Succeeded = false,
            Errors = [error],
        };
}

/// <summary>
/// Result of deriving OSPF Router ID from Loopback addressing.
/// </summary>
internal sealed class OspfLoopbackRouterIdResult
{
    /// <summary>
    /// Gets a value indicating whether a Router ID was derived unambiguously.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Host address (no prefix length) of the single in-scope Loopback address selected as the OSPF
    /// Router ID. <see langword="null"/> when <see cref="Succeeded"/> is <see langword="false"/>.
    /// </summary>
    public string? RouterId { get; init; }

    /// <summary>
    /// Name of the supported Loopback interface (<c>lo</c>, <c>Loopback</c> or <c>Loopback-OSPF</c>) that
    /// holds <see cref="RouterId"/>, as written on the router. <see langword="null"/> when derivation failed.
    /// </summary>
    public string? LoopbackInterfaceName { get; init; }

    /// <summary>
    /// <see cref="RouterId"/> normalised to host-prefix form (<c>address/32</c>), which is how the Loopback
    /// address is compared and re-asserted even when RouterOS exported it without a prefix.
    /// <see langword="null"/> when derivation failed.
    /// </summary>
    public string? LoopbackCidr { get; init; }

    /// <summary>Outside-range Loopback hosts ignored for migration identity (not deleted).</summary>
    public IReadOnlyList<string> IgnoredOutsideRangeAddresses { get; init; } = [];

    /// <summary>Distinct in-scope Loopback hosts considered during selection.</summary>
    public IReadOnlyList<string> ObservedInScopeAddresses { get; init; } = [];

    /// <summary>
    /// Human-readable reasons no Router ID could be chosen (missing Loopback, malformed or non-<c>/32</c>
    /// address, ambiguous in-scope hosts). Always empty when <see cref="Succeeded"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Single-line failure message built by joining <see cref="Errors"/> with <c>"; "</c>, suitable for
    /// logs and gate failures. <see langword="null"/> when derivation succeeded.
    /// </summary>
    public string? Detail => Succeeded ? null : string.Join("; ", Errors);
}
