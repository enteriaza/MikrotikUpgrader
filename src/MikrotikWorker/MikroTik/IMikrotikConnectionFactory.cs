// -----------------------------------------------------------------------------
// File: IMikrotikConnectionFactory.cs
//
// Description:
// Abstraction over tik4net connection open for a single device. Callers own
// disposal of the returned ITikConnection.
//
// -----------------------------------------------------------------------------

using tik4net;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Opens a short-lived native RouterOS API connection for a single device.
/// </summary>
internal interface IMikrotikConnectionFactory
{
    /// <summary>
    /// Validates <paramref name="connection"/> and opens a tik4net API session.
    /// </summary>
    Task<ITikConnection> CreateAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken);
}
