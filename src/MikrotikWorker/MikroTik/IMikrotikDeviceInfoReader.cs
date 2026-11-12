// -----------------------------------------------------------------------------
// File: IMikrotikDeviceInfoReader.cs
//
// Description:
// Reads identity, version, architecture, and routerboard fields from an already-
// open RouterOS API connection without opening a second session.
//
// -----------------------------------------------------------------------------

using tik4net;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Reads device information from an already-open RouterOS API connection.
/// </summary>
internal interface IMikrotikDeviceInfoReader
{
    /// <summary>
    /// Collects <see cref="MikrotikDeviceInfo"/> for <paramref name="host"/> using <paramref name="connection"/>.
    /// </summary>
    Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
        ITikConnection connection,
        string host,
        CancellationToken cancellationToken = default);
}
