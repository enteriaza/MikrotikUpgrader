namespace MikrotikWorker.MikroTik;

/// <summary>
/// Read-only snapshot of RouterOS device information.
/// </summary>
public sealed class MikrotikDeviceInfo
{
    public required string Identity { get; init; }

    public required string Version { get; init; }

    public string? ArchitectureName { get; init; }

    public string? BoardName { get; init; }

    public string? Platform { get; init; }

    public string? Uptime { get; init; }

    public string? RouterboardModel { get; init; }

    public string? RouterboardFirmwareType { get; init; }

    public string? RouterboardCurrentFirmware { get; init; }

    public string? RouterboardUpgradeFirmware { get; init; }

    public string? RouterboardFactoryFirmware { get; init; }
}
