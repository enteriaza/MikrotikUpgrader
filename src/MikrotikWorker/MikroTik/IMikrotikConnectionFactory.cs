using tik4net;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Opens a short-lived native RouterOS API connection for a single device.
/// </summary>
internal interface IMikrotikConnectionFactory
{
    Task<ITikConnection> CreateAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken);
}
