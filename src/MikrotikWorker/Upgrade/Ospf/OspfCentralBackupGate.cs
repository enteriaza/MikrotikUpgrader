// -----------------------------------------------------------------------------
// File: OspfCentralBackupGate.cs
//
// Description:
// Shared gate ensuring OSPF mutation never begins without a centrally verified
// backup that belongs to the intended router.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Backup;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Central-backup prerequisite shared by production and lab OSPF mutation entry points.
/// </summary>
internal static class OspfCentralBackupGate
{
    /// <summary>
    /// True when central backup artefacts were verified. Remote cleanup failure after verification
    /// still qualifies — cleanup status must not block OSPF when central copies are good.
    /// </summary>
    public static bool IsCentralBackupVerified(DeviceBackupResult backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return backup.Succeeded || backup.RemoteCleanupFailed;
    }

    /// <summary>
    /// Throws when <paramref name="backup"/> is not centrally verified.
    /// </summary>
    public static void EnsureVerifiedOrThrow(DeviceBackupResult backup, string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

        if (IsCentralBackupVerified(backup))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{entryPoint}: refusing OSPF mutation — central backup was not verified. "
            + (backup.FailureReason ?? "No failure reason supplied."));
    }

    /// <summary>
    /// Throws when the verified backup manifest does not belong to the intended router.
    /// </summary>
    public static void EnsureBelongsToRouterOrThrow(
        DeviceBackupResult backup,
        string expectedRouterIp,
        string expectedIdentity,
        string entryPoint)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRouterIp);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

        if (backup.Manifest is null)
        {
            throw new InvalidOperationException(
                $"{entryPoint}: refusing OSPF mutation — verified backup has no manifest to confirm "
                + "router ownership.");
        }

        BackupManifest manifest = backup.Manifest;
        if (!string.Equals(manifest.RouterIp, expectedRouterIp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{entryPoint}: refusing OSPF mutation — backup RouterIp '{manifest.RouterIp}' does not "
                + $"match intended router '{expectedRouterIp}'.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.RouterIdentity)
            && !string.Equals(manifest.RouterIdentity, expectedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{entryPoint}: refusing OSPF mutation — backup RouterIdentity '{manifest.RouterIdentity}' "
                + $"does not match intended identity '{expectedIdentity}'.");
        }
    }

    /// <summary>
    /// Creates the pre-OSPF backup, verifies central status, confirms router ownership, and records
    /// ordered events for tests. The first OSPF mutation must not occur before this returns.
    /// </summary>
    public static async Task EnsureCentralBackupBeforeMutationAsync(
        IDeviceBackupService backups,
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        string username,
        string password,
        string expectedRouterIp,
        string expectedIdentity,
        string entryPoint,
        IList<string>? operationOrder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(deviceInfo);

        operationOrder?.Add("backup-start");
        DeviceBackupResult backup = await backups
            .CreatePreUpgradeBackupAsync(
                connection,
                job,
                deviceInfo,
                username,
                password,
                cancellationToken)
            .ConfigureAwait(false);

        if (IsCentralBackupVerified(backup))
        {
            operationOrder?.Add("central-backup-verified");
        }
        else
        {
            operationOrder?.Add("central-backup-failed");
            EnsureVerifiedOrThrow(backup, entryPoint);
        }

        EnsureBelongsToRouterOrThrow(backup, expectedRouterIp, expectedIdentity, entryPoint);
        operationOrder?.Add("central-backup-router-confirmed");
    }
}
