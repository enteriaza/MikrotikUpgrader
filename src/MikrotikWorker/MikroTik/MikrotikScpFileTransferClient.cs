using Renci.SshNet;
using Renci.SshNet.Common;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Retrieves router files over SFTP/SCP. RouterOS API <c>contents</c> cannot transfer binary backups intact.
/// </summary>
internal sealed class MikrotikScpFileTransferClient(ILogger<MikrotikScpFileTransferClient> logger)
    : IMikrotikFileTransferClient
{
    public Task DownloadFileAsync(
        string host,
        int port,
        string username,
        string password,
        string remoteFileName,
        string localFilePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);

                Renci.SshNet.ConnectionInfo connectionInfo = new(
                    host,
                    port,
                    username,
                    new PasswordAuthenticationMethod(username, password))
                {
                    Timeout = timeout,
                };

                string remotePath = "/" + remoteFileName.TrimStart('/');
                using SftpClient sftp = new(connectionInfo);
                try
                {
                    sftp.Connect();
                    using FileStream output = new(localFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    sftp.DownloadFile(remotePath, output);
                    output.Flush();
                }
                catch (SshException ex)
                {
                    logger.LogError(ex, "SFTP download failed for {RemoteFile} from {Host}:{Port}", remoteFileName, host, port);
                    throw;
                }
                finally
                {
                    if (sftp.IsConnected)
                    {
                        sftp.Disconnect();
                    }
                }
            },
            cancellationToken);
    }
}
