using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using Serilog;
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
        // Extra opt-in gate so Category=LiveUpgrade cannot run accidentally without explicit env.
        LiveMikrotikTestGate.RequireArmed(LiveMikrotikTestGate.AllowLiveUpgradeEnv, "LiveUpgrade");

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

        VerifyPriorCentralBackupIntact();

        string firmwareRoot = FindFirmwareFilesRoot();
        Assert.True(Directory.Exists(firmwareRoot), $"FirmwareFiles missing at {firmwareRoot}");
        string mainPackage = Path.Combine(firmwareRoot, "mikrotik", "routeros-mipsbe-6.49.23.npk");
        Assert.True(File.Exists(mainPackage), "routeros-mipsbe-6.49.23.npk missing");

        string backupRoot = Path.Combine(AppContext.BaseDirectory, "Backups-LiveUpgrade");
        Directory.CreateDirectory(backupRoot);

        string advertisedBaseUrl = $"http://{WorkerAdvertisedHost}:{FirmwareHttpPort}/firmware_files";
        await using WebApplication firmwareServer = await StartFirmwareServerAsync(firmwareRoot, FirmwareHttpPort);

        // Prove Kestrel is reachable and serves the full package before asking the router to fetch.
        long expectedPackageBytes = new FileInfo(mainPackage).Length;
        using (HttpClient probe = new())
        {
            using HttpResponseMessage response = await probe.GetAsync(
                $"{advertisedBaseUrl}/mikrotik/routeros-mipsbe-6.49.23.npk",
                HttpCompletionOption.ResponseHeadersRead);
            Assert.True(response.IsSuccessStatusCode, $"Kestrel probe failed: {(int)response.StatusCode}");
            Assert.Equal(expectedPackageBytes, response.Content.Headers.ContentLength);
        }

        string liveLogRoot = Path.Combine(AppContext.BaseDirectory, "Logs-LiveUpgrade");
        Directory.CreateDirectory(liveLogRoot);
        SerilogOptions serilogOptions = new()
        {
            LogDirectory = liveLogRoot,
            FileName = "application-.log",
            RollingInterval = "Day",
            WriteToConsole = false,
            WriteToFile = true,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
            DeviceLogAsyncCapacity = 10_000,
            DeviceLogAsyncBlockWhenFull = true,
            AsyncBufferCapacity = 10_000,
            AsyncBlockWhenFull = true,
            MinimumLevel = "Information",
        };
        DeviceLogWriter deviceLogWriter = new(Options.Create(serilogOptions));
        await deviceLogWriter.StartAsync(CancellationToken.None);
        LoggerConfiguration loggerConfiguration = new();
        SerilogConfigurator.Configure(
            loggerConfiguration,
            configuration: new ConfigurationBuilder().Build(),
            environment: new LiveUpgradeHostEnvironment(),
            options: serilogOptions,
            deviceLogWriter: deviceLogWriter);
        using Serilog.Core.Logger serilogLogger = loggerConfiguration.CreateLogger();
        Log.Logger = serilogLogger;
        using Serilog.Extensions.Logging.SerilogLoggerFactory loggerFactory = new(serilogLogger);

        FirmwareUpgradeOptions upgradeOptions = new()
        {
            Enabled = true,
            AdvertisedBaseUrl = advertisedBaseUrl,
            FetchTimeout = TimeSpan.FromMinutes(15),
            ReconnectInterval = TimeSpan.FromSeconds(10),
            RebootTimeout = TimeSpan.FromMinutes(5),
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
        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot });
        IOptions<FirmwareUpgradeOptions> upgradeOptionsWrapper = Options.Create(upgradeOptions);
        IOptions<MikroTikOptions> mikrotikOptionsWrapper = Options.Create(mikrotik);
        FirmwarePackageCatalog catalog = new(firmwareFilesOptions);
        UpgradePackageResolver resolver = new(catalog, firmwareFilesOptions);
        DeviceCompatibilityAssessor assessor = new(
            catalog,
            new FixedRouterOsV7TargetProvider("7.23.8"));
        RouterOsV6UpgradeExecutor upgradeExecutor = new(
            api,
            connectionFactory,
            resolver,
            backupService,
            new FixedRouterOsV7TargetProvider("7.23.8"),
            new LegacyFleetUpgradeAutomationCleanup(
                api,
                loggerFactory.CreateLogger<LegacyFleetUpgradeAutomationCleanup>()),
            new RouterboardFirmwareUpgradeService(
                api,
                connectionFactory,
                upgradeOptionsWrapper,
                mikrotikOptionsWrapper,
                TimeProvider.System,
                loggerFactory.CreateLogger<RouterboardFirmwareUpgradeService>()),
            new PassingOspfMigrationService(),
            upgradeOptionsWrapper,
            mikrotikOptionsWrapper,
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
            Assert.Equal("hEX PoE", beforeInfo.BoardName, ignoreCase: true);
            Assert.Equal("960PGS", beforeInfo.RouterboardModel, ignoreCase: true);

            beforePackages = await api.ListInstalledPackagesAsync(connection, CancellationToken.None);
            InstalledPackageInfo mainBefore = Assert.Single(
                beforePackages,
                static p => UpgradePackageResolver.IsMainRouterOsPackage(p.Name));
            Assert.Equal("routeros-mipsbe", mainBefore.Name, ignoreCase: true);
            Assert.False(mainBefore.IsBundledComponent);
            Assert.Contains(
                beforePackages,
                static p => UpgradePackageResolver.IsSystemPackage(p.Name) && p.IsBundledComponent);

            assessment = assessor.Assess(LiveHost, beforeInfo, DateTimeOffset.UtcNow);
            Assert.Equal(DeviceAssessmentStatus.ReadyForUpgrade, assessment.OverallStatus);

            resolution = resolver.Resolve(beforeInfo.ArchitectureName, beforePackages, RouterOsVersion.Parse("6.49.23"));
            Assert.True(
                resolution.Succeeded,
                $"Package resolution failed: {resolution.FailureReason}. Inventory: {string.Join(", ", beforePackages.Select(p => $"{p.Name}/{p.Version}/bundle={p.Bundle}/disabled={p.Disabled}"))}");
            Assert.Single(resolution.Packages);
            Assert.Equal("routeros-mipsbe", resolution.Packages[0].InstalledPackageName, ignoreCase: true);
            Assert.Equal(
                "routeros-mipsbe-6.49.23.npk",
                resolution.Packages[0].CatalogPackage.FileName,
                ignoreCase: true);
            Assert.DoesNotContain(
                resolution.Packages,
                static p => p.InstalledPackageName.Equals("system", StringComparison.OrdinalIgnoreCase));

            foreach (ResolvedUpgradePackage pkg in resolution.Packages)
            {
                Assert.True(File.Exists(pkg.CatalogPackage.FullPath), $"Missing catalog file {pkg.CatalogPackage.FileName}");
                Assert.Equal(RouterOsVersion.Parse("6.49.23"), pkg.CatalogPackage.Version);
            }

            // Phase B+C+D via production upgrade executor (backup gate → fetch → reboot → verify).
            rebootStartedAt = DateTimeOffset.UtcNow;
            DateTimeOffset reconnectWaitStart = rebootStartedAt;
            using (DeviceLogScope scope = DeviceLogScope.Begin(job, deviceLogWriter, new DeviceLogIdentityRegistry()))
            {
                Microsoft.Extensions.Logging.ILogger jobLogger =
                    loggerFactory.CreateLogger("MikrotikWorker.Scheduling.RouterJobExecutor");
                using (DeviceLogging.BeginServiceOnly())
                {
                    jobLogger.LogInformation(
                        "Router job started. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp);
                }

                jobLogger.LogInformation("Router job started");
                scope.SetRouterIdentity(beforeInfo.Identity);
                jobLogger.LogInformation("Device identity: {RouterIdentity}", beforeInfo.Identity);

                upgradeResult = await upgradeExecutor.ExecuteAsync(
                    connection,
                    job,
                    beforeInfo,
                    assessment,
                    mikrotik.Username,
                    password,
                    static () => true,
                    CancellationToken.None);

                using (DeviceLogging.BeginServiceSummary())
                {
                    jobLogger.LogInformation(
                        "Final upgrade outcome: {UpgradeOutcome} (version={PostUpgradeVersion})",
                        upgradeResult.Outcome,
                        upgradeResult.PostUpgradeVersion);
                }
            }

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

        await deviceLogWriter.StopAsync(CancellationToken.None);
        deviceLogWriter.Dispose();
        await Log.CloseAndFlushAsync();
        string deviceLogPath = Path.Combine(
            deviceLogWriter.DeviceLogDirectory,
            DeviceLogNaming.BuildDeviceLogFileName(beforeInfo.Identity, IPAddress.Parse(LiveHost)));
        string deviceLogContent = File.Exists(deviceLogPath)
            ? await File.ReadAllTextAsync(deviceLogPath)
            : string.Empty;

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

        string[] exportFiles = Directory.GetFiles(Path.Combine(backupRoot, "Exports"), "*.rsc")
            .Where(static p => new FileInfo(p).Length > 0)
            .ToArray();
        string[] binaryFiles = Directory.GetFiles(Path.Combine(backupRoot, "Binary"), "*.backup")
            .Where(static p => new FileInfo(p).Length > 0)
            .ToArray();
        string[] manifests = Directory.GetFiles(Path.Combine(backupRoot, "Manifests"), "*.json")
            .Where(static p => new FileInfo(p).Length > 0)
            .ToArray();
        Assert.NotEmpty(exportFiles);
        Assert.NotEmpty(binaryFiles);
        Assert.NotEmpty(manifests);

        BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllTextAsync(manifests.OrderByDescending(File.GetLastWriteTimeUtc).First()))!;
        Assert.Equal(LiveHost, manifest.RouterIp);
        Assert.Equal("pre-upgrade", manifest.Purpose);
        Assert.Equal(beforeInfo.Identity, manifest.RouterIdentity);
        Assert.False(string.IsNullOrWhiteSpace(manifest.ExportSha256));
        Assert.False(string.IsNullOrWhiteSpace(manifest.BinaryBackupSha256));
        Assert.True(manifest.ExportSizeBytes > 0);
        Assert.True(manifest.BinaryBackupSizeBytes > 0);
        Assert.Equal("SucceededWithRemoteCleanup", manifest.BackupCompletionStatus);

        string expectedIdentityIpPrefix =
            DeviceLogNaming.BuildDeviceStem(beforeInfo.Identity, IPAddress.Parse(LiveHost)) + "_";
        Assert.StartsWith(expectedIdentityIpPrefix, manifest.ExportFileName, StringComparison.Ordinal);
        Assert.StartsWith(expectedIdentityIpPrefix, manifest.BinaryBackupFileName, StringComparison.Ordinal);
        Assert.Contains(LiveHost, manifest.ExportFileName, StringComparison.Ordinal);
        Assert.Equal(
            Path.GetFileNameWithoutExtension(manifest.ExportFileName),
            Path.GetFileNameWithoutExtension(manifest.BinaryBackupFileName));
        Assert.EndsWith(".rsc", manifest.ExportFileName, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".backup", manifest.BinaryBackupFileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(job.RouterName, manifest.ExportFileName, StringComparison.Ordinal);

        string exportPath = Path.Combine(backupRoot, "Exports", manifest.ExportFileName);
        string binaryPath = Path.Combine(backupRoot, "Binary", manifest.BinaryBackupFileName);
        Assert.True(File.Exists(exportPath));
        Assert.True(File.Exists(binaryPath));
        Assert.Equal(manifest.ExportSha256, ComputeSha256Hex(exportPath));
        Assert.Equal(manifest.BinaryBackupSha256, ComputeSha256Hex(binaryPath));

        if (upgradeResult.Outcome != UpgradeOutcome.UpgradeSucceeded)
        {
            await WriteSummaryAsync(new
            {
                RouterIp = LiveHost,
                Status = upgradeResult.Outcome.ToString(),
                Detail = Sanitize(upgradeResult.Detail ?? string.Empty, password),
                BeforeVersion = beforeInfo.Version,
                AfterVersion = afterInfo?.Version,
                Architecture = beforeInfo.ArchitectureName,
                SelectedPackages = resolution.Packages.Select(p => new
                {
                    p.InstalledPackageName,
                    p.CatalogPackage.FileName,
                    p.CatalogPackage.SizeBytes,
                    p.RelativeUrlPath,
                }).ToArray(),
                BeforePackages = beforePackages.Select(p => new
                {
                    p.Name,
                    p.Version,
                    p.Disabled,
                    p.Bundle,
                    p.IsBundledComponent,
                }).ToArray(),
                AfterPackages = afterPackages?.Select(p => new
                {
                    p.Name,
                    p.Version,
                    p.Disabled,
                    p.Bundle,
                    p.IsBundledComponent,
                }).ToArray(),
                RebootInitiated = upgradeResult.RebootInitiated,
                RebootStartedAtUtc = rebootStartedAt,
                ReconnectDurationSeconds = reconnectDuration.TotalSeconds,
                AdvertisedBaseUrl = advertisedBaseUrl,
                DeviceLogPath = deviceLogPath,
                DeviceLogExcerpt = Sanitize(
                    deviceLogContent.Length <= 8000 ? deviceLogContent : deviceLogContent[^8000..],
                    password),
                OtherRoutersModified = false,
                RouterboardUpgradeExecuted = false,
            });
        }

        Assert.True(
            upgradeResult.Outcome == UpgradeOutcome.UpgradeSucceeded,
            $"Upgrade outcome={upgradeResult.Outcome}; detail={Sanitize(upgradeResult.Detail ?? string.Empty, password)}");
        Assert.True(upgradeResult.RebootInitiated);
        Assert.NotNull(afterInfo);
        Assert.True(RouterOsVersion.TryParse(afterInfo!.Version, out RouterOsVersion afterVersion));
        Assert.Equal(RouterOsVersion.Parse("6.49.23"), afterVersion);
        Assert.Equal("mipsbe", afterInfo.ArchitectureName, ignoreCase: true);
        Assert.Equal("hEX PoE", afterInfo.BoardName, ignoreCase: true);
        Assert.Equal("960PGS", afterInfo.RouterboardModel, ignoreCase: true);

        foreach (ResolvedUpgradePackage required in resolution.Packages)
        {
            InstalledPackageInfo? found = afterPackages!.FirstOrDefault(p =>
                p.Name.Equals(required.InstalledPackageName, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(found);
            Assert.True(RouterOsVersion.TryParse(found!.Version, out RouterOsVersion pkgVer));
            Assert.Equal(RouterOsVersion.Parse("6.49.23"), pkgVer);
        }

        foreach (InstalledPackageInfo afterPkg in afterPackages!)
        {
            Assert.True(
                RouterOsVersion.TryParse(afterPkg.Version, out RouterOsVersion afterPkgVer),
                $"Unparseable package version for {afterPkg.Name}");
            Assert.Equal(RouterOsVersion.Parse("6.49.23"), afterPkgVer);
        }

        Assert.False(string.IsNullOrWhiteSpace(deviceLogContent), $"Missing production device log at {deviceLogPath}");
        Assert.Contains("Backup prerequisite passed", deviceLogContent, StringComparison.Ordinal);
        Assert.Contains("Fetching firmware package", deviceLogContent, StringComparison.Ordinal);
        Assert.Contains("Initiating reboot for package installation", deviceLogContent, StringComparison.Ordinal);
        Assert.Contains("Reconnected successfully", deviceLogContent, StringComparison.Ordinal);
        Assert.Contains("Final upgrade outcome", deviceLogContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UpgradeSucceeded", deviceLogContent, StringComparison.Ordinal);
        Assert.DoesNotContain(password, deviceLogContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", deviceLogContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/system/routerboard/upgrade", deviceLogContent, StringComparison.Ordinal);

        await WriteSummaryAsync(new
        {
            RouterIp = LiveHost,
            Status = "UpgradeSucceeded",
            BeforeVersion = beforeInfo.Version,
            AfterVersion = afterInfo.Version,
            Architecture = afterInfo.ArchitectureName,
            BoardName = afterInfo.BoardName,
            RouterboardModel = afterInfo.RouterboardModel,
            BeforePackages = beforePackages.Select(p => new
            {
                p.Name,
                p.Version,
                p.Disabled,
                p.Bundle,
                p.IsBundledComponent,
            }).ToArray(),
            SelectedPackages = resolution.Packages.Select(p => new
            {
                p.InstalledPackageName,
                p.CatalogPackage.FileName,
                p.CatalogPackage.SizeBytes,
                p.RelativeUrlPath,
            }).ToArray(),
            AfterPackages = afterPackages!.Select(p => new
            {
                p.Name,
                p.Version,
                p.Disabled,
                p.Bundle,
                p.IsBundledComponent,
            }).ToArray(),
            RouterboardBefore = new
            {
                beforeInfo.RouterboardModel,
                beforeInfo.RouterboardFirmwareType,
                beforeInfo.RouterboardCurrentFirmware,
                beforeInfo.RouterboardUpgradeFirmware,
                beforeInfo.RouterboardFactoryFirmware,
            },
            RouterboardAfter = new
            {
                afterInfo.RouterboardModel,
                afterInfo.RouterboardFirmwareType,
                afterInfo.RouterboardCurrentFirmware,
                afterInfo.RouterboardUpgradeFirmware,
                afterInfo.RouterboardFactoryFirmware,
            },
            PriorVerifiedBackup = new
            {
                ExportFileName = "LIVE-TEST_198.18.0.83_20261110T183422050Z.rsc",
                BinaryFileName = "LIVE-TEST_198.18.0.83_20261110T183422050Z.backup",
                ExportSha256 = "3e7f1314387ee62724fe54f23dd10c42552e4c0e8eae7d2c4866421c2812994f",
                BinarySha256 = "c1acc3a9397357471e358f4bbcf19dd5db96ad30d48bb225de2d63cadf63b156",
                Status = "SucceededWithRemoteCleanup",
            },
            Backup = new
            {
                manifest.ExportFileName,
                manifest.ExportSizeBytes,
                manifest.ExportSha256,
                manifest.BinaryBackupFileName,
                manifest.BinaryBackupSizeBytes,
                manifest.BinaryBackupSha256,
                manifest.BackupTimestampUtc,
                manifest.BackupCompletionStatus,
                ManifestPath = manifests.OrderByDescending(File.GetLastWriteTimeUtc).First(),
                ExportPath = exportPath,
                BinaryPath = binaryPath,
            },
            RebootStartedAtUtc = rebootStartedAt,
            ReconnectDurationSeconds = reconnectDuration.TotalSeconds,
            UpgradeOutcome = upgradeResult.Outcome.ToString(),
            upgradeResult.Detail,
            AdvertisedBaseUrl = advertisedBaseUrl,
            OtherRoutersModified = false,
            RouterboardUpgradeExecuted = false,
            DeviceLogPath = deviceLogPath,
            DeviceLogBytes = deviceLogContent.Length,
            ApplicationLogDirectory = liveLogRoot,
            ApplicationLogFiles = Directory.GetFiles(liveLogRoot, "application-*.log"),
        });

        string markerPath = Path.Combine(Path.GetTempPath(), "mikrotik-live-upgrade-log-paths.txt");
        string[] appLogs = Directory.GetFiles(liveLogRoot, "application-*.log");
        Assert.NotEmpty(appLogs);
        string mainLogPath = appLogs.OrderByDescending(File.GetLastWriteTimeUtc).First();
        await File.WriteAllTextAsync(
            markerPath,
            string.Join(Environment.NewLine, [mainLogPath, deviceLogPath]));
    }

    private sealed class LiveUpgradeHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "MikrotikWorker.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
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

    private static void VerifyPriorCentralBackupIntact()
    {
        string backupRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "Backups-LiveReadOnly"));
        const string baseName = "LIVE-TEST_198.18.0.83_20261110T183422050Z";
        string exportPath = Path.Combine(backupRoot, "Exports", baseName + ".rsc");
        string binaryPath = Path.Combine(backupRoot, "Binary", baseName + ".backup");
        string manifestPath = Path.Combine(backupRoot, "Manifests", baseName + ".json");

        Assert.True(File.Exists(exportPath), $"Missing prior export backup: {exportPath}");
        Assert.True(File.Exists(binaryPath), $"Missing prior binary backup: {binaryPath}");
        Assert.True(File.Exists(manifestPath), $"Missing prior manifest: {manifestPath}");

        BackupManifest prior = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))!;
        Assert.Equal(LiveHost, prior.RouterIp);
        Assert.Contains("6.49.19", prior.RouterOsVersionBeforeUpgrade, StringComparison.Ordinal);
        Assert.Equal("mipsbe", prior.Architecture, ignoreCase: true);
        Assert.Equal("SucceededWithRemoteCleanup", prior.BackupCompletionStatus);
        Assert.True(new FileInfo(exportPath).Length > 0);
        Assert.True(new FileInfo(binaryPath).Length > 0);
        Assert.Equal(prior.ExportSha256, ComputeSha256Hex(exportPath));
        Assert.Equal(prior.BinaryBackupSha256, ComputeSha256Hex(binaryPath));
        Assert.Equal(
            "3e7f1314387ee62724fe54f23dd10c42552e4c0e8eae7d2c4866421c2812994f",
            prior.ExportSha256,
            ignoreCase: true);
        Assert.Equal(
            "c1acc3a9397357471e358f4bbcf19dd5db96ad30d48bb225de2d63cadf63b156",
            prior.BinaryBackupSha256,
            ignoreCase: true);
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
