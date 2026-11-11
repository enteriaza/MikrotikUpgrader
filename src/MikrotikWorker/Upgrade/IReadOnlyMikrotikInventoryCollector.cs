namespace MikrotikWorker.Upgrade;

/// <summary>
/// Strictly read-only RouterOS inventory collector for live package-compatibility audits.
/// Must not depend on upgrade, backup, fetch, or reboot services.
/// </summary>
internal interface IReadOnlyMikrotikInventoryCollector
{
    Task<ReadOnlyInventoryCollectionResult> CollectAsync(
        string routerIp,
        string username,
        string password,
        int port,
        TimeSpan connectionTimeout,
        string? routerName = null,
        CancellationToken cancellationToken = default);
}
