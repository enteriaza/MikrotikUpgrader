using tik4net;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// RouterOS operations used by pre-flight, backup, and v6 upgrade workflows.
/// </summary>
internal interface IMikrotikRouterOperations : IMikrotikDeviceInfoReader
{
    Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default);

    Task ExportConfigurationToFileAsync(
        ITikConnection connection,
        string fileNameWithoutExtension,
        CancellationToken cancellationToken = default);

    Task SaveSystemBackupAsync(
        ITikConnection connection,
        string fileNameWithoutExtension,
        string? backupPassword,
        CancellationToken cancellationToken = default);

    Task<RouterFileInfo?> FindFileAsync(
        ITikConnection connection,
        string fileName,
        CancellationToken cancellationToken = default);

    Task WaitForFileAsync(
        ITikConnection connection,
        string fileName,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a single file by exact name. Never uses wildcards.
    /// </summary>
    Task RemoveFileAsync(
        ITikConnection connection,
        string fileName,
        CancellationToken cancellationToken = default);

    Task FetchUrlToFileAsync(
        ITikConnection connection,
        string url,
        string destinationFileName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task RebootAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default);
}

internal interface IMikrotikFileTransferClient
{
    Task DownloadFileAsync(
        string host,
        int port,
        string username,
        string password,
        string remoteFileName,
        string localFilePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
