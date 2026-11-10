using System.Net;

namespace MikrotikWorker.Logging;

internal static class DeviceLogNaming
{
    public const string DeviceLogKeyPropertyName = "DeviceLogKey";

    /// <summary>
    /// Marks a device-scoped event that should also appear in the service log at Information level.
    /// </summary>
    public const string ServiceSummaryPropertyName = "ServiceSummary";

    /// <summary>
    /// Marks a log event that must stay in the service log only (not the per-device file),
    /// even while a <see cref="DeviceLogScope"/> is active.
    /// </summary>
    public const string ServiceOnlyPropertyName = "ServiceOnly";

    /// <summary>
    /// Persistent per-device log file: <c>{Identity}_{IP}.log</c>.
    /// </summary>
    public static string BuildDeviceLogFileName(string? routerIdentity, IPAddress routerIp)
    {
        ArgumentNullException.ThrowIfNull(routerIp);
        return BuildDeviceStem(routerIdentity, routerIp) + ".log";
    }

    /// <summary>
    /// Shared stem for device log and timestamped backup artefacts: <c>{Identity}_{IP}</c>.
    /// </summary>
    public static string BuildDeviceStem(string? routerIdentity, IPAddress routerIp)
    {
        ArgumentNullException.ThrowIfNull(routerIp);
        string label = string.IsNullOrWhiteSpace(routerIdentity)
            ? "router"
            : Sanitize(routerIdentity);
        return $"{label}_{SanitizeIp(routerIp)}";
    }

    public static string BuildPendingLogFileName(IPAddress routerIp)
    {
        ArgumentNullException.ThrowIfNull(routerIp);
        return "pending_" + SanitizeIp(routerIp) + ".log";
    }

    /// <summary>
    /// Registers the identity→IP mapping when a registry is supplied, and always returns
    /// <c>{Identity}_{IP}.log</c> so distinct routers never share a log by identity alone.
    /// </summary>
    public static string ResolveIdentityLogFileName(
        DeviceLogIdentityRegistry? registry,
        string? routerIdentity,
        IPAddress routerIp)
    {
        ArgumentNullException.ThrowIfNull(routerIp);
        string stem = BuildDeviceStem(routerIdentity, routerIp);
        registry?.TryRegister(
            string.IsNullOrWhiteSpace(routerIdentity) ? "router" : Sanitize(routerIdentity),
            routerIp.ToString(),
            out _);
        return stem + ".log";
    }

    public static string SanitizeIp(IPAddress routerIp)
    {
        ArgumentNullException.ThrowIfNull(routerIp);
        // IPv4 keeps dotted notation ('.' is allowed). IPv6 ':' becomes '_'.
        return Sanitize(routerIp.ToString());
    }

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
            else if (c is ' ' or ':' or '/' or '\\' or '%' or '[' or ']')
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
