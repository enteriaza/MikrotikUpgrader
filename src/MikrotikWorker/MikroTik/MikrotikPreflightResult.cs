namespace MikrotikWorker.MikroTik;

/// <summary>
/// Overall outcome of a read-only device pre-flight check.
/// </summary>
public enum MikrotikPreflightStatus
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,
}

/// <summary>
/// Connection phase of a pre-flight attempt.
/// </summary>
public enum MikrotikConnectionStatus
{
    NotAttempted = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
}

/// <summary>
/// Device-information retrieval phase of a pre-flight attempt.
/// </summary>
public enum MikrotikInformationStatus
{
    NotAttempted = 0,
    Succeeded = 1,
    Failed = 2,
    Incomplete = 3,
    Cancelled = 4,
}

/// <summary>
/// Structured, credential-free result of a RouterOS device pre-flight.
/// Suitable as input for a future firmware package compatibility check.
/// </summary>
public sealed class MikrotikPreflightResult
{
    public required string RouterIp { get; init; }

    public string? RouterIdentity { get; init; }

    public string? RouterOsVersion { get; init; }

    public string? Architecture { get; init; }

    public string? BoardName { get; init; }

    public string? Platform { get; init; }

    public string? Uptime { get; init; }

    public string? RouterboardModel { get; init; }

    public string? RouterboardFirmwareType { get; init; }

    public string? RouterboardCurrentFirmware { get; init; }

    public string? RouterboardUpgradeFirmware { get; init; }

    public string? RouterboardFactoryFirmware { get; init; }

    public MikrotikConnectionStatus ConnectionStatus { get; init; }

    public MikrotikInformationStatus InformationStatus { get; init; }

    public MikrotikPreflightStatus Status { get; init; }

    public string? FailureReason { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    public TimeSpan Duration { get; init; }

    public static MikrotikPreflightResult FromDeviceInfo(
        string routerIp,
        MikrotikDeviceInfo info,
        DateTimeOffset timestampUtc,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(info);

        string? missing = ValidateRequired(info);
        bool complete = missing is null;

        return new MikrotikPreflightResult
        {
            RouterIp = routerIp,
            RouterIdentity = NullIfWhiteSpace(info.Identity),
            RouterOsVersion = NullIfWhiteSpace(info.Version),
            Architecture = info.ArchitectureName,
            BoardName = info.BoardName,
            Platform = info.Platform,
            Uptime = info.Uptime,
            RouterboardModel = info.RouterboardModel,
            RouterboardFirmwareType = info.RouterboardFirmwareType,
            RouterboardCurrentFirmware = info.RouterboardCurrentFirmware,
            RouterboardUpgradeFirmware = info.RouterboardUpgradeFirmware,
            RouterboardFactoryFirmware = info.RouterboardFactoryFirmware,
            ConnectionStatus = MikrotikConnectionStatus.Succeeded,
            InformationStatus = complete
                ? MikrotikInformationStatus.Succeeded
                : MikrotikInformationStatus.Incomplete,
            Status = complete ? MikrotikPreflightStatus.Succeeded : MikrotikPreflightStatus.Failed,
            FailureReason = missing,
            TimestampUtc = timestampUtc,
            Duration = duration,
        };
    }

    public static string? ValidateRequired(MikrotikDeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (string.IsNullOrWhiteSpace(info.Identity))
        {
            return "Required device identity is missing.";
        }

        if (string.IsNullOrWhiteSpace(info.Version))
        {
            return "Required RouterOS version is missing.";
        }

        if (string.IsNullOrWhiteSpace(info.ArchitectureName))
        {
            return "Required architecture is missing.";
        }

        return null;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
