// -----------------------------------------------------------------------------
// File: IMikrotikApiClient.cs
//
// Description:
// Public application-facing contract for short-lived RouterOS API queries that
// open their own connection (device info). Upgrade workflows use the richer
// IMikrotikRouterOperations on an already-open connection instead.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Application-facing MikroTik RouterOS API client.
/// </summary>
public interface IMikrotikApiClient
{
    /// <summary>
    /// Opens a connection using <paramref name="connection"/>, reads device info, then disconnects.
    /// </summary>
    Task<MikrotikDeviceInfo> GetDeviceInfoAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken = default);
}
