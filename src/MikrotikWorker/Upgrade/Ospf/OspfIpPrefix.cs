using System.Net;
using System.Net.Sockets;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// IPv4 CIDR prefix used for OSPF network matching (prefix-agnostic; no hard-coded ranges).
/// </summary>
internal readonly record struct OspfIpPrefix(uint NetworkAddress, int PrefixLength)
{
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

    public bool Contains(OspfIpPrefix other)
    {
        if (other.PrefixLength < PrefixLength)
        {
            return false;
        }

        uint mask = PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);
        return (NetworkAddress & mask) == (other.NetworkAddress & mask);
    }

    public bool ContainsHost(uint hostAddress)
    {
        uint mask = PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);
        return (NetworkAddress & mask) == (hostAddress & mask);
    }

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
