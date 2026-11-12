using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade.Live;
/// <summary>
/// Read-only live inventory + SFTP backup against 198.18.0.83.
/// Does not fetch packages, reboot, or change configuration.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveMikrotikInventoryAndBackupTests
{
    private const string LiveHost = "198.18.0.83";
    private static readonly TimeSpan OpTimeout = TimeSpan.FromSeconds(30);
    private static readonly string InventoryPath =
        Path.Combine(Path.GetTempPath(), "mikrotik-live-inventory.json");
    private static readonly string BackupSummaryPath =
        Path.Combine(Path.GetTempPath(), "mikrotik-live-backup-result.json");

    [Fact]
    public async Task LiveRouter_PackageInventory_ResolvesAgainstFirmwareFiles()
    {
        LiveMikrotikTestGate.RequireArmed(LiveMikrotikTestGate.AllowLiveMikrotikEnv, "Live");

        (MikroTikOptions mikrotik, string password) = LoadCredentials();
        Assert.True(await IsTcpReachableAsync(LiveHost, mikrotik.Port, TimeSpan.FromSeconds(3)));

        Tik4netConnectionFactory factory = new(Options.Create(mikrotik));
        MikrotikApiClient api = new(factory, Options.Create(mikrotik), NullLogger<MikrotikApiClient>.Instance);

        using CancellationTokenSource cts = new(OpTimeout);
        using var connection = await factory
            .CreateAsync(
                new MikrotikConnectionOptions
                {
                    Host = LiveHost,
                    Username = mikrotik.Username,
                    Password = password,
                    Port = mikrotik.Port,
                },
                cts.Token)
            .WaitAsync(OpTimeout, cts.Token);

        MikrotikDeviceInfo info = await api.ReadDeviceInfoAsync(connection, LiveHost, cts.Token)
            .WaitAsync(OpTimeout, cts.Token);
        IReadOnlyList<InstalledPackageInfo> packages = await api
            .ListInstalledPackagesAsync(connection, cts.Token)
            .WaitAsync(OpTimeout, cts.Token);

        // Current post-upgrade baseline after authorised 6.49.19 → 6.49.23 live run.
        Assert.Contains("6.49.23", info.Version, StringComparison.Ordinal);
        Assert.Equal("mipsbe", info.ArchitectureName, ignoreCase: true);

        InstalledPackageInfo main = Assert.Single(
            packages,
            static p => UpgradePackageResolver.IsMainRouterOsPackage(p.Name));
        Assert.Equal("routeros-mipsbe", main.Name, ignoreCase: true);
        Assert.Equal("6.49.23", main.Version, ignoreCase: true);
        Assert.False(main.IsBundledComponent);

        InstalledPackageInfo system = Assert.Single(
            packages,
            static p => UpgradePackageResolver.IsSystemPackage(p.Name));
        Assert.True(system.IsBundledComponent);
        Assert.Equal("routeros-mipsbe", system.Bundle, ignoreCase: true);

        InstalledPackageInfo ipv6 = Assert.Single(
            packages,
            static p => p.Name.Equals("ipv6", StringComparison.OrdinalIgnoreCase));
        Assert.True(ipv6.IsBundledComponent);
        Assert.Equal("routeros-mipsbe", ipv6.Bundle, ignoreCase: true);

        Assert.All(
            packages.Where(static p => !UpgradePackageResolver.IsMainRouterOsPackage(p.Name)),
            static p => Assert.True(p.IsBundledComponent, $"{p.Name} should be bundled under routeros-mipsbe"));

        string firmwareRoot = FindFirmwareFilesRoot();
        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(firmwareRoot),
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot }));
        UpgradePackageResolution resolution = resolver.Resolve(
            info.ArchitectureName,
            packages,
            RouterOsVersion.Parse("6.49.23"));

        PackageInventorySummary inventorySummary = PackageInventoryReporting.Summarize(packages);

        await File.WriteAllTextAsync(
            InventoryPath,
            JsonSerializer.Serialize(
                new
                {
                    RouterIp = LiveHost,
                    info.Identity,
                    info.Version,
                    info.ArchitectureName,
                    info.RouterboardModel,
                    info.RouterboardFirmwareType,
                    info.RouterboardCurrentFirmware,
                    info.RouterboardUpgradeFirmware,
                    info.RouterboardFactoryFirmware,
                    IndependentInstalledCount = inventorySummary.IndependentCount,
                    IndependentInstalled = PackageInventoryReporting.FormatIndependentPackages(
                        inventorySummary.IndependentPackages),
                    BundledComponentCount = inventorySummary.BundledCount,
                    DeviceLogFileName = DeviceLogNaming.BuildDeviceLogFileName(info.Identity, IPAddress.Parse(LiveHost)),
                    Packages = packages.Select(p => new
                    {
                        p.Name,
                        p.Version,
                        p.Disabled,
                        p.Bundle,
                        p.IsBundledComponent,
                    }).ToArray(),
                    ResolutionSucceeded = resolution.Succeeded,
                    resolution.FailureReason,
                    Selected = resolution.Packages.Select(p => new
                    {
                        p.InstalledPackageName,
                        p.CatalogPackage.FileName,
                        p.CatalogPackage.SizeBytes,
                    }).ToArray(),
                    FirmwareFetchOccurred = false,
                    RebootOccurred = false,
                    RouterboardUpgradeCommandIssued = false,
                },
                new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(
            resolution.Succeeded,
            $"Resolution failed: {resolution.FailureReason}. Inventory written to {InventoryPath}");
        Assert.Single(resolution.Packages);
        Assert.Equal("routeros-mipsbe", resolution.Packages[0].InstalledPackageName, ignoreCase: true);
        Assert.Equal("routeros-mipsbe-6.49.23.npk", resolution.Packages[0].CatalogPackage.FileName, ignoreCase: true);
        Assert.DoesNotContain(resolution.Packages, static p => p.InstalledPackageName.Equals("system", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(InventoryPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveRouter_SftpBackup_ExportAndBinary_StoredCentrally()
    {
        LiveMikrotikTestGate.RequireArmed(LiveMikrotikTestGate.AllowLiveMikrotikEnv, "Live");

        (MikroTikOptions mikrotik, string password) = LoadCredentials();
        Assert.True(await IsTcpReachableAsync(LiveHost, mikrotik.Port, TimeSpan.FromSeconds(3)));
        Assert.True(await IsTcpReachableAsync(LiveHost, 22, TimeSpan.FromSeconds(3)), "SSH/SFTP port 22 unreachable");

        string backupRoot = Path.Combine(AppContext.BaseDirectory, "Backups-LiveReadOnly");
        Directory.CreateDirectory(backupRoot);

        FirmwareUpgradeOptions upgradeOptions = new()
        {
            Enabled = false,
            BackupDirectory = backupRoot,
            BackupFileReadyTimeout = TimeSpan.FromSeconds(60),
            BackupFileReadyPollInterval = TimeSpan.FromSeconds(1),
            BackupPassword = string.Empty,
            ScpPort = 22,
            ScpTimeout = TimeSpan.FromSeconds(60),
        };

        Tik4netConnectionFactory factory = new(Options.Create(mikrotik));
        MikrotikApiClient api = new(factory, Options.Create(mikrotik), NullLogger<MikrotikApiClient>.Instance);
        MikrotikScpFileTransferClient sftp = new(NullLogger<MikrotikScpFileTransferClient>.Instance);
        DeviceBackupService backupService = new(
            api,
            sftp,
            Options.Create(upgradeOptions),
            TimeProvider.System,
            NullLogger<DeviceBackupService>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using var connection = await factory
            .CreateAsync(
                new MikrotikConnectionOptions
                {
                    Host = LiveHost,
                    Username = mikrotik.Username,
                    Password = password,
                    Port = mikrotik.Port,
                },
                cts.Token)
            .WaitAsync(OpTimeout, cts.Token);

        MikrotikDeviceInfo info = await api.ReadDeviceInfoAsync(connection, LiveHost, cts.Token)
            .WaitAsync(OpTimeout, cts.Token);

        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "live-backup.csv",
            RowNumber = 2,
            RouterName = "LIVE-TEST",
            RouterIp = IPAddress.Parse(LiveHost),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

        DeviceBackupResult result = await backupService
            .CreatePreUpgradeBackupAsync(connection, job, info, mikrotik.Username, password, cts.Token)
            .WaitAsync(TimeSpan.FromMinutes(2), cts.Token);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(result.RemoteCleanupSucceeded);
        Assert.False(result.RemoteCleanupFailed);
        Assert.True(File.Exists(result.ExportLocalPath));
        Assert.True(File.Exists(result.BinaryLocalPath));
        Assert.True(File.Exists(result.ManifestLocalPath));
        Assert.True(new FileInfo(result.ExportLocalPath!).Length > 0);
        Assert.True(new FileInfo(result.BinaryLocalPath!).Length > 0);
        Assert.Equal(result.Manifest!.ExportSha256, ComputeSha256Hex(result.ExportLocalPath!));
        Assert.Equal(result.Manifest.BinaryBackupSha256, ComputeSha256Hex(result.BinaryLocalPath!));
        Assert.Equal(LiveHost, result.Manifest.RouterIp);
        Assert.Equal("pre-upgrade", result.Manifest.Purpose);
        Assert.Equal("SucceededWithRemoteCleanup", result.Manifest.BackupCompletionStatus);

        RouterFileInfo? exportStillRemote = await api
            .FindFileAsync(connection, result.Manifest.RemoteExportFileName!, cts.Token)
            .WaitAsync(OpTimeout, cts.Token);
        RouterFileInfo? binaryStillRemote = await api
            .FindFileAsync(connection, result.Manifest.RemoteBinaryFileName!, cts.Token)
            .WaitAsync(OpTimeout, cts.Token);
        Assert.Null(exportStillRemote);
        Assert.Null(binaryStillRemote);

        string manifestJson = await File.ReadAllTextAsync(result.ManifestLocalPath!);
        Assert.DoesNotContain(password, manifestJson, StringComparison.Ordinal);

        await File.WriteAllTextAsync(
            BackupSummaryPath,
            JsonSerializer.Serialize(
                new
                {
                    RouterIp = LiveHost,
                    result.Manifest.ExportFileName,
                    result.Manifest.ExportSizeBytes,
                    result.Manifest.ExportSha256,
                    result.Manifest.BinaryBackupFileName,
                    result.Manifest.BinaryBackupSizeBytes,
                    result.Manifest.BinaryBackupSha256,
                    result.Manifest.BackupTimestampUtc,
                    result.Manifest.RemoteExportFileName,
                    result.Manifest.RemoteBinaryFileName,
                    result.Manifest.BackupCompletionStatus,
                    result.ExportLocalPath,
                    result.BinaryLocalPath,
                    result.ManifestLocalPath,
                    result.RemoteCleanupSucceeded,
                    RemoteExportAbsent = exportStillRemote is null,
                    RemoteBinaryAbsent = binaryStillRemote is null,
                    FirmwareFetchOccurred = false,
                    RebootOccurred = false,
                    Status = "BackupSucceededWithRemoteCleanup",
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static (MikroTikOptions Options, string Password) LoadCredentials()
    {
        string? localConfigPath = FindLocalConfigPath();
        Assert.True(localConfigPath is not null, "Missing appsettings.Development.local.json");
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(localConfigPath, optional: false, reloadOnChange: false)
            .Build();
        MikroTikOptions mikrotik = new();
        configuration.GetSection(MikroTikOptions.SectionName).Bind(mikrotik);
        Assert.True(mikrotik.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(mikrotik.Username));
        Assert.False(string.IsNullOrEmpty(mikrotik.Password));
        return (mikrotik, mikrotik.Password);
    }

    private static string FindFirmwareFilesRoot()
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FirmwareFiles")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "FirmwareFiles")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "FirmwareFiles")),
        ];
        return candidates.First(Directory.Exists);
    }

    private static string? FindLocalConfigPath()
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MikrotikWorker", "appsettings.Development.local.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.local.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "MikrotikWorker", "appsettings.Development.local.json")),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ComputeSha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<bool> IsTcpReachableAsync(string host, int port, TimeSpan timeout)
    {
        try
        {
            using TcpClient client = new();
            using CancellationTokenSource cts = new(timeout);
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
