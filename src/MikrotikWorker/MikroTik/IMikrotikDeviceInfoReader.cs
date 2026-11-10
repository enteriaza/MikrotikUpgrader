using tik4net;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Reads device information from an already-open RouterOS API connection.
/// </summary>
internal interface IMikrotikDeviceInfoReader
{
    Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
        ITikConnection connection,
        string host,
        CancellationToken cancellationToken = default);
}
