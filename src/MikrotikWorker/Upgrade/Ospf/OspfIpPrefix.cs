// -----------------------------------------------------------------------------
// File: OspfIpPrefix.cs
//
// Description:
// IPv4 CIDR prefix used for OSPF network matching (prefix-agnostic; no
// hard-coded ranges).
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// IPv4 CIDR prefix used for OSPF network matching (prefix-agnostic; no hard-coded ranges).
/// </summary>
internal readonly record struct OspfIpPrefix(uint NetworkAddress, int PrefixLength)
{
    /// <summary>
    /// Formats the prefix as dotted-quad CIDR text.
    /// </summary>
    /// <returns>The CIDR text, for example <c>10.0.0.0/24</c>.</returns>
    public string ToCidrString()
    {
        byte[] bytes =
        [
            (byte)(NetworkAddress >> 24),
            (byte)(NetworkAddress >> 16),
            (byte)(NetworkAddress >> 8),
            (byte)NetworkAddress,
        ];
        return $"{new IPAddress(bytes)}/{PrefixLength}";
    }

    /// <summary>
    /// True when the other prefix is equal or a more-specific subnet of this prefix.
    /// </summary>
    public bool Contains(OspfIpPrefix other)
    {
        if (other.PrefixLength < PrefixLength)
        {
            return false;
        }

        uint mask = PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);
        return (NetworkAddress & mask) == (other.NetworkAddress & mask);
    }

    /// <summary>
    /// Determines whether a host address falls inside this prefix.
    /// </summary>
    /// <param name="hostAddress">IPv4 address as a 32-bit value.</param>
    /// <returns><see langword="true"/> when the address is inside the prefix.</returns>
    public bool ContainsHost(uint hostAddress)
    {
        uint mask = PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);
        return (NetworkAddress & mask) == (hostAddress & mask);
    }

    /// <summary>
    /// Parses an IPv4 CIDR string into an OspfIpPrefix.
    /// </summary>
    public static bool TryParse(string? text, out OspfIpPrefix prefix)
    {
        prefix = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        int slash = trimmed.IndexOf('/');
        string addrPart = slash < 0 ? trimmed : trimmed[..slash];
        int len = 32;
        if (slash >= 0)
        {
            if (!int.TryParse(trimmed[(slash + 1)..], out len) || len is < 0 or > 32)
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(addrPart, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] b = ip.GetAddressBytes();
        uint addr = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint mask = len == 0 ? 0u : uint.MaxValue << (32 - len);
        prefix = new OspfIpPrefix(addr & mask, len);
        return true;
    }

    /// <summary>
    /// Parses an IPv4 address with an optional prefix length; the length defaults to 32 when omitted.
    /// </summary>
    /// <param name="addressWithOptionalMask">Address text such as <c>10.0.0.1</c> or <c>10.0.0.1/24</c>.</param>
    /// <param name="host">Receives the address as a 32-bit value.</param>
    /// <param name="hostPrefixLength">Receives the prefix length.</param>
    /// <returns><see langword="true"/> when the text is a valid address.</returns>
    public static bool TryParseHost(string? addressWithOptionalMask, out uint host, out int hostPrefixLength)
    {
        host = 0;
        hostPrefixLength = 32;
        if (string.IsNullOrWhiteSpace(addressWithOptionalMask))
        {
            return false;
        }

        string trimmed = addressWithOptionalMask.Trim();
        int slash = trimmed.IndexOf('/');
        string addrPart = slash < 0 ? trimmed : trimmed[..slash];
        if (slash >= 0)
        {
            if (!int.TryParse(trimmed[(slash + 1)..], out hostPrefixLength) || hostPrefixLength is < 0 or > 32)
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(addrPart, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] b = ip.GetAddressBytes();
        host = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        return true;
    }
}
