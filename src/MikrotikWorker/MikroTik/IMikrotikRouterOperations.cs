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

    /// <summary>
    /// Counts <c>/system/script</c> objects with the exact name.
    /// </summary>
    Task<int> CountSystemScriptsByNameAsync(
        ITikConnection connection,
        string scriptName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support CountSystemScriptsByNameAsync.");

    /// <summary>
    /// Removes every <c>/system/script</c> object with the exact name, each by its RouterOS <c>.id</c>.
    /// </summary>
    Task RemoveSystemScriptsByNameAsync(
        ITikConnection connection,
        string scriptName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support RemoveSystemScriptsByNameAsync.");

    /// <summary>
    /// Counts <c>/system/scheduler</c> objects with the exact name.
    /// </summary>
    Task<int> CountSchedulersByNameAsync(
        ITikConnection connection,
        string schedulerName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support CountSchedulersByNameAsync.");

    /// <summary>
    /// Removes every <c>/system/scheduler</c> object with the exact name, each by its RouterOS <c>.id</c>.
    /// Unrelated schedulers are never touched.
    /// </summary>
    Task RemoveSchedulersByNameAsync(
        ITikConnection connection,
        string schedulerName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support RemoveSchedulersByNameAsync.");

    /// <summary>
    /// Issues exactly one <c>/system/routerboard/upgrade</c> (RouterBOOT loader only).
    /// </summary>
    Task UpgradeRouterboardAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support UpgradeRouterboardAsync.");
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
