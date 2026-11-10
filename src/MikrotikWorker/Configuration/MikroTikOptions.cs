namespace MikrotikWorker.Configuration;

/// <summary>
/// Global MikroTik RouterOS API integration settings.
/// Device addresses come from scheduled CSV rows; username/password are shared for all routers.
/// </summary>
public sealed class MikroTikOptions
{
    public const string SectionName = "Mikrotik";

    /// <summary>
    /// When false, the worker does not attempt device connections.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Shared RouterOS API username used for every scheduled router.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Shared RouterOS API password used for every scheduled router.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Default RouterOS API TCP port used when a device connection omits an explicit port.
    /// </summary>
    public int Port { get; set; } = 8728;

    /// <summary>
    /// Timeout applied to the TCP connect and login handshake.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reserved for future reconnect / retry behaviour between device jobs.
    /// </summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(30);
}
