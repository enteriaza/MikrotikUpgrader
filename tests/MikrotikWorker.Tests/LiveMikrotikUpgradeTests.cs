using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests;

/// <summary>
/// Controlled live upgrade of the designated test router only.
/// Destructive: issues reboot. Run explicitly: dotnet test --filter Category=LiveUpgrade
/// </summary>
[Trait("Category", "LiveUpgrade")]
public sealed class LiveMikrotikUpgradeTests
{
    private const string LiveHost = "198.18.0.83";
    private const string WorkerAdvertisedHost = "198.18.0.66";
    private const int FirmwareHttpPort = 8080;
    private static readonly string SummaryPath =
        Path.Combine(Path.GetTempPath(), "mikrotik-live-upgrade-result.json");

    [Fact]
    public async Task LiveRouter_Backup_Fetch_Reboot_Verify_6_49_23()
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
        string password = mikrotik.Password;

        Assert.True(await IsTcpReachableAsync(LiveHost, mikrotik.Port, TimeSpan.FromSeconds(3)), "API port unreachable");
        Assert.True(await IsTcpReachableAsync(LiveHost, 22, TimeSpan.FromSeconds(3)), "SSH/SFTP port unreachable — stop");

        string firmwareRoot = FindFirmwareFilesRoot();
        Assert.True(Directory.Exists(firmwareRoot), $"FirmwareFiles missing at {firmwareRoot}");
        string mainPackage = Path.Combine(firmwareRoot, "mikrotik", "routeros-mipsbe-6.49.23.npk");
        Assert.True(File.Exists(mainPackage), "routeros-mipsbe-6.49.23.npk missing");

        string backupRoot = Path.Combine(AppContext.BaseDirectory, "Backups-LiveUpgrade");
        Directory.CreateDirectory(backupRoot);

        string advertisedBaseUrl = $"http://{WorkerAdvertisedHost}:{FirmwareHttpPort}/firmware_files";
        await using WebApplication firmwareServer = await StartFirmwareServerAsync(firmwareRoot, FirmwareHttpPort);

        // Prove Kestrel is reachable from this host before asking the router to fetch.
        using (HttpClient probe = new())
        {
            HttpResponseMessage response = await probe.GetAsync(
                $"{advertisedBaseUrl}/mikrotik/routeros-mipsbe-6.49.23.npk");
            Assert.True(response.IsSuccessStatusCode, $"Kestrel probe failed: {(int)response.StatusCode}");
        }

