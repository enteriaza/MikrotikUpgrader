using System.Globalization;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using tik4net;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// tik4net-backed RouterOS API client for device info, inventory, backup, fetch, and reboot.
/// Never issues <c>/system/routerboard/upgrade</c>.
/// </summary>
internal sealed class MikrotikApiClient(
    IMikrotikConnectionFactory connectionFactory,
    IOptions<MikroTikOptions> options,
    ILogger<MikrotikApiClient> logger) : IMikrotikApiClient, IMikrotikRouterOperations
{
    public async Task<MikrotikDeviceInfo> GetDeviceInfoAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        MikrotikConnectionOptionsValidator.Validate(connection);
        cancellationToken.ThrowIfCancellationRequested();

        int port = connection.Port ?? options.Value.Port;
        logger.LogInformation(
            "Connecting to MikroTik device at {Host}:{Port}",
            connection.Host,
            port);

        try
        {
            using ITikConnection tikConnection =
                await connectionFactory.CreateAsync(connection, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Connected to MikroTik device at {Host}:{Port}", connection.Host, port);

            MikrotikDeviceInfo info = await ReadDeviceInfoAsync(tikConnection, connection.Host, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Read MikroTik device info for {Host}: Identity={Identity}, Version={Version}, Architecture={ArchitectureName}, Board={BoardName}, Uptime={Uptime}",
                connection.Host,
                info.Identity,
                info.Version,
                info.ArchitectureName,
                info.BoardName,
                info.Uptime);

            return info;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to communicate with MikroTik device at {Host}:{Port}",
                connection.Host,
                port);
            throw;
        }
    }

    public async Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
        ITikConnection connection,
        string host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        cancellationToken.ThrowIfCancellationRequested();

        string identity = await connection
            .CreateCommand("/system/identity/print")
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? string.Empty;

        cancellationToken.ThrowIfCancellationRequested();

        ITikReSentence resource = await connection
            .CreateCommand("/system/resource/print")
            .ExecuteSingleRowAsync(cancellationToken)
            .ConfigureAwait(false);

        ITikReSentence? routerboard = await TryReadRouterboardAsync(connection, host, cancellationToken)
            .ConfigureAwait(false);

        return new MikrotikDeviceInfo
        {
            Identity = identity,
            Version = resource.GetResponseFieldOrDefault("version", string.Empty) ?? string.Empty,
            ArchitectureName = NullIfWhiteSpace(resource.GetResponseFieldOrDefault("architecture-name", null)),
            BoardName = NullIfWhiteSpace(resource.GetResponseFieldOrDefault("board-name", null)),
            Platform = NullIfWhiteSpace(resource.GetResponseFieldOrDefault("platform", null)),
            Uptime = NullIfWhiteSpace(resource.GetResponseFieldOrDefault("uptime", null)),
            RouterboardModel = NullIfWhiteSpace(routerboard?.GetResponseFieldOrDefault("model", null)),
            RouterboardFirmwareType = NullIfWhiteSpace(routerboard?.GetResponseFieldOrDefault("firmware-type", null)),
            RouterboardCurrentFirmware = NullIfWhiteSpace(routerboard?.GetResponseFieldOrDefault("current-firmware", null)),
            RouterboardUpgradeFirmware = NullIfWhiteSpace(routerboard?.GetResponseFieldOrDefault("upgrade-firmware", null)),
            RouterboardFactoryFirmware = NullIfWhiteSpace(routerboard?.GetResponseFieldOrDefault("factory-firmware", null)),
        };
    }

    public async Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/system/package/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<InstalledPackageInfo> packages = [];
        foreach (ITikReSentence row in rows)
        {
            string name = row.GetResponseFieldOrDefault("name", string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string version = row.GetResponseFieldOrDefault("version", string.Empty) ?? string.Empty;
            string disabledRaw = row.GetResponseFieldOrDefault("disabled", "false") ?? "false";
            bool disabled = disabledRaw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || disabledRaw.Equals("yes", StringComparison.OrdinalIgnoreCase);
            string? bundle = NullIfWhiteSpace(row.GetResponseFieldOrDefault("bundle", null));

            packages.Add(new InstalledPackageInfo(name.Trim(), version.Trim(), disabled, bundle));
        }

        return packages;
    }

    public async Task ExportConfigurationToFileAsync(
        ITikConnection connection,
        string fileNameWithoutExtension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameWithoutExtension);
        cancellationToken.ThrowIfCancellationRequested();

        // File-based export avoids truncation of large interactive /export responses.
        ITikCommand command = connection.CreateCommand("/export");
        command.AddParameter("file", fileNameWithoutExtension);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Issued /export file={File}", fileNameWithoutExtension);
    }

    public async Task SaveSystemBackupAsync(
        ITikConnection connection,
        string fileNameWithoutExtension,
        string? backupPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameWithoutExtension);
        cancellationToken.ThrowIfCancellationRequested();

        ITikCommand command = connection.CreateCommand("/system/backup/save");
        command.AddParameter("name", fileNameWithoutExtension);
        if (!string.IsNullOrEmpty(backupPassword))
        {
            command.AddParameter("password", backupPassword);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Issued /system/backup/save name={File}", fileNameWithoutExtension);
    }

    public async Task<RouterFileInfo?> FindFileAsync(
        ITikConnection connection,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/file/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (ITikReSentence row in rows)
        {
            string name = row.GetResponseFieldOrDefault("name", string.Empty) ?? string.Empty;
            if (!string.Equals(Path.GetFileName(name), Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string sizeRaw = row.GetResponseFieldOrDefault("size", "0") ?? "0";
            if (!long.TryParse(sizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size))
            {
                size = 0;
            }

            string? id = NullIfWhiteSpace(row.GetResponseFieldOrDefault(".id", null));
            return new RouterFileInfo(name, size, id);
        }

        return null;
    }

    public async Task RemoveFileAsync(
        ITikConnection connection,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        RouterFileInfo? existing = await FindFileAsync(connection, fileName, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        ITikCommand command = connection.CreateCommand("/file/remove");
        if (!string.IsNullOrWhiteSpace(existing.Id))
        {
            command.AddParameter(".id", existing.Id);
        }
        else
        {
            // Fallback for transports that expose name-based numbers.
            command.AddParameter("numbers", existing.Name);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Issued /file/remove for {File}", fileName);
    }

    public async Task WaitForFileAsync(
        ITikConnection connection,
        string fileName,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RouterFileInfo? file = await FindFileAsync(connection, fileName, cancellationToken)
                .ConfigureAwait(false);
            if (file is not null && file.SizeBytes > 0)
            {
                return;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Router file '{fileName}' was not ready with non-zero size within {timeout}.");
    }

    public async Task FetchUrlToFileAsync(
        ITikConnection connection,
        string url,
        string destinationFileName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFileName);
        cancellationToken.ThrowIfCancellationRequested();

        // Remove any prior partial/stale destination so size verification cannot pass a leftover file.
        RouterFileInfo? existing = await FindFileAsync(connection, destinationFileName, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            logger.LogInformation(
                "Removing existing router file before fetch. File={File}, SizeBytes={SizeBytes}",
                destinationFileName,
                existing.SizeBytes);
            await RemoveFileAsync(connection, destinationFileName, cancellationToken).ConfigureAwait(false);
        }

        ITikCommand command = connection.CreateCommand("/tool/fetch");
        command.AddParameter("url", url);
        command.AddParameter("dst-path", destinationFileName);
        command.AddParameter("mode", "http");
        command.AddParameter("keep-result", "yes");

        // /tool/fetch emits multiple !re progress sentences; ExecuteNonQuery rejects that shape.
        int timeoutSec = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        List<ITikReSentence> sentences = [];
        try
        {
            await foreach (ITikReSentence sentence in command
                               .ExecuteListUntilDoneAsync(timeoutSec, timeoutCts.Token)
                               .ConfigureAwait(false))
            {
                sentences.Add(sentence);
                string? status = sentence.GetResponseFieldOrDefault("status", null);
                string? downloaded = sentence.GetResponseFieldOrDefault("downloaded", null);
                string? total = sentence.GetResponseFieldOrDefault("total", null);
                if (!string.IsNullOrWhiteSpace(status))
                {
                    logger.LogInformation(
                        "Fetch progress. File={File}, Status={Status}, Downloaded={Downloaded}, Total={Total}",
                        destinationFileName,
                        status,
                        downloaded,
                        total);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Fetch of '{destinationFileName}' exceeded {timeout}.");
        }

        ITikReSentence? final = sentences.LastOrDefault(static s =>
            string.Equals(s.GetResponseFieldOrDefault("status", null), "finished", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.GetResponseFieldOrDefault("status", null), "failed", StringComparison.OrdinalIgnoreCase));
        if (final is null)
        {
            throw new InvalidOperationException(
                $"Fetch of '{destinationFileName}' completed without a finished/failed status sentence.");
        }

        string finalStatus = final.GetResponseFieldOrDefault("status", string.Empty) ?? string.Empty;
        if (!finalStatus.Equals("finished", StringComparison.OrdinalIgnoreCase))
        {
            string failure = final.GetResponseFieldOrDefault("failure", null)
                ?? final.GetResponseFieldOrDefault("message", null)
                ?? finalStatus;
            throw new InvalidOperationException($"Fetch of '{destinationFileName}' failed: {failure}");
        }

        logger.LogInformation("Completed /tool/fetch dst-path={File}", destinationFileName);
    }

    public async Task RebootAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        ITikCommand command = connection.CreateCommand("/system/reboot");
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Router reboot command returned with connection loss (expected during restart)");
        }
    }

    private async Task<ITikReSentence?> TryReadRouterboardAsync(
        ITikConnection connection,
        string host,
        CancellationToken cancellationToken)
    {
        try
        {
            return await connection
                .CreateCommand("/system/routerboard/print")
                .ExecuteSingleRowOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "RouterBOARD information is unavailable for MikroTik device {Host}. Command=/system/routerboard/print",
                host);
            return null;
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
