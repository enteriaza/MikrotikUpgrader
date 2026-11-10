using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
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
        string baseName = BackupFileNaming.BuildBaseName(job.RouterName, job.RouterIp, stamp);
        string exportRemote = baseName + ".rsc";
        string binaryRemote = baseName + ".backup";

        string backupRoot = options.GetAbsoluteBackupDirectory();
        string exportsDir = Path.Combine(backupRoot, "Exports");
        string binaryDir = Path.Combine(backupRoot, "Binary");
        string manifestsDir = Path.Combine(backupRoot, "Manifests");
        Directory.CreateDirectory(exportsDir);
        Directory.CreateDirectory(binaryDir);
        Directory.CreateDirectory(manifestsDir);

        string? exportLocal = null;
        string? binaryLocal = null;

        logger.LogInformation(
            "Backup operation started. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, RouterIdentity={RouterIdentity}, BackupBaseName={BackupBaseName}",
            job.ImportId,
            job.JobId,
            job.RouterName,
            job.RouterIp,
            deviceInfo.Identity,
            baseName);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Export command issued. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RemoteFile={RemoteFile}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                exportRemote);
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
            logger.LogInformation(
                "Export completed on router. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RemoteFile={RemoteFile}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                exportRemote);

            logger.LogInformation(
                "Binary backup creation started. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RemoteFile={RemoteFile}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                binaryRemote);
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
            logger.LogInformation(
                "Binary backup creation completed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RemoteFile={RemoteFile}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                binaryRemote);

            RouterFileInfo exportRemoteInfo = (await routerOperations
                .FindFileAsync(connection, exportRemote, cancellationToken)
                .ConfigureAwait(false))!;
            RouterFileInfo binaryRemoteInfo = (await routerOperations
                .FindFileAsync(connection, binaryRemote, cancellationToken)
                .ConfigureAwait(false))!;

            if (exportRemoteInfo.SizeBytes <= 0 || binaryRemoteInfo.SizeBytes <= 0)
            {
                return Fail("Backup file on router was empty.", exportLocal, binaryLocal);
            }

            exportLocal = BackupFileNaming.EnsureUniquePath(exportsDir, exportRemote);
            binaryLocal = BackupFileNaming.EnsureUniquePath(binaryDir, binaryRemote);

            logger.LogInformation(
                "Binary backup retrieval started via SCP. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
                job.ImportId,
                job.JobId,
                job.RouterIp);

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
            logger.LogInformation(
                "Export retrieved. ImportId={ImportId}, JobId={JobId}, LocalFile={LocalFile}, SizeBytes={SizeBytes}",
                job.ImportId,
                job.JobId,
                Path.GetFileName(exportLocal),
                new FileInfo(exportLocal).Length);

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
            logger.LogInformation(
                "Binary backup retrieved. ImportId={ImportId}, JobId={JobId}, LocalFile={LocalFile}, SizeBytes={SizeBytes}",
                job.ImportId,
                job.JobId,
                Path.GetFileName(binaryLocal),
                new FileInfo(binaryLocal).Length);

            FileInfo exportInfo = new(exportLocal);
            FileInfo binaryInfo = new(binaryLocal);
            if (!exportInfo.Exists || exportInfo.Length <= 0)
            {
                return Fail("Central export file missing or empty.", exportLocal, binaryLocal);
            }

            if (!binaryInfo.Exists || binaryInfo.Length <= 0)
            {
                return Fail("Central binary backup missing or empty.", exportLocal, binaryLocal);
            }

            if (exportInfo.Length != exportRemoteInfo.SizeBytes)
            {
                return Fail(
                    $"Export size mismatch (router={exportRemoteInfo.SizeBytes}, local={exportInfo.Length}).",
                    exportLocal,
                    binaryLocal);
            }

            if (binaryInfo.Length != binaryRemoteInfo.SizeBytes)
            {
                return Fail(
                    $"Binary backup size mismatch (router={binaryRemoteInfo.SizeBytes}, local={binaryInfo.Length}).",
                    exportLocal,
                    binaryLocal);
            }

            string exportSha = ComputeSha256Hex(exportLocal);
            string binarySha = ComputeSha256Hex(binaryLocal);
            logger.LogInformation(
                "Central file verification and SHA-256 calculation completed. ImportId={ImportId}, JobId={JobId}, ExportSha256={ExportSha256}, BinarySha256={BinarySha256}",
                job.ImportId,
                job.JobId,
                exportSha,
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
                BackupCompletionStatus = "Succeeded",
            };

            string manifestPath = BackupFileNaming.EnsureUniquePath(manifestsDir, baseName + ".json");
            await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(manifest, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Backup manifest created. ImportId={ImportId}, JobId={JobId}, ManifestFile={ManifestFile}, BackupPrerequisite=Passed",
                job.ImportId,
                job.JobId,
                Path.GetFileName(manifestPath));

            return new DeviceBackupResult
            {
                Succeeded = true,
                ExportLocalPath = exportLocal,
                BinaryLocalPath = binaryLocal,
                ManifestLocalPath = manifestPath,
                Manifest = manifest,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Backup cancelled. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, BackupPrerequisite=Failed",
                job.ImportId,
                job.JobId,
                job.RouterIp);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Backup failed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, BackupPrerequisite=Failed",
                job.ImportId,
                job.JobId,
                job.RouterIp);
            return Fail(ex.Message, exportLocal, binaryLocal);
        }
    }

    private static DeviceBackupResult Fail(string reason, string? exportLocal, string? binaryLocal) =>
        new()
        {
            Succeeded = false,
            FailureReason = reason,
            ExportLocalPath = exportLocal,
            BinaryLocalPath = binaryLocal,
        };

    private static string ComputeSha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
