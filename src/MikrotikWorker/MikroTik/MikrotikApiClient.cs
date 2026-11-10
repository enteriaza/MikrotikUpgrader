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

            packages.Add(new InstalledPackageInfo(name.Trim(), version.Trim(), disabled));
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

            return new RouterFileInfo(name, size);
        }

        return null;
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

        ITikCommand command = connection.CreateCommand("/tool/fetch");
        command.AddParameter("url", url);
        command.AddParameter("dst-path", destinationFileName);
        command.AddParameter("mode", "http");
        command.AddParameter("keep-result", "yes");

        Task execute = command.ExecuteNonQueryAsync(cancellationToken);
        Task finished = await Task.WhenAny(execute, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (finished != execute)
        {
            throw new TimeoutException($"Fetch of '{destinationFileName}' exceeded {timeout}.");
        }

        await execute.ConfigureAwait(false);
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
