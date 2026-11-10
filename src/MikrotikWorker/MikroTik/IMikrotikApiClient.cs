namespace MikrotikWorker.MikroTik;

/// <summary>
/// Application-facing MikroTik RouterOS API client.
/// </summary>
public interface IMikrotikApiClient
{
    Task<MikrotikDeviceInfo> GetDeviceInfoAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken = default);
}
