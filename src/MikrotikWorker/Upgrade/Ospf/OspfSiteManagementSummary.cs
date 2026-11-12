// -----------------------------------------------------------------------------
// File: OspfSiteManagementSummary.cs
//
// Description:
// Derives the mandatory site management summary /24 from interface-assigned
// addresses inside 10.0.0.0/8, excluding Loopback /32 and designated backhaul
// /30s.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Derives the mandatory site management summary <c>/24</c> from interface-assigned addresses
/// inside <c>10.0.0.0/8</c>, excluding Loopback <c>/32</c> and designated backhaul <c>/30</c>s.
/// </summary>
internal static class OspfSiteManagementSummary
{
    /// <summary>
    /// Infrastructure address space <c>10.0.0.0/8</c>; only addresses inside it can form the management summary.
    /// </summary>
    private static readonly OspfIpPrefix TenDotZero = ParseRequired("10.0.0.0/8");

    /// <summary>
    /// Derives the site management <c>/24</c> from interface addresses.
    /// Loopback, PPPoE, backhaul, and out-of-scope addresses are excluded. When several candidates remain, one is chosen only if exactly one is corroborated by a hint.
    /// </summary>
    /// <param name="ipAddresses">Captured <c>/ip/address</c> rows.</param>
    /// <param name="loopback">Resolved Loopback Router ID result; must have succeeded.</param>
    /// <param name="backhauls">Planned active backhauls whose <c>/30</c> subnets are excluded.</param>
    /// <param name="corroboratingManagementCidrs">Optional management CIDR hints used to break ties.</param>
    /// <returns>A successful result with the chosen <c>/24</c>, or a failed result describing why none could be chosen unambiguously.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
    public static OspfSiteManagementSummaryResult Derive(
        IReadOnlyList<OspfIpAddressRow> ipAddresses,
        OspfLoopbackRouterIdResult loopback,
        IReadOnlyList<OspfPlanBackhaulTemplate> backhauls,
        IReadOnlyList<string>? corroboratingManagementCidrs = null)
    {
        ArgumentNullException.ThrowIfNull(ipAddresses);
        ArgumentNullException.ThrowIfNull(loopback);
        ArgumentNullException.ThrowIfNull(backhauls);

        if (!loopback.Succeeded
            || string.IsNullOrWhiteSpace(loopback.RouterId)
            || string.IsNullOrWhiteSpace(loopback.LoopbackInterfaceName))
        {
            return Failed(
                loopback.Errors.Count > 0
                    ? loopback.Errors
                    : ["Loopback must be resolved before deriving the site management /24."]);
        }

        List<OspfIpPrefix> backhaulNets = [];
        foreach (OspfPlanBackhaulTemplate bh in backhauls)
        {
            if (!OspfIpPrefix.TryParse(bh.NetworkCidr, out OspfIpPrefix net) || net.PrefixLength != 30)
            {
                return Failed(
                    $"Backhaul '{bh.Interface}' network '{bh.NetworkCidr}' is not a usable /30 for management /24 exclusion.");
            }

            backhaulNets.Add(net);
        }

        HashSet<string> mgmt24 = new(StringComparer.Ordinal);
        List<string> contributing = [];
        List<string> ignoredOutside = [];
        List<string> ignoredPppoe = [];

        foreach (OspfIpAddressRow row in ipAddresses)
        {
            if (string.IsNullOrWhiteSpace(row.Interface) || string.IsNullOrWhiteSpace(row.Address))
            {
                continue;
            }

            if (OspfLoopbackRouterId.IsSupportedLoopbackInterfaceName(row.Interface))
            {
                continue;
            }

            if (IsPppoeRoleInterface(row.Interface))
            {
                ignoredPppoe.Add($"{row.Interface}={row.Address}");
                continue;
            }

            if (!TryParseAssignedHost(row, out uint host, out string? parseError))
            {
                return Failed(
                    parseError
                    ?? $"Interface '{row.Interface}' address '{row.Address}' cannot be classified for management /24 derivation.");
            }

            if (!TenDotZero.ContainsHost(host))
            {
                ignoredOutside.Add($"{row.Interface}={row.Address}");
                continue;
            }

            if (backhaulNets.Any(n => n.ContainsHost(host)))
            {
                continue;
            }

            uint masked = host & (uint.MaxValue << 8); // /24
            string cidr = FormatHost(masked) + "/24";
            mgmt24.Add(cidr);
            contributing.Add($"{row.Interface}={row.Address}->{cidr}");
        }

        if (mgmt24.Count == 0)
        {
            return Failed(
                "No eligible site management /24 could be derived from interface-assigned addresses "
                + "in 10.0.0.0/8 after excluding Loopback, backhaul, and PPPoE role interfaces.");
        }

        if (mgmt24.Count > 1)
        {
            HashSet<string> hints = new(
                (corroboratingManagementCidrs ?? [])
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Select(static s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);

            List<string> corroborated = mgmt24
                .Where(c => hints.Contains(c))
                .OrderBy(static s => s, StringComparer.Ordinal)
                .ToList();

            if (corroborated.Count == 1)
            {
                string chosen = corroborated[0];
                return new OspfSiteManagementSummaryResult
                {
                    Succeeded = true,
                    ManagementCidr = chosen,
                    ContributingAddresses = contributing
                        .Where(c => c.Contains(chosen, StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    IgnoredOutsideRangeAddresses = ignoredOutside,
                    IgnoredPppoeAddresses = ignoredPppoe,
                };
            }

            return Failed(
                "Multiple distinct eligible site management /24 prefixes were derived ("
                + string.Join(", ", mgmt24.OrderBy(static s => s, StringComparer.Ordinal))
                + "); refusing to select without unambiguous corroborating MGMT evidence. Contributors: "
                + string.Join("; ", contributing));
        }

        string summary = mgmt24.Single();
        return new OspfSiteManagementSummaryResult
        {
            Succeeded = true,
            ManagementCidr = summary,
            ContributingAddresses = contributing,
            IgnoredOutsideRangeAddresses = ignoredOutside,
            IgnoredPppoeAddresses = ignoredPppoe,
        };
    }

    /// <summary>
    /// Collect MGMT-commented area ranges and explicit management-style site network /24s as hints.
    /// </summary>
    public static IReadOnlyList<string> CollectCorroboratingManagementCidrs(
        IReadOnlyList<OspfPlanAreaRange> ranges,
        IReadOnlyList<OspfNetworkRow>? sourceNetworks = null)
    {
        HashSet<string> hints = new(StringComparer.OrdinalIgnoreCase);
        foreach (OspfPlanAreaRange range in ranges)
        {
            if (!string.Equals(range.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (OspfIpPrefix.TryParse(range.RangeCidr, out OspfIpPrefix prefix) && prefix.PrefixLength == 24)
            {
                hints.Add(prefix.ToCidrString());
            }
        }

        if (sourceNetworks is not null)
        {
            foreach (OspfNetworkRow network in sourceNetworks)
            {
                bool mgmtComment =
                    string.Equals(network.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase)
                    || (network.Comment?.Contains("MGMT", StringComparison.OrdinalIgnoreCase) ?? false)
                    || string.IsNullOrWhiteSpace(network.Comment);

                if (!mgmtComment)
                {
                    continue;
                }

                if (OspfIpPrefix.TryParse(network.Network, out OspfIpPrefix net)
                    && net.PrefixLength == 24
                    && TenDotZero.ContainsHost(net.NetworkAddress))
                {
                    hints.Add(net.ToCidrString());
                }
            }
        }

        return hints.OrderBy(static s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Determines whether an interface name denotes a PPPoE role interface, whose addresses never contribute to management.
    /// </summary>
    /// <param name="interfaceName">Interface name from <c>/ip/address</c>.</param>
    /// <returns><see langword="true"/> when the name contains <c>pppoe</c> or starts with <c>&lt;pppoe-</c>.</returns>
    internal static bool IsPppoeRoleInterface(string interfaceName)
    {
        string n = interfaceName.Trim();
        return n.Contains("pppoe", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("<pppoe-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the IPv4 host address of an interface address row.
    /// </summary>
    /// <param name="row">Address row to parse.</param>
    /// <param name="host">Receives the address as a 32-bit value on success.</param>
    /// <param name="error">Receives the failure reason when parsing fails.</param>
    /// <returns><see langword="true"/> when the address and any prefix length are valid.</returns>
    private static bool TryParseAssignedHost(OspfIpAddressRow row, out uint host, out string? error)
    {
        host = 0;
        error = null;
        string trimmed = row.Address!.Trim();
        int slash = trimmed.IndexOf('/');
        string addrPart = slash < 0 ? trimmed : trimmed[..slash];
        if (slash >= 0)
        {
            if (!int.TryParse(trimmed[(slash + 1)..], out int len) || len is < 0 or > 32)
            {
                error = $"Interface '{row.Interface}' address '{row.Address}' has a malformed prefix.";
                return false;
            }
        }

        if (!IPAddress.TryParse(addrPart, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"Interface '{row.Interface}' address '{row.Address}' is not a valid IPv4 address.";
            return false;
        }

        byte[] b = ip.GetAddressBytes();
        host = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        return true;
    }

    /// <summary>
    /// Formats a 32-bit IPv4 value as dotted-quad text.
    /// </summary>
    /// <param name="host">IPv4 address as a 32-bit value.</param>
    /// <returns>The dotted-quad address.</returns>
    private static string FormatHost(uint host)
    {
        byte[] bytes =
        [
            (byte)(host >> 24),
            (byte)(host >> 16),
            (byte)(host >> 8),
            (byte)host,
        ];
        return new IPAddress(bytes).ToString();
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
    /// Creates a failed result carrying a single error.
    /// </summary>
    /// <param name="error">Description of why derivation failed.</param>
    /// <returns>A failed result.</returns>
    private static OspfSiteManagementSummaryResult Failed(string error) => Failed([error]);

    /// <summary>
    /// Creates a failed result carrying the supplied errors.
    /// </summary>
    /// <param name="errors">Reasons derivation failed.</param>
    /// <returns>A failed result.</returns>
    private static OspfSiteManagementSummaryResult Failed(IReadOnlyList<string> errors) =>
        new()
        {
            Succeeded = false,
            Errors = errors.ToList(),
        };
}

/// <summary>
/// Derived site management summary /24 and supporting details.
/// </summary>
internal sealed class OspfSiteManagementSummaryResult
{
    /// <summary>
    /// Gets a value indicating whether a single management <c>/24</c> was derived.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// The single site management <c>/24</c> in CIDR form (for example <c>10.20.30.0/24</c>) that the
    /// in-scope interface addresses collapse into. <see langword="null"/> when derivation failed.
    /// </summary>
    public string? ManagementCidr { get; init; }

    /// <summary>
    /// Evidence trail of the addresses that produced <see cref="ManagementCidr"/>, one entry per address
    /// formatted as <c>interface=address-&gt;cidr</c> (the interface, its address, and the <c>/24</c> it maps to).
    /// </summary>
    public IReadOnlyList<string> ContributingAddresses { get; init; } = [];

    /// <summary>
    /// Gets addresses outside <c>10.0.0.0/8</c> that were ignored.
    /// </summary>
    public IReadOnlyList<string> IgnoredOutsideRangeAddresses { get; init; } = [];

    /// <summary>
    /// Gets PPPoE role interface addresses that were ignored.
    /// </summary>
    public IReadOnlyList<string> IgnoredPppoeAddresses { get; init; } = [];

    /// <summary>
    /// Human-readable reasons no single management <c>/24</c> could be derived. Always empty when
    /// <see cref="Succeeded"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Single-line failure message built by joining <see cref="Errors"/> with <c>"; "</c>, suitable for
    /// logs and gate failures. <see langword="null"/> when derivation succeeded.
    /// </summary>
    public string? Detail => Succeeded ? null : string.Join("; ", Errors);
}
