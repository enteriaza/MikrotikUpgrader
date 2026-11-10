using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests;

public sealed class DeviceBackupServiceTests
{
    [Fact]
    public async Task SuccessfulExportAndBinaryBackup_WritesManifestAndHashes()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-ok-").FullName;
        RecordingRouterOps ops = new();
        InMemoryFileTransfer transfer = new();
        DeviceBackupService service = CreateService(backupRoot, ops, transfer);

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(File.Exists(result.ExportLocalPath));
        Assert.True(File.Exists(result.BinaryLocalPath));
        Assert.True(File.Exists(result.ManifestLocalPath));
        Assert.Equal(2, transfer.Downloads.Count);
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(result.ManifestLocalPath!), StringComparison.Ordinal);
        Assert.Equal("pre-upgrade", result.Manifest!.Purpose);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("export-body"))).ToLowerInvariant(),
            result.Manifest.ExportSha256);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task ExportFailure_BlocksAndDoesNotFetch()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-export-fail-").FullName;
        RecordingRouterOps ops = new() { ExportThrows = true };
        DeviceBackupService service = CreateService(backupRoot, ops, new InMemoryFileTransfer());

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task EmptyBinaryBackup_Fails()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-empty-").FullName;
        RecordingRouterOps ops = new() { BinarySize = 0 };
        DeviceBackupService service = CreateService(backupRoot, ops, new InMemoryFileTransfer());

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(
            result.FailureReason!.Contains("empty", StringComparison.OrdinalIgnoreCase)
            || result.FailureReason.Contains("not ready", StringComparison.OrdinalIgnoreCase),
            result.FailureReason);
    }

    [Fact]
    public async Task RetrievalFailure_PreservesPartialFilesPolicy()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-scp-fail-").FullName;
        RecordingRouterOps ops = new();
        InMemoryFileTransfer transfer = new() { FailOnDownload = true };
        DeviceBackupService service = CreateService(backupRoot, ops, transfer);

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(ops.FetchCalled);
    }

    [Fact]
    public void BackupFileNaming_NeverOverwrites()
    {
        string dir = Directory.CreateTempSubdirectory("bk-name-").FullName;
        string first = BackupFileNaming.EnsureUniquePath(dir, "same.rsc");
        File.WriteAllText(first, "a");
        string second = BackupFileNaming.EnsureUniquePath(dir, "same.rsc");
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task SftpAuthenticationFailure_StopsWithoutFetch()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-auth-fail-").FullName;
        RecordingRouterOps ops = new();
        InMemoryFileTransfer transfer = new() { FailOnDownload = true };
        DeviceBackupService service = CreateService(backupRoot, ops, transfer);

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "wrong-password",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task SizeMismatchAfterRetrieval_FailsHashGate()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-size-mismatch-").FullName;
        RecordingRouterOps ops = new();
        InMemoryFileTransfer transfer = new() { CorruptLocalSize = true };
        DeviceBackupService service = CreateService(backupRoot, ops, transfer);

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("size mismatch", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(ops.FetchCalled);
    }

    [Fact]
    public async Task CancellationDuringBackup_Propagates()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-cancel-").FullName;
        RecordingRouterOps ops = new() { ExportDelay = TimeSpan.FromSeconds(5) };
        using CancellationTokenSource cts = new();
        DeviceBackupService service = CreateService(backupRoot, ops, new InMemoryFileTransfer());

        Task<DeviceBackupResult> task = service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            cts.Token);
        await Task.Delay(30);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.False(ops.FetchCalled);
    }

    private static DeviceBackupService CreateService(
        string backupRoot,
        RecordingRouterOps ops,
        IMikrotikFileTransferClient transfer) =>
        new(
            ops,
            transfer,
            Options.Create(new FirmwareUpgradeOptions
            {
                BackupDirectory = backupRoot,
                BackupFileReadyTimeout = TimeSpan.FromSeconds(2),
                BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(10),
                ScpTimeout = TimeSpan.FromSeconds(5),
            }),
            TimeProvider.System,
            NullLogger<DeviceBackupService>.Instance);

    private static MikrotikScheduledJob CreateJob() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "CORE-01",
            RouterIp = IPAddress.Parse("10.0.0.50"),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    private static MikrotikDeviceInfo CreateDevice() =>
        new()
        {
            Identity = "CORE-01",
            Version = "6.49.19",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardModel = "960PGS",
        };

    private sealed class RecordingRouterOps : IMikrotikRouterOperations
    {
        public bool ExportThrows { get; init; }

        public long BinarySize { get; init; } = 12;

        public TimeSpan ExportDelay { get; init; }

        public bool FetchCalled { get; private set; }

        public bool RebootCalled { get; private set; }

        private readonly Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);

        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDevice());

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([new("routeros", "6.49.19", false)]);

        public async Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default)
        {
            if (ExportDelay > TimeSpan.Zero)
            {
                await Task.Delay(ExportDelay, cancellationToken);
            }

            if (ExportThrows)
            {
                throw new InvalidOperationException("export failed");
            }

            _files[fileNameWithoutExtension + ".rsc"] = Encoding.UTF8.GetByteCount("export-body");
        }

        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default)
        {
            _files[fileNameWithoutExtension + ".backup"] = BinarySize;
            return Task.CompletedTask;
        }

        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            if (_files.TryGetValue(fileName, out long size))
            {
                return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, size));
            }

            return Task.FromResult<RouterFileInfo?>(null);
        }

        public async Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default)
        {
            RouterFileInfo? file = await FindFileAsync(connection, fileName, cancellationToken);
            if (file is null || file.SizeBytes <= 0)
            {
                throw new TimeoutException("not ready");
            }
        }

        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            return Task.CompletedTask;
        }

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryFileTransfer : IMikrotikFileTransferClient
    {
        public bool FailOnDownload { get; init; }

        public bool CorruptLocalSize { get; init; }

        public List<string> Downloads { get; } = [];

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
            if (FailOnDownload)
            {
                throw new IOException("sftp authentication failed");
            }

            Downloads.Add(remoteFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            byte[] payload = remoteFileName.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetBytes(CorruptLocalSize ? "x" : "export-body")
                : Enumerable.Repeat((byte)7, CorruptLocalSize ? 1 : 12).ToArray();
            File.WriteAllBytes(localFilePath, payload);
            return Task.CompletedTask;
        }
    }

}