        RecordingLoggerProvider logs = new();
        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(logs);
            builder.SetMinimumLevel(LogLevel.Information);
        });

        FirmwareUpgradeOptions upgradeOptions = new()
        {
            Enabled = true,
            TargetRouterOsVersion = "6.49.23",
            AdvertisedBaseUrl = advertisedBaseUrl,
            FetchTimeout = TimeSpan.FromMinutes(15),
            ReconnectInterval = TimeSpan.FromSeconds(10),
            RebootTimeout = TimeSpan.FromMinutes(15),
            PostUpgradeVerifyTimeout = TimeSpan.FromMinutes(5),
            BackupFileReadyTimeout = TimeSpan.FromMinutes(3),
            BackupFileReadyPollInterval = TimeSpan.FromSeconds(1),
            BackupPassword = string.Empty,
            BackupDirectory = backupRoot,
            ScpPort = 22,
            ScpTimeout = TimeSpan.FromMinutes(5),
        };

        Tik4netConnectionFactory connectionFactory = new(Options.Create(mikrotik));
        MikrotikApiClient api = new(
            connectionFactory,
            Options.Create(mikrotik),
            loggerFactory.CreateLogger<MikrotikApiClient>());
        MikrotikScpFileTransferClient sftp = new(loggerFactory.CreateLogger<MikrotikScpFileTransferClient>());
        DeviceBackupService backupService = new(
            api,
            sftp,
            Options.Create(upgradeOptions),
            TimeProvider.System,
            loggerFactory.CreateLogger<DeviceBackupService>());
        FirmwarePackageCatalog catalog = new(Options.Create(new FirmwareFilesOptions { Path = firmwareRoot }));
        UpgradePackageResolver resolver = new(
            catalog,
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot }));
        DeviceCompatibilityAssessor assessor = new(
            catalog,
            Options.Create(new FirmwareAssessmentOptions { TargetRouterOsVersion = "6.49.23" }));
        RouterOsV6UpgradeExecutor upgradeExecutor = new(
            api,
            connectionFactory,
            resolver,
            backupService,
            Options.Create(upgradeOptions),
            Options.Create(mikrotik),
            TimeProvider.System,
            loggerFactory.CreateLogger<RouterOsV6UpgradeExecutor>());

        MikrotikConnectionOptions connectionOptions = new()
        {
            Host = LiveHost,
            Username = mikrotik.Username,
            Password = password,
            Port = mikrotik.Port,
        };

        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "live-upgrade.csv",
            RowNumber = 2,
            RouterName = "LIVE-TEST",
            RouterIp = IPAddress.Parse(LiveHost),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-5),
            DateStopUtc = DateTime.UtcNow.AddHours(2),
        };

        DateTimeOffset rebootStartedAt = default;
        TimeSpan reconnectDuration = TimeSpan.Zero;
        UpgradeExecutionResult upgradeResult;
        MikrotikDeviceInfo beforeInfo;
        IReadOnlyList<InstalledPackageInfo> beforePackages;
        DeviceCompatibilityAssessment assessment;
        UpgradePackageResolution resolution;

        ITikConnection connection = await connectionFactory.CreateAsync(connectionOptions, CancellationToken.None);
        try
        {
            beforeInfo = await api.ReadDeviceInfoAsync(connection, LiveHost, CancellationToken.None);
            Assert.Equal(LiveHost, connectionOptions.Host);
            Assert.Contains("6.49.19", beforeInfo.Version, StringComparison.Ordinal);
            Assert.Equal("mipsbe", beforeInfo.ArchitectureName, ignoreCase: true);

            beforePackages = await api.ListInstalledPackagesAsync(connection, CancellationToken.None);
            Assert.Contains(
                beforePackages,
                static p => UpgradePackageResolver.IsMainRouterOsPackage(p.Name)
                    || UpgradePackageResolver.IsSystemPackage(p.Name));

            assessment = assessor.Assess(LiveHost, beforeInfo, DateTimeOffset.UtcNow);
            Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);

            resolution = resolver.Resolve(beforeInfo.ArchitectureName, beforePackages, RouterOsVersion.Parse("6.49.23"));
            Assert.True(
                resolution.Succeeded,
                $"Package resolution failed: {resolution.FailureReason}. Inventory: {string.Join(", ", beforePackages.Select(p => $"{p.Name}/{p.Version}/disabled={p.Disabled}"))}");
            Assert.Contains(
                resolution.Packages,
                static p => p.CatalogPackage.FileName.Equals("routeros-mipsbe-6.49.23.npk", StringComparison.OrdinalIgnoreCase));

            foreach (ResolvedUpgradePackage pkg in resolution.Packages)
            {
                Assert.True(File.Exists(pkg.CatalogPackage.FullPath), $"Missing catalog file {pkg.CatalogPackage.FileName}");
                Assert.Equal(RouterOsVersion.Parse("6.49.23"), pkg.CatalogPackage.Version);
            }

            // Phase B+C+D via production upgrade executor (backup gate → fetch → reboot → verify).
            rebootStartedAt = DateTimeOffset.UtcNow;
            DateTimeOffset reconnectWaitStart = rebootStartedAt;
            upgradeResult = await upgradeExecutor.ExecuteAsync(
                connection,
                job,
                beforeInfo,
                assessment,
                mikrotik.Username,
                password,
                static () => true,
                CancellationToken.None);
            reconnectDuration = DateTimeOffset.UtcNow - reconnectWaitStart;
        }
        finally
        {
            try
            {
                connection.Dispose();
            }
            catch
            {
            }
        }

        // Collect post-state independently for the report even if upgrade result already verified.
        MikrotikDeviceInfo? afterInfo = null;
        IReadOnlyList<InstalledPackageInfo>? afterPackages = null;
        try
        {
            using ITikConnection verifyConnection =
                await connectionFactory.CreateAsync(connectionOptions, CancellationToken.None);
            afterInfo = await api.ReadDeviceInfoAsync(verifyConnection, LiveHost, CancellationToken.None);
            afterPackages = await api.ListInstalledPackagesAsync(verifyConnection, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await WriteSummaryAsync(new
            {
                RouterIp = LiveHost,
                Status = "PostUpgradeProbeFailed",
                UpgradeOutcome = upgradeResult.Outcome.ToString(),
                Detail = Sanitize(upgradeResult.Detail ?? ex.Message, password),
            });
            Assert.Fail($"Post-upgrade probe failed: {Sanitize(ex.Message, password)}");
        }

        string[] exportFiles = Directory.GetFiles(Path.Combine(backupRoot, "Exports"), "*.rsc");
        string[] binaryFiles = Directory.GetFiles(Path.Combine(backupRoot, "Binary"), "*.backup");
        string[] manifests = Directory.GetFiles(Path.Combine(backupRoot, "Manifests"), "*.json");
        Assert.NotEmpty(exportFiles);
        Assert.NotEmpty(binaryFiles);
        Assert.NotEmpty(manifests);

        BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllTextAsync(manifests.OrderByDescending(File.GetLastWriteTimeUtc).First()))!;
        Assert.Equal(LiveHost, manifest.RouterIp);
        Assert.Equal("pre-upgrade", manifest.Purpose);
        Assert.False(string.IsNullOrWhiteSpace(manifest.ExportSha256));
        Assert.False(string.IsNullOrWhiteSpace(manifest.BinaryBackupSha256));
        Assert.True(manifest.ExportSizeBytes > 0);
        Assert.True(manifest.BinaryBackupSizeBytes > 0);

        string exportPath = Path.Combine(backupRoot, "Exports", manifest.ExportFileName);
        string binaryPath = Path.Combine(backupRoot, "Binary", manifest.BinaryBackupFileName);
        Assert.True(File.Exists(exportPath));
        Assert.True(File.Exists(binaryPath));
        Assert.Equal(manifest.ExportSha256, ComputeSha256Hex(exportPath));
        Assert.Equal(manifest.BinaryBackupSha256, ComputeSha256Hex(binaryPath));

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, upgradeResult.Outcome);
        Assert.True(upgradeResult.RebootInitiated);
        Assert.NotNull(afterInfo);
        Assert.True(RouterOsVersion.TryParse(afterInfo!.Version, out RouterOsVersion afterVersion));
        Assert.Equal(RouterOsVersion.Parse("6.49.23"), afterVersion);
        Assert.Equal("mipsbe", afterInfo.ArchitectureName, ignoreCase: true);

        foreach (ResolvedUpgradePackage required in resolution.Packages)
        {
            InstalledPackageInfo? found = afterPackages!.FirstOrDefault(p =>
                p.Name.Equals(required.InstalledPackageName, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(found);
            Assert.True(RouterOsVersion.TryParse(found!.Version, out RouterOsVersion pkgVer));
            Assert.Equal(RouterOsVersion.Parse("6.49.23"), pkgVer);
        }

        Assert.DoesNotContain(logs.Messages, m => m.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, static m => m.Contains("Password=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logs.Messages, static m => m.Contains("/system/routerboard/upgrade", StringComparison.Ordinal));

        await WriteSummaryAsync(new
        {
            RouterIp = LiveHost,
            Status = "UpgradeSucceeded",
            BeforeVersion = beforeInfo.Version,
            AfterVersion = afterInfo.Version,
            Architecture = afterInfo.ArchitectureName,
            BeforePackages = beforePackages.Select(p => new { p.Name, p.Version, p.Disabled }).ToArray(),
            SelectedPackages = resolution.Packages.Select(p => new
            {
                p.InstalledPackageName,
                p.CatalogPackage.FileName,
                p.CatalogPackage.SizeBytes,
                p.RelativeUrlPath,
            }).ToArray(),
            AfterPackages = afterPackages!.Select(p => new { p.Name, p.Version, p.Disabled }).ToArray(),
            Backup = new
            {
                manifest.ExportFileName,
                manifest.ExportSizeBytes,
                manifest.ExportSha256,
                manifest.BinaryBackupFileName,
                manifest.BinaryBackupSizeBytes,
                manifest.BinaryBackupSha256,
                manifest.BackupTimestampUtc,
                ManifestPath = manifests.OrderByDescending(File.GetLastWriteTimeUtc).First(),
                ExportPath = exportPath,
                BinaryPath = binaryPath,
            },
            RebootStartedAtUtc = rebootStartedAt,
            ReconnectDurationSeconds = reconnectDuration.TotalSeconds,
            UpgradeOutcome = upgradeResult.Outcome.ToString(),
            AdvertisedBaseUrl = advertisedBaseUrl,
            DeviceLogNote = "Live test used RecordingLoggerProvider; production device logs go to Logs/Devices/",
        });
    }

    private static async Task<WebApplication> StartFirmwareServerAsync(string firmwareRoot, int port)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();

        FileExtensionContentTypeProvider contentTypes = new();
        contentTypes.Mappings[".npk"] = "application/octet-stream";
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(firmwareRoot),
            RequestPath = FirmwareFilesOptions.RequestPath,
            ContentTypeProvider = contentTypes,
        });

        await app.StartAsync();
        return app;
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

    private static async Task WriteSummaryAsync(object summary) =>
        await File.WriteAllTextAsync(
            SummaryPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

    private static string Sanitize(string message, string password) =>
        string.IsNullOrEmpty(password)
            ? message
            : message.Replace(password, "***", StringComparison.Ordinal);

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
