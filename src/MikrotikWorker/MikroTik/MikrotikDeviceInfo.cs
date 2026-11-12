// -----------------------------------------------------------------------------
// File: MikrotikDeviceInfo.cs
//
// Description:
// Read-only snapshot of identity, RouterOS version, architecture/board, uptime,
// and optional RouterBOARD firmware fields collected during pre-flight.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Read-only snapshot of RouterOS device information.
/// </summary>
public sealed class MikrotikDeviceInfo
{
    /// <summary>RouterOS system identity.</summary>
    public required string Identity { get; init; }

    /// <summary>Installed RouterOS version string (as reported by the device).</summary>
    public required string Version { get; init; }

    /// <summary>CPU architecture name (e.g. <c>arm</c>, <c>mipsbe</c>) when reported.</summary>
    public string? ArchitectureName { get; init; }

    /// <summary>Board name when reported.</summary>
    public string? BoardName { get; init; }

    /// <summary>Platform string when reported.</summary>
    public string? Platform { get; init; }

    /// <summary>Uptime string when reported.</summary>
    public string? Uptime { get; init; }

    /// <summary>RouterBOARD model when <c>/system/routerboard</c> is available.</summary>
    public string? RouterboardModel { get; init; }

    /// <summary>RouterBOARD firmware type when available.</summary>
    public string? RouterboardFirmwareType { get; init; }

    /// <summary>Currently running RouterBOARD firmware version when available.</summary>
    public string? RouterboardCurrentFirmware { get; init; }

    /// <summary>Upgrade firmware version available on the board when reported.</summary>
    public string? RouterboardUpgradeFirmware { get; init; }

    /// <summary>Factory firmware version when reported.</summary>
    public string? RouterboardFactoryFirmware { get; init; }
}
