using System.Net;
using System.Text;

namespace MikrotikWorker.Logging;

internal static class DeviceLogNaming
{
    public const string DeviceLogKeyPropertyName = "DeviceLogKey";

    public static string BuildFileName(string? routerIdentityOrName, IPAddress routerIp)
    {
        ArgumentNullException.ThrowIfNull(routerIp);

        string label = string.IsNullOrWhiteSpace(routerIdentityOrName)
            ? "router"
            : Sanitize(routerIdentityOrName);
        string ip = Sanitize(routerIp.ToString());
        return $"{label}_{ip}.log";
    }

    public static string BuildDeviceLogKey(string? routerIdentityOrName, IPAddress routerIp) =>
        BuildFileName(routerIdentityOrName, routerIp);

    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 120)];
        int written = 0;
        foreach (char c in value.Trim())
        {
            if (written >= buffer.Length)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                buffer[written++] = c;
            }
            else if (c is ' ' or ':' or '/' or '\\')
            {
                buffer[written++] = '_';
            }
        }

        if (written == 0)
        {
            return "router";
        }

        return new string(buffer[..written]);
    }
}
