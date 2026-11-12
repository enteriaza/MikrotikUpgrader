// -----------------------------------------------------------------------------
// File: MikroTikOptions.cs
//
// Description:
// Options bound from the "Mikrotik" configuration section for shared RouterOS API
// credentials, default port, and connect timeouts. Device addresses come from
// scheduled CSV rows, not this section. Mikrotik:Enabled gates API connectivity
// independently of FirmwareUpgrade:Enabled (which only controls upgrade stages).
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Configuration;

/// <summary>
/// Global MikroTik RouterOS API integration settings.
/// Device addresses come from scheduled CSV rows; username/password are shared for all routers.
/// </summary>
/// <remarks>
/// <c>Mikrotik:Enabled</c> and <c>FirmwareUpgrade:Enabled</c> are independent kill-switches.
/// Disabling MikroTik prevents connections; disabling firmware upgrade still allows connect/
/// assessment but refuses fetch/install/reboot (jobs report Failed).
/// </remarks>
public sealed class MikroTikOptions
{
    /// <summary>Configuration section name: <c>Mikrotik</c>.</summary>
    public const string SectionName = "Mikrotik";

    /// <summary>
    /// When false, the worker does not attempt device connections.
    /// Independent of <c>FirmwareUpgrade:Enabled</c>. Defaults to <c>false</c> when omitted.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Shared RouterOS API username used for every scheduled router.
    /// Defaults to empty; required when <see cref="Enabled"/> is true.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Shared RouterOS API password used for every scheduled router.
    /// Defaults to empty; required when <see cref="Enabled"/> is true.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Default RouterOS API TCP port used when a device connection omits an explicit port.
    /// Default: 8728.
    /// </summary>
    public int Port { get; set; } = 8728;

    /// <summary>
    /// Timeout applied to the TCP connect and login handshake.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reserved for future reconnect / retry behaviour between device jobs.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(30);
}
