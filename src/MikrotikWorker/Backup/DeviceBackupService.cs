using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;

namespace MikrotikWorker.Backup;

internal interface IDeviceBackupService
{
    Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        string username,
        string password,
        CancellationToken cancellationToken);
}

internal sealed class DeviceBackupService(
    IMikrotikRouterOperations routerOperations,
    IMikrotikFileTransferClient fileTransferClient,
    IOptions<FirmwareUpgradeOptions> upgradeOptions,
    TimeProvider timeProvider,
    ILogger<DeviceBackupService> logger) : IDeviceBackupService
{
    private const int MaxAllocationAttempts = 8;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
                return Fail("Export file on router was empty.", exportLocal, binaryLocal, manifestPath);
            }

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
                return Fail("Central export file missing or empty after transfer.", exportLocal, binaryLocal, manifestPath);
            }

            if (exportInfo.Length != exportRemoteInfo.SizeBytes)
            {
                return Fail(
                    $"Export size mismatch (router={exportRemoteInfo.SizeBytes}, local={exportInfo.Length}).",
                    exportLocal,
                    binaryLocal,
                    manifestPath);
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
                return Fail("Binary backup on router was empty.", exportLocal, binaryLocal, manifestPath);
            }

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
                return Fail("Central binary backup missing or empty after transfer.", exportLocal, binaryLocal, manifestPath);
            }

            if (binaryInfo.Length != binaryRemoteInfo.SizeBytes)
            {
                return Fail(
                    $"Binary backup size mismatch (router={binaryRemoteInfo.SizeBytes}, local={binaryInfo.Length}).",
                    exportLocal,
                    binaryLocal,
                    manifestPath);
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
                return Fail("Manifest write failed or produced an empty file.", exportLocal, binaryLocal, manifestPath);
            }

            logger.LogInformation("Backup manifest created ({ManifestFile})", Path.GetFileName(manifestPath));

            // --- Remote cleanup only after central verification ---
            logger.LogInformation("Remote backup cleanup started");

            try
            {
                await routerOperations.RemoveFileAsync(connection, exportRemote, cancellationToken)
                    .ConfigureAwait(false);
                await routerOperations.RemoveFileAsync(connection, binaryRemote, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Backup cancelled during remote cleanup; central backups retained. Remaining: {RemainingRemoteFiles}",
                    $"{exportRemote}, {binaryRemote}");
                throw;
            }
            catch (Exception cleanupEx)
            {
                logger.LogError(
                    cleanupEx,
                    "Remote backup cleanup failed. Remaining: {RemainingRemoteFiles}",
                    $"{exportRemote}, {binaryRemote}");
                return Fail(
                    $"Remote cleanup failed after central verification: {cleanupEx.Message}. Remaining remote files: {exportRemote}, {binaryRemote}",
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    remoteCleanupFailed: true,
                    remainingRemoteFiles: [exportRemote, binaryRemote]);
            }

            RouterFileInfo? exportStillThere = await routerOperations
                .FindFileAsync(connection, exportRemote, cancellationToken)
                .ConfigureAwait(false);
            RouterFileInfo? binaryStillThere = await routerOperations
                .FindFileAsync(connection, binaryRemote, cancellationToken)
                .ConfigureAwait(false);
            if (exportStillThere is not null || binaryStillThere is not null)
            {
                List<string> remaining = [];
                if (exportStillThere is not null)
                {
                    remaining.Add(exportRemote);
                }

                if (binaryStillThere is not null)
                {
                    remaining.Add(binaryRemote);
                }

                logger.LogError(
                    "Remote cleanup verification failed; still present: {RemainingRemoteFiles}",
                    string.Join(", ", remaining));
                return Fail(
                    $"Remote cleanup verification failed; still present: {string.Join(", ", remaining)}",
                    exportLocal,
                    binaryLocal,
                    manifestPath,
                    remoteCleanupFailed: true,
                    remainingRemoteFiles: remaining);
            }

            logger.LogInformation("Remote backup cleanup succeeded");

            BackupManifest finalManifest = manifest with { BackupCompletionStatus = "SucceededWithRemoteCleanup" };
            await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(finalManifest, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);

            active.Complete();
            reservation = null;

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
            return Fail(ex.Message, exportLocal, binaryLocal, manifestPath);
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    private static bool LocalArtefactPathsAreFree(BackupStemReservation reservation) =>
        !File.Exists(reservation.ExportLocalPath)
        && !File.Exists(reservation.BinaryLocalPath)
        && !File.Exists(reservation.ManifestLocalPath);

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
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

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

    private static string ComputeSha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
