// -----------------------------------------------------------------------------
// File: MikrotikPreflightResult.cs
//
// Description:
// Credential-free pre-flight result DTOs and status enums used after connect +
// device-info read. FromDeviceInfo marks Incomplete/Failed when identity, version,
// or architecture is missing.
//
// -----------------------------------------------------------------------------
namespace MikrotikWorker.MikroTik;

/// <summary>
/// Overall outcome of a read-only device pre-flight check.
/// </summary>
public enum MikrotikPreflightStatus
{
    /// <summary>Required identity, version, and architecture were present.</summary>
    Succeeded = 0,
    /// <summary>Connection or required fields failed.</summary>
    Failed = 1,
    /// <summary>Caller cancelled during pre-flight.</summary>
    Cancelled = 2,
}

/// <summary>
/// Connection phase of a pre-flight attempt.
/// </summary>
public enum MikrotikConnectionStatus
{
    /// <summary>Connect was not attempted.</summary>
    NotAttempted = 0,
    /// <summary>API connect/login succeeded.</summary>
    Succeeded = 1,
    /// <summary>API connect/login failed.</summary>
    Failed = 2,
    /// <summary>Connect cancelled by caller.</summary>
    Cancelled = 3,
}

/// <summary>
/// Device-information retrieval phase of a pre-flight attempt.
/// </summary>
public enum MikrotikInformationStatus
{
    /// <summary>Device info was not read.</summary>
    NotAttempted = 0,
    /// <summary>Required fields were present.</summary>
    Succeeded = 1,
    /// <summary>Device info read threw or was unusable.</summary>
    Failed = 2,
    /// <summary>Connected but identity/version/architecture missing.</summary>
    Incomplete = 3,
    /// <summary>Info read cancelled by caller.</summary>
    Cancelled = 4,
}

/// <summary>
/// Structured, credential-free result of a RouterOS device pre-flight.
/// Suitable as input for a future firmware package compatibility check.
/// </summary>
public sealed class MikrotikPreflightResult
{
    /// <summary>Target router address used for the preflight attempt.</summary>
    public required string RouterIp { get; init; }

    /// <summary>RouterOS identity when retrieved; otherwise null.</summary>
    public string? RouterIdentity { get; init; }

    /// <summary>Reported RouterOS version string when retrieved.</summary>
    public string? RouterOsVersion { get; init; }

    /// <summary>CPU/architecture string when retrieved.</summary>
    public string? Architecture { get; init; }

    /// <summary>Board name when retrieved.</summary>
    public string? BoardName { get; init; }

    /// <summary>Platform string when retrieved.</summary>
    public string? Platform { get; init; }

    /// <summary>Device uptime string when retrieved.</summary>
    public string? Uptime { get; init; }

    /// <summary>RouterBOARD model when retrieved.</summary>
    public string? RouterboardModel { get; init; }

    /// <summary>RouterBOARD firmware type when retrieved.</summary>
    public string? RouterboardFirmwareType { get; init; }

    /// <summary>Currently running RouterBOARD firmware when retrieved.</summary>
    public string? RouterboardCurrentFirmware { get; init; }

    /// <summary>Available RouterBOARD upgrade firmware when retrieved.</summary>
    public string? RouterboardUpgradeFirmware { get; init; }

    /// <summary>Factory RouterBOARD firmware when retrieved.</summary>
    public string? RouterboardFactoryFirmware { get; init; }

    /// <summary>Whether the API connection/login succeeded.</summary>
    public MikrotikConnectionStatus ConnectionStatus { get; init; }

    /// <summary>Whether identity/version/architecture information was complete.</summary>
    public MikrotikInformationStatus InformationStatus { get; init; }

    /// <summary>Overall preflight outcome combining connection and information status.</summary>
    public MikrotikPreflightStatus Status { get; init; }

    /// <summary>Human-readable failure detail when the preflight did not fully succeed.</summary>
    public string? FailureReason { get; init; }

    /// <summary>UTC timestamp when the preflight completed.</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Elapsed wall time for the preflight attempt.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Builds a succeeded/incomplete result from a connected device snapshot.
    /// </summary>
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

    /// <summary>
    /// Returns a failure reason when identity, version, or architecture is missing; otherwise null.
    /// </summary>
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

    /// <summary>
    /// Returns null for null or whitespace-only input; otherwise the value unchanged.
    /// </summary>
    /// <param name="value">Text to normalise.</param>
    /// <returns>The original text, or null.</returns>
    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
