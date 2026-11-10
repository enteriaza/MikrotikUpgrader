namespace MikrotikWorker.MikroTik;

/// <summary>
/// Per-device RouterOS API connection parameters.
/// Host comes from the scheduled CSV; username/password come from shared Mikrotik options.
/// </summary>
public sealed class MikrotikConnectionOptions
{
    public required string Host { get; init; }

    /// <summary>
    /// Optional device-specific API port. When null, the global <c>Mikrotik:Port</c> is used.
    /// </summary>
    public int? Port { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }
}
