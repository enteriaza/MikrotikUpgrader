// -----------------------------------------------------------------------------
// File: MikrotikConnectionOptions.cs
//
// Description:
// Per-device connection parameters for a single API/SCP session. Host comes from
// the scheduled CSV; credentials from IRouterCredentialResolver; optional Port
// and ConnectTimeout fall back to Mikrotik section defaults when null.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Per-device RouterOS API connection parameters.
/// Host comes from the scheduled CSV; username/password come from shared Mikrotik options.
/// </summary>
public sealed class MikrotikConnectionOptions
{
    /// <summary>Device management IP or host name.</summary>
    public required string Host { get; init; }

    /// <summary>
    /// Optional device-specific API port. When null, the global <c>Mikrotik:Port</c> is used.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>RouterOS API username. Never log.</summary>
    public required string Username { get; init; }

    /// <summary>RouterOS API password. Never log.</summary>
    public required string Password { get; init; }

    /// <summary>
    /// Optional connect/login timeout for this connection.
    /// When null, the global <c>Mikrotik:ConnectionTimeout</c> is used.
    /// </summary>
    public TimeSpan? ConnectTimeout { get; init; }
}
