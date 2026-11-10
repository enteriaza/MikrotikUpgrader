using System.Collections.Concurrent;

namespace MikrotikWorker.Logging;

/// <summary>
/// Tracks which router IP first claimed a sanitised identity so device logs are not mixed
/// when two physical routers share the same RouterOS identity string.
/// </summary>
internal sealed class DeviceLogIdentityRegistry
{
    private readonly ConcurrentDictionary<string, string> _identityToIp =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers or validates an identity→IP mapping.
    /// Returns false when the same identity+IP was already registered (idempotent).
    /// Sets <paramref name="collision"/> when the identity is already bound to a different IP.
    /// </summary>
    public bool TryRegister(string sanitisedIdentity, string routerIp, out bool collision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitisedIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(routerIp);

        if (_identityToIp.TryAdd(sanitisedIdentity, routerIp))
        {
            collision = false;
            return true;
        }

        if (_identityToIp.TryGetValue(sanitisedIdentity, out string? existing)
            && string.Equals(existing, routerIp, StringComparison.OrdinalIgnoreCase))
        {
            collision = false;
            return false;
        }

        collision = true;
        return true;
    }
}
