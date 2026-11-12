// -----------------------------------------------------------------------------
// File: DeviceBackupService.cs
//
// Description:
// Creates pre-upgrade .rsc + .backup on the router, downloads them via SFTP to
// Backups/Exports and Backups/Binary, writes a SHA-256 JSON manifest, then best-
// effort removes owned remote leftovers. Stem allocation happens before any remote commands.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Serialization;
using tik4net;

namespace MikrotikWorker.Backup;

/// <summary>
/// Creates remote export/binary backups, downloads them centrally, and writes a manifest.
/// </summary>
internal interface IDeviceBackupService
{
    /// <summary>
    /// Creates and retrieves a pre-upgrade backup for <paramref name="job"/> using an open API session.
    /// </summary>
    Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        string username,
        string password,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production backup orchestration over RouterOS API + SFTP file transfer.
/// </summary>
internal sealed class DeviceBackupService(
    IMikrotikRouterOperations routerOperations,
    IMikrotikFileTransferClient fileTransferClient,
    IOptions<FirmwareUpgradeOptions> upgradeOptions,
    TimeProvider timeProvider,
    ILogger<DeviceBackupService> logger) : IDeviceBackupService
{
    /// <summary>Max local stem reallocations when reserved paths collide before remote commands.</summary>
    private const int MaxAllocationAttempts = 8;

    /// <inheritdoc />
    public async Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(deviceInfo);

        FirmwareUpgradeOptions options = upgradeOptions.Value;
        DateTimeOffset stamp = timeProvider.GetUtcNow();

        string backupRoot = options.GetAbsoluteBackupDirectory();
        string exportsDir = Path.Combine(backupRoot, "Exports");
        string binaryDir = Path.Combine(backupRoot, "Binary");
        string manifestsDir = Path.Combine(backupRoot, "Manifests");

        BackupStemReservation? reservation = null;
        string? exportLocal = null;
        string? binaryLocal = null;
        string? manifestPath = null;
        List<string> ownedRemoteFiles = [];
        bool centralVerified = false;

        try
        {
            // Allocate (and re-allocate on local collision) before any remote backup commands.
            for (int attempt = 1; attempt <= MaxAllocationAttempts; attempt++)
            {
                reservation?.Release();
                reservation = BackupFileNaming.AllocateUniqueBaseName(
                    deviceInfo.Identity,
                    job.RouterIp,
                    stamp,
                    exportsDir,
                    binaryDir,
                    manifestsDir);

                if (LocalArtefactPathsAreFree(reservation))
                {
                    break;
                }

                logger.LogWarning(
                    "Backup stem collision before remote commands; allocating a new stem (attempt {Attempt}/{MaxAttempts})",
                    attempt,
                    MaxAllocationAttempts);
                reservation.Release();
                reservation = null;
                if (attempt == MaxAllocationAttempts)
                {
                    return Fail(
                        "Unable to allocate a free local backup stem before remote backup commands.",
                        exportLocal,
                        binaryLocal,
                        manifestPath);
                }
            }

            BackupStemReservation active = reservation!;
            string baseName = active.Stem;
            string exportRemote = active.ExportFileName;
            string binaryRemote = active.BinaryFileName;
            exportLocal = active.ExportLocalPath;
            binaryLocal = active.BinaryLocalPath;
            manifestPath = active.ManifestLocalPath;

            logger.LogInformation(
                "Backup started (base={BackupBaseName}, export={RemoteExport}, binary={RemoteBinary})",
                baseName,
                exportRemote,
                binaryRemote);

            cancellationToken.ThrowIfCancellationRequested();

            // --- Export ---
            logger.LogInformation("Creating RouterOS export");
            await routerOperations
                .ExportConfigurationToFileAsync(connection, baseName, cancellationToken)
                .ConfigureAwait(false);
            await routerOperations
                .WaitForFileAsync(
                    connection,
                    exportRemote,
                    options.BackupFileReadyTimeout,
                    options.BackupFileReadyPollInterval,
                    cancellationToken)
                .ConfigureAwait(false);

            RouterFileInfo exportRemoteInfo = (await routerOperations
                .FindFileAsync(connection, exportRemote, cancellationToken)
                .ConfigureAwait(false))!;
            if (exportRemoteInfo.SizeBytes <= 0)
            {
                return FailRetain(
                    "Export file on router was empty.",
                    job,
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    ownedRemoteFiles);
            }

            ownedRemoteFiles.Add(exportRemote);
            logger.LogInformation(
                "Remote backup creation completed for export ({RemoteFile}, {SizeBytes} bytes)",
                exportRemote,
                exportRemoteInfo.SizeBytes);

            logger.LogInformation("Central download started ({RemoteFile})", exportRemote);
            await fileTransferClient
                .DownloadFileAsync(
                    job.RouterIp.ToString(),
                    options.ScpPort,
                    username,
                    password,
                    exportRemote,
                    exportLocal,
                    options.ScpTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            FileInfo exportInfo = new(exportLocal);
            if (!exportInfo.Exists || exportInfo.Length <= 0)
            {
                return FailRetain(
                    "Central export file missing or empty after transfer.",
                    job,
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    ownedRemoteFiles);
            }

            if (exportInfo.Length != exportRemoteInfo.SizeBytes)
            {
                return FailRetain(
                    $"Export size mismatch (router={exportRemoteInfo.SizeBytes}, local={exportInfo.Length}).",
                    job,
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    ownedRemoteFiles);
            }

            string exportSha = ComputeSha256Hex(exportLocal);
            logger.LogInformation(
                "Export verified ({LocalFile}, {SizeBytes} bytes, sha256={Sha256})",
                Path.GetFileName(exportLocal),
                exportInfo.Length,
                exportSha);

            // --- Binary backup ---
            logger.LogInformation("Creating binary backup");
            string? backupPassword = string.IsNullOrEmpty(options.BackupPassword) ? null : options.BackupPassword;
            await routerOperations
                .SaveSystemBackupAsync(connection, baseName, backupPassword, cancellationToken)
                .ConfigureAwait(false);
            await routerOperations
                .WaitForFileAsync(
                    connection,
                    binaryRemote,
                    options.BackupFileReadyTimeout,
                    options.BackupFileReadyPollInterval,
                    cancellationToken)
                .ConfigureAwait(false);

            RouterFileInfo binaryRemoteInfo = (await routerOperations
                .FindFileAsync(connection, binaryRemote, cancellationToken)
                .ConfigureAwait(false))!;
            if (binaryRemoteInfo.SizeBytes <= 0)
            {
                return FailRetain(
                    "Binary backup on router was empty.",
                    job,
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    ownedRemoteFiles);
            }

            ownedRemoteFiles.Add(binaryRemote);
            logger.LogInformation(
                "Remote backup creation completed for binary ({RemoteFile}, {SizeBytes} bytes)",
                binaryRemote,
                binaryRemoteInfo.SizeBytes);

            logger.LogInformation("Central download started ({RemoteFile})", binaryRemote);
            await fileTransferClient
                .DownloadFileAsync(
                    job.RouterIp.ToString(),
                    options.ScpPort,
                    username,
                    password,
                    binaryRemote,
                    binaryLocal,
                    options.ScpTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            FileInfo binaryInfo = new(binaryLocal);
            if (!binaryInfo.Exists || binaryInfo.Length <= 0)
            {
                return FailRetain(
                    "Central binary backup missing or empty after transfer.",
                    job,
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    ownedRemoteFiles);
            }

            if (binaryInfo.Length != binaryRemoteInfo.SizeBytes)
            {
                return FailRetain(
                    $"Binary backup size mismatch (router={binaryRemoteInfo.SizeBytes}, local={binaryInfo.Length}).",
                    job,
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    ownedRemoteFiles);
            }

            string binarySha = ComputeSha256Hex(binaryLocal);
            logger.LogInformation(
                "Binary backup verified ({LocalFile}, {SizeBytes} bytes, sha256={Sha256})",
                Path.GetFileName(binaryLocal),
                binaryInfo.Length,
                binarySha);

            BackupManifest manifest = new()
            {
                JobId = job.JobId,
                ImportId = job.ImportId,
                RouterName = job.RouterName,
                RouterIp = job.RouterIp.ToString(),
                RouterIdentity = deviceInfo.Identity,
                RouterOsVersionBeforeUpgrade = deviceInfo.Version,
                Architecture = deviceInfo.ArchitectureName,
                BoardName = deviceInfo.BoardName,
                RouterboardModel = deviceInfo.RouterboardModel,
                BackupTimestampUtc = stamp,
                Purpose = "pre-upgrade",
                ExportFileName = Path.GetFileName(exportLocal),
                ExportSizeBytes = exportInfo.Length,
                ExportSha256 = exportSha,
                BinaryBackupFileName = Path.GetFileName(binaryLocal),
                BinaryBackupSizeBytes = binaryInfo.Length,
                BinaryBackupSha256 = binarySha,
                BackupCompletionStatus = "CentralVerified",
                RemoteExportFileName = exportRemote,
                RemoteBinaryFileName = binaryRemote,
            };

            await WriteManifestCreateNewAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length <= 0)
            {
                return FailRetain(
                    "Manifest write failed or produced an empty file.",
                    job,
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    ownedRemoteFiles);
            }

            centralVerified = true;
            logger.LogInformation("Backup manifest created ({ManifestFile})", Path.GetFileName(manifestPath));
            logger.LogInformation("Central verification succeeded");

            // Remote cleanup only after central verification. Caller cancellation must not
            // prevent a bounded cleanup attempt once the central copies are verified.
            DeviceBackupResult? cleanupFailure = await TryCleanupOwnedRemoteArtefactsAsync(
                    connection,
                    job,
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    ownedRemoteFiles,
                    options.ScpTimeout)
                .ConfigureAwait(false);
            if (cleanupFailure is not null)
            {
                // Cleanup outcome was logged and captured on the failure result. Caller cancellation
                // still wins: do not return a continuable backup result that could advance OSPF.
                if (cancellationToken.IsCancellationRequested)
                {
                    using (DeviceLogging.BeginServiceOnly())
                    {
                        logger.LogWarning(
                            "Caller cancelled after central verification; remote cleanup failed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RemainingRemoteFiles={RemainingRemoteFiles}",
                            job.ImportId,
                            job.JobId,
                            job.RouterIp,
                            string.Join(", ", cleanupFailure.RemainingRemoteFiles));
                    }

                    throw new OperationCanceledException(cancellationToken);
                }

                return cleanupFailure;
            }

            BackupManifest finalManifest = manifest with { BackupCompletionStatus = "SucceededWithRemoteCleanup" };
            // Final manifest update is part of the post-verify cleanup window; use a bounded token
            // so caller cancellation after verification cannot leave status stuck at CentralVerified
            // when remotes were already removed.
            using (CancellationTokenSource manifestCts = new(options.ScpTimeout))
            {
                await File.WriteAllTextAsync(
                        manifestPath,
                        JsonSerializer.Serialize(finalManifest, BackupManifestJsonContext.Default.BackupManifest),
                        manifestCts.Token)
                    .ConfigureAwait(false);
            }

            active.Complete();
            reservation = null;
            ownedRemoteFiles.Clear();

            if (cancellationToken.IsCancellationRequested)
            {
                using (DeviceLogging.BeginServiceOnly())
                {
                    logger.LogWarning(
                        "Caller cancelled after central verification; remote cleanup completed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
                        job.ImportId,
                        job.JobId,
                        job.RouterIp);
                }

                throw new OperationCanceledException(cancellationToken);
            }

            return new DeviceBackupResult
            {
                Succeeded = true,
                ExportLocalPath = exportLocal,
                BinaryLocalPath = binaryLocal,
                ManifestLocalPath = manifestPath,
                Manifest = finalManifest,
                RemoteCleanupSucceeded = true,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!centralVerified && ownedRemoteFiles.Count > 0)
            {
                logger.LogWarning(
                    "Remote artefact intentionally retained because central verification was not completed before cancellation. RemainingRemoteFiles={RemainingRemoteFiles}",
                    string.Join(", ", ownedRemoteFiles));
            }

            using (DeviceLogging.BeginServiceOnly())
            {
                logger.LogWarning(
                    "Backup cancelled. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, BackupPrerequisite=Failed",
                    job.ImportId,
                    job.JobId,
                    job.RouterIp);
            }

            logger.LogWarning("Backup cancelled");
            throw;
        }
        catch (Exception ex)
        {
            if (ownedRemoteFiles.Count > 0 && !centralVerified)
            {
                logger.LogWarning(
                    "Remote artefact intentionally retained because central verification failed. RemainingRemoteFiles={RemainingRemoteFiles}, Reason={Reason}",
                    string.Join(", ", ownedRemoteFiles),
                    ex.Message);
            }

            using (DeviceLogging.BeginServiceOnly())
            {
                logger.LogError(
                    ex,
                    "Backup failed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, BackupPrerequisite=Failed",
                    job.ImportId,
                    job.JobId,
                    job.RouterIp);
            }

            logger.LogError(ex, "Backup failed");
            return Fail(
                ex.Message,
                exportLocal,
                binaryLocal,
                manifestPath,
                remainingRemoteFiles: ownedRemoteFiles.Count > 0 ? [.. ownedRemoteFiles] : null);
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    /// <summary>
    /// Removes owned remote artefacts under a bounded timeout and verifies they are gone.
    /// Returns a failed <see cref="DeviceBackupResult"/> when cleanup fails; otherwise null.
    /// </summary>
    private async Task<DeviceBackupResult?> TryCleanupOwnedRemoteArtefactsAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string? exportLocal,
        string? binaryLocal,
        string? manifestPath,
        List<string> ownedRemoteFiles,
        TimeSpan cleanupTimeout)
    {
        logger.LogInformation(
            "Remote backup cleanup started ({RemoteFiles})",
            string.Join(", ", ownedRemoteFiles));

        using CancellationTokenSource cleanupCts = new(cleanupTimeout);
        CancellationToken cleanupToken = cleanupCts.Token;

        try
        {
            foreach (string remoteFile in ownedRemoteFiles)
            {
                await routerOperations.RemoveFileAsync(connection, remoteFile, cleanupToken)
                    .ConfigureAwait(false);
            }

            List<string> remaining = [];
            foreach (string remoteFile in ownedRemoteFiles)
            {
                RouterFileInfo? stillThere = await routerOperations
                    .FindFileAsync(connection, remoteFile, cleanupToken)
                    .ConfigureAwait(false);
                if (stillThere is not null)
                {
                    remaining.Add(remoteFile);
                }
            }

            if (remaining.Count > 0)
            {
                logger.LogError(
                    "Remote cleanup verification failed; still present: {RemainingRemoteFiles}",
                    string.Join(", ", remaining));
                using (DeviceLogging.BeginServiceOnly())
                {
                    logger.LogError(
                        "Remote cleanup verification failed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RemainingRemoteFiles={RemainingRemoteFiles}",
                        job.ImportId,
                        job.JobId,
                        job.RouterIp,
                        string.Join(", ", remaining));
                }

                return Fail(
                    $"Remote cleanup verification failed; still present: {string.Join(", ", remaining)}",
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    remoteCleanupFailed: true,
                    remainingRemoteFiles: remaining);
            }
        }
        catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
        {
            logger.LogError(
                "Remote cleanup failed (bounded cleanup cancelled or timed out). RemainingRemoteFiles={RemainingRemoteFiles}",
                string.Join(", ", ownedRemoteFiles));
            using (DeviceLogging.BeginServiceOnly())
            {
                logger.LogError(
                    "Remote cleanup timed out. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RemainingRemoteFiles={RemainingRemoteFiles}",
                    job.ImportId,
                    job.JobId,
                    job.RouterIp,
                    string.Join(", ", ownedRemoteFiles));
            }

            return Fail(
                $"Remote cleanup failed after central verification: cleanup timed out after {cleanupTimeout}. Remaining remote files: {string.Join(", ", ownedRemoteFiles)}",
                exportLocal,
                binaryLocal,
                manifestPath,
                remoteCleanupFailed: true,
                remainingRemoteFiles: [.. ownedRemoteFiles]);
        }
        catch (Exception cleanupEx)
        {
            logger.LogError(
                cleanupEx,
                "Remote cleanup failed. RemainingRemoteFiles={RemainingRemoteFiles}",
                string.Join(", ", ownedRemoteFiles));
            using (DeviceLogging.BeginServiceOnly())
            {
                logger.LogError(
                    cleanupEx,
                    "Remote cleanup failed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RemainingRemoteFiles={RemainingRemoteFiles}",
                    job.ImportId,
                    job.JobId,
                    job.RouterIp,
                    string.Join(", ", ownedRemoteFiles));
            }

            return Fail(
                $"Remote cleanup failed after central verification: {cleanupEx.Message}. Remaining remote files: {string.Join(", ", ownedRemoteFiles)}",
                exportLocal,
                binaryLocal,
                manifestPath,
                remoteCleanupFailed: true,
                remainingRemoteFiles: [.. ownedRemoteFiles]);
        }

        logger.LogInformation("Remote backup cleanup succeeded");
        return null;
    }

    /// <summary>
    /// Logs intentional remote retention after failed central verification, then returns <see cref="Fail"/>.
    /// </summary>
    private DeviceBackupResult FailRetain(
        string reason,
        MikrotikScheduledJob job,
        string? exportLocal,
        string? binaryLocal,
        string? manifestPath,
        IReadOnlyList<string> ownedRemoteFiles)
    {
        if (ownedRemoteFiles.Count > 0)
        {
            logger.LogWarning(
                "Remote artefact intentionally retained because central verification failed. RemainingRemoteFiles={RemainingRemoteFiles}, Reason={Reason}",
                string.Join(", ", ownedRemoteFiles),
                reason);
            using (DeviceLogging.BeginServiceOnly())
            {
                logger.LogWarning(
                    "Remote artefact retained. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RemainingRemoteFiles={RemainingRemoteFiles}, Reason={Reason}",
                    job.ImportId,
                    job.JobId,
                    job.RouterIp,
                    string.Join(", ", ownedRemoteFiles),
                    reason);
            }
        }

        return Fail(
            reason,
            exportLocal,
            binaryLocal,
            manifestPath,
            remainingRemoteFiles: ownedRemoteFiles.Count > 0 ? ownedRemoteFiles : null);
    }

    /// <summary>True when none of the reserved export/binary/manifest paths already exist on disk.</summary>
    private static bool LocalArtefactPathsAreFree(BackupStemReservation reservation) =>
        !File.Exists(reservation.ExportLocalPath)
        && !File.Exists(reservation.BinaryLocalPath)
        && !File.Exists(reservation.ManifestLocalPath);

    /// <summary>Writes the manifest with <see cref="FileMode.CreateNew"/> so stem collisions fail loudly.</summary>
    private static async Task WriteManifestCreateNewAsync(
        string manifestPath,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            manifestPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        await JsonSerializer
            .SerializeAsync(stream, manifest, BackupManifestJsonContext.Default.BackupManifest, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds a failed <see cref="DeviceBackupResult"/> preserving any local paths and remote leftovers.</summary>
    private static DeviceBackupResult Fail(
        string reason,
        string? exportLocal,
        string? binaryLocal,
        string? manifestPath,
        bool remoteCleanupFailed = false,
        IReadOnlyList<string>? remainingRemoteFiles = null) =>
        new()
        {
            Succeeded = false,
            FailureReason = reason,
            ExportLocalPath = exportLocal,
            BinaryLocalPath = binaryLocal,
            ManifestLocalPath = manifestPath,
            RemoteCleanupSucceeded = false,
            RemoteCleanupFailed = remoteCleanupFailed,
            RemainingRemoteFiles = remainingRemoteFiles ?? [],
        };

    /// <summary>Lowercase hex SHA-256 of a local file.</summary>
    private static string ComputeSha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
