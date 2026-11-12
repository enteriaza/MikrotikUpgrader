// -----------------------------------------------------------------------------
// File: IReadOnlyMikrotikInventoryCollector.cs
//
// Description:
// Strictly read-only RouterOS inventory collector for live
// package-compatibility audits. Must not depend on upgrade, backup, fetch, or
// reboot services.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Strictly read-only RouterOS inventory collector for live package-compatibility audits.
/// Must not depend on upgrade, backup, fetch, or reboot services.
/// </summary>
internal interface IReadOnlyMikrotikInventoryCollector
{
    /// <summary>
    /// Opens a short-lived API session and collects identity, resource, package, and optional
    /// routerboard print data without issuing mutating commands.
    /// </summary>
    /// <param name="routerIp">Router management address.</param>
    /// <param name="username">API username from configuration (never from CSV).</param>
    /// <param name="password">API password from configuration (never from CSV).</param>
    /// <param name="port">API port.</param>
    /// <param name="connectionTimeout">Connect timeout for the session.</param>
    /// <param name="routerName">Optional display name from the audit CSV.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ReadOnlyInventoryCollectionResult> CollectAsync(
        string routerIp,
        string username,
        string password,
        int port,
        TimeSpan connectionTimeout,
        string? routerName = null,
        CancellationToken cancellationToken = default);
}
