// -----------------------------------------------------------------------------
// File: MikrotikScpFileTransferClient.cs
//
// Description:
// SFTP (SSH.NET) download of router files into a .partial sibling then atomic Move to the
// final path. Used because RouterOS API cannot transfer binary .backup contents intact.
//
// -----------------------------------------------------------------------------

using Renci.SshNet;
using Renci.SshNet.Common;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Retrieves router files over SFTP. RouterOS API <c>contents</c> cannot transfer binary backups intact.
/// </summary>
/// <remarks>
/// Uses SSH.NET's cancellable <see cref="SftpClient.DownloadFileAsync(string, Stream, CancellationToken)"/>,
/// which observes the token during path resolution and each read/write of the transfer loop.
/// Downloads write to a uniquely named sibling <c>.partial</c> file and only promote to the
/// destination on success, so cancellation never leaves a completed-looking package at the final path.
/// Completion boundary: once <see cref="File.Move(string, string)"/> succeeds, the operation is success
/// even if cancellation is requested afterwards.
/// </remarks>
internal sealed class MikrotikScpFileTransferClient : IMikrotikFileTransferClient
{
    /// <summary>Logger for download failures and partial-file cleanup problems.</summary>
    private readonly ILogger<MikrotikScpFileTransferClient> _logger;
    /// <summary>Creates the SFTP session for a download; swapped in tests, the production default wraps <see cref="SftpClient"/>.</summary>
    private readonly Func<Renci.SshNet.ConnectionInfo, ISftpTransferSession> _sessionFactory;

    /// <summary>
    /// Creates the production SFTP transfer client.
    /// </summary>
    /// <param name="logger">Logger for download failures and partial-file cleanup problems.</param>
    public MikrotikScpFileTransferClient(ILogger<MikrotikScpFileTransferClient> logger)
        : this(logger, static info => new SshNetSftpTransferSession(info))
    {
    }

    /// <summary>Test seam for deterministic cancellation coverage.</summary>
    /// <param name="logger">Logger for download failures and partial-file cleanup problems.</param>
    /// <param name="sessionFactory">Creates the SFTP session for the supplied connection info; lets tests inject a fake session.</param>
    internal MikrotikScpFileTransferClient(
        ILogger<MikrotikScpFileTransferClient> logger,
        Func<Renci.SshNet.ConnectionInfo, ISftpTransferSession> sessionFactory)
    {
        _logger = logger;
        _sessionFactory = sessionFactory;
    }

    /// <inheritdoc />
    public async Task DownloadFileAsync(
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

        cancellationToken.ThrowIfCancellationRequested();

        string? directory = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(localFilePath))
        {
            throw new IOException($"Destination file already exists: {localFilePath}");
        }

        Renci.SshNet.ConnectionInfo connectionInfo = new(
            host,
            port,
            username,
            new PasswordAuthenticationMethod(username, password))
        {
            Timeout = timeout,
        };

        string remotePath = "/" + remoteFileName.TrimStart('/');
        // Unique partial name so concurrent downloads (even to the same final path) cannot
        // delete or append into each other's in-progress artefacts.
        string partialPath = localFilePath + "." + Guid.NewGuid().ToString("N") + ".partial";

        await using ISftpTransferSession session = _sessionFactory(connectionInfo);
        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);

            await using (FileStream output = new(
                               partialPath,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               bufferSize: 64 * 1024,
                               options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await session
                    .DownloadToStreamAsync(remotePath, output, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Promote only after a complete, flushed download. Never overwrite an existing destination
            // (two-argument Move throws if the destination already exists).
            File.Move(partialPath, localFilePath);
        }
        catch (OperationCanceledException)
        {
            TryDeleteOwnedPartial(partialPath);
            throw;
        }
        catch (SshException ex)
        {
            TryDeleteOwnedPartial(partialPath);
            _logger.LogError(
                ex,
                "SFTP download failed for {RemoteFile} from {Host}:{Port}",
                remoteFileName,
                host,
                port);
            throw;
        }
        catch
        {
            TryDeleteOwnedPartial(partialPath);
            throw;
        }
    }

    /// <summary>Best-effort removal of this download's own <c>.partial</c> file; a failure is logged as a warning and never thrown, so it cannot mask the original error.</summary>
    private void TryDeleteOwnedPartial(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to delete SFTP download partial artefact {PartialPath}",
                partialPath);
        }
    }
}

/// <summary>Exclusive SFTP session used for a single download.</summary>
internal interface ISftpTransferSession : IAsyncDisposable
{
    /// <summary>Opens the SSH/SFTP connection.</summary>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Streams a remote file into <paramref name="destination"/>.</summary>
    /// <param name="remotePath">Absolute remote file path.</param>
    /// <param name="destination">Writable stream that receives the file content.</param>
    /// <param name="cancellationToken">Cancels the transfer; observed during path resolution and each read/write.</param>
    Task DownloadToStreamAsync(string remotePath, Stream destination, CancellationToken cancellationToken);
}

/// <summary>SSH.NET-backed <see cref="ISftpTransferSession"/> that owns one <see cref="SftpClient"/> and disconnects and disposes it exactly once.</summary>
/// <param name="connectionInfo">Host, credentials and timeout used to create the underlying SFTP client.</param>
internal sealed class SshNetSftpTransferSession(Renci.SshNet.ConnectionInfo connectionInfo) : ISftpTransferSession
{
    /// <summary>Underlying SFTP client created from the connection info; disposed by <see cref="DisposeAsync"/>.</summary>
    private readonly SftpClient _client = new(connectionInfo);
    /// <summary>Set to 1 once disposed so cleanup runs only once (accessed via <see cref="Interlocked"/>).</summary>
    private int _disposed;

    /// <summary>Connects the SFTP client, honouring cancellation.</summary>
    public Task ConnectAsync(CancellationToken cancellationToken) =>
        _client.ConnectAsync(cancellationToken);

    /// <summary>Downloads the remote file into the destination stream using SSH.NET's cancellable download.</summary>
    public Task DownloadToStreamAsync(
        string remotePath,
        Stream destination,
        CancellationToken cancellationToken) =>
        _client.DownloadFileAsync(remotePath, destination, cancellationToken);

    /// <summary>Disconnects (when connected, ignoring disconnect errors) and disposes the client. Repeated calls are no-ops.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch
        {
        }

        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
