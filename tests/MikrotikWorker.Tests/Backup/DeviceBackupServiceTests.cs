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

namespace MikrotikWorker.Tests.Backup;
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
        Assert.True(result.RemoteCleanupSucceeded);
        Assert.Equal(2, ops.RemovedFiles.Count);
        Assert.Empty(ops.Files.Keys);
        Assert.StartsWith("CORE-01_", Path.GetFileName(result.ExportLocalPath), StringComparison.Ordinal);
        Assert.StartsWith("CORE-01_", Path.GetFileName(result.BinaryLocalPath), StringComparison.Ordinal);
        Assert.Equal(
            Path.GetFileNameWithoutExtension(result.ExportLocalPath),
            Path.GetFileNameWithoutExtension(result.BinaryLocalPath));
        Assert.Equal("CORE-01", result.Manifest.RouterIdentity);
        Assert.Equal("CORE-01", result.Manifest.RouterName);
        Assert.Contains("10.0.0.50", Path.GetFileName(result.ExportLocalPath!), StringComparison.Ordinal);
        Assert.StartsWith("CORE-01_10.0.0.50_", Path.GetFileName(result.ExportLocalPath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalVerificationFailure_DoesNotDeleteRemoteFiles()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-no-delete-").FullName;
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
        Assert.Empty(ops.RemovedFiles);
        Assert.True(ops.Files.Count >= 1);
        Assert.Contains(result.RemainingRemoteFiles, static f => f.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.RemoteCleanupFailed);
    }

    [Fact]
    public async Task FailedBinaryDownload_RetainsRemoteBackup()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-bin-dl-fail-").FullName;
        RecordingRouterOps ops = new();
        InMemoryFileTransfer transfer = new() { FailOnRemoteSuffix = ".backup" };
        DeviceBackupService service = CreateService(backupRoot, ops, transfer);

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(ops.RemovedFiles);
        Assert.Contains(result.RemainingRemoteFiles, static f => f.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RemainingRemoteFiles, static f => f.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, result.RemainingRemoteFiles.Count);
        Assert.Contains(ops.Files.Keys, static k => k.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailedExportDownload_RetainsRemoteExport()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-exp-dl-fail-").FullName;
        RecordingRouterOps ops = new();
        InMemoryFileTransfer transfer = new() { FailOnRemoteSuffix = ".rsc" };
        DeviceBackupService service = CreateService(backupRoot, ops, transfer);

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(ops.RemovedFiles);
        Assert.Single(result.RemainingRemoteFiles);
        Assert.EndsWith(".rsc", result.RemainingRemoteFiles[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ops.Files.Keys, static k => k.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ops.Files.Keys, static k => k.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemoteDeletionFailure_RetainsCentralBackups_AndFails()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-cleanup-fail-").FullName;
        RecordingRouterOps ops = new() { RemoveThrows = true };
        DeviceBackupService service = CreateService(backupRoot, ops, new InMemoryFileTransfer());

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.RemoteCleanupFailed);
        Assert.True(File.Exists(result.ExportLocalPath));
        Assert.True(File.Exists(result.BinaryLocalPath));
        Assert.True(File.Exists(result.ManifestLocalPath));
        Assert.NotEmpty(result.RemainingRemoteFiles);
        Assert.Contains("Remote cleanup failed", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RouterDisconnectDuringCleanup_ReportsCleanupFailureWithoutMaskingCentralBackup()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-cleanup-disconnect-").FullName;
        RecordingRouterOps ops = new() { RemoveExceptionFactory = static () => new IOException("router disconnected") };
        DeviceBackupService service = CreateService(backupRoot, ops, new InMemoryFileTransfer());

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.RemoteCleanupFailed);
        Assert.Contains("router disconnected", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ExportLocalPath));
        Assert.True(File.Exists(result.BinaryLocalPath));
        Assert.Equal(2, result.RemainingRemoteFiles.Count);
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
        Assert.Empty(ops.RemovedFiles);
        Assert.True(ops.Files.Count >= 1);
    }

    [Fact]
    public async Task Cleanup_RemovesOnlyJobBackupFiles()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-unrelated-").FullName;
        RecordingRouterOps ops = new();
        ops.SeedFile("unrelated-config.rsc", 99);
        ops.SeedFile("other.backup", 88);
        DeviceBackupService service = CreateService(backupRoot, ops, new InMemoryFileTransfer());

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(2, ops.RemovedFiles.Count);
        Assert.DoesNotContain("unrelated-config.rsc", ops.RemovedFiles);
        Assert.DoesNotContain("other.backup", ops.RemovedFiles);
        Assert.True(ops.Files.ContainsKey("unrelated-config.rsc"));
        Assert.True(ops.Files.ContainsKey("other.backup"));
    }

    [Fact]
    public async Task CancellationAfterVerification_StillAttemptsBoundedCleanup()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-cancel-cleanup-").FullName;
        RecordingRouterOps ops = new() { RemoveDelay = TimeSpan.FromMilliseconds(80) };
        using CancellationTokenSource cts = new();
        DeviceBackupService service = CreateService(
            backupRoot,
            ops,
            new InMemoryFileTransfer(),
            scpTimeout: TimeSpan.FromSeconds(30));

        Task<DeviceBackupResult> task = service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            cts.Token);

        while (ops.RemovedFiles.Count == 0 && !task.IsCompleted)
        {
            await Task.Delay(20);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);

        string[] localFiles = Directory.GetFiles(backupRoot, "*", SearchOption.AllDirectories);
        Assert.Contains(localFiles, static p => p.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(localFiles, static p => p.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(localFiles, static p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, ops.RemovedFiles.Count);
        Assert.DoesNotContain(ops.Files.Keys, static k =>
            k.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase)
            || k.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancellationDuringDownload_RetainsRemoteArtefacts()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-cancel-dl-").FullName;
        RecordingRouterOps ops = new();
        using CancellationTokenSource cts = new();
        InMemoryFileTransfer transfer = new()
        {
            DownloadDelay = TimeSpan.FromSeconds(5),
            OnDownloadStarted = _ => cts.Cancel(),
        };
        DeviceBackupService service = CreateService(backupRoot, ops, transfer);

        Task<DeviceBackupResult> task = service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Empty(ops.RemovedFiles);
        Assert.Contains(ops.Files.Keys, static k => k.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BoundedCleanupTimeout_ReportsCleanupFailure()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-cleanup-timeout-").FullName;
        RecordingRouterOps ops = new() { RemoveDelay = TimeSpan.FromSeconds(2) };
        DeviceBackupService service = CreateService(
            backupRoot,
            ops,
            new InMemoryFileTransfer(),
            scpTimeout: TimeSpan.FromMilliseconds(50));

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.RemoteCleanupFailed);
        Assert.Contains("timed out", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.ExportLocalPath));
        Assert.True(File.Exists(result.BinaryLocalPath));
        Assert.NotEmpty(result.RemainingRemoteFiles);
    }

    [Fact]
    public async Task ConcurrentBackupsOnSameRouter_DoNotDeleteEachOthersArtefacts()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-concurrent-").FullName;
        SharedRouterFileStore store = new();
        store.Set("unrelated.rsc", 10);
        RecordingRouterOps opsA = new(store);
        RecordingRouterOps opsB = new(store);
        DateTimeOffset stamp = new(2026, 11, 10, 19, 0, 0, 100, TimeSpan.Zero);
        DeviceBackupService serviceA = CreateService(backupRoot, opsA, new InMemoryFileTransfer(), fixedTime: stamp);
        DeviceBackupService serviceB = CreateService(
            backupRoot,
            opsB,
            new InMemoryFileTransfer(),
            fixedTime: stamp.AddMilliseconds(1));

        MikrotikScheduledJob job = CreateJob();
        MikrotikDeviceInfo device = CreateDevice();
        Task<DeviceBackupResult> taskA = serviceA.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(), job, device, "admin", "secret", CancellationToken.None);
        Task<DeviceBackupResult> taskB = serviceB.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(), job, device, "admin", "secret", CancellationToken.None);
        DeviceBackupResult[] results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, static r => Assert.True(r.Succeeded, r.FailureReason));
        Assert.Equal(2, opsA.RemovedFiles.Count);
        Assert.Equal(2, opsB.RemovedFiles.Count);
        Assert.Empty(opsA.RemovedFiles.Intersect(opsB.RemovedFiles, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("unrelated.rsc", opsA.RemovedFiles);
        Assert.DoesNotContain("unrelated.rsc", opsB.RemovedFiles);
    }

    [Fact]
    public async Task PartialBinaryCreationFailure_RetainsOwnedExportOnly()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-partial-bin-").FullName;
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
        Assert.Empty(ops.RemovedFiles);
        Assert.Single(result.RemainingRemoteFiles);
        Assert.EndsWith(".rsc", result.RemainingRemoteFiles[0], StringComparison.OrdinalIgnoreCase);
        Assert.False(result.RemoteCleanupFailed);
    }

    [Fact]
    public async Task ApplicationShutdown_DoesNotLeaveUnboundedCleanupTask()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-shutdown-cleanup-").FullName;
        RecordingRouterOps ops = new() { RemoveDelay = TimeSpan.FromSeconds(5) };
        DeviceBackupService service = CreateService(
            backupRoot,
            ops,
            new InMemoryFileTransfer(),
            scpTimeout: TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource shutdown = new();

        Task<DeviceBackupResult> task = service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            shutdown.Token);

        while (ops.RemovedFiles.Count == 0 && !task.IsCompleted)
        {
            await Task.Delay(10);
        }

        shutdown.Cancel();
        // Bounded cleanup runs, then caller cancellation is rethrown (not returned as BackupFailed).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task CancellationAfterVerification_WithCleanupFailure_PropagatesCancellation()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-cancel-cleanup-fail-").FullName;
        using CancellationTokenSource cts = new();
        RecordingRouterOps ops = new()
        {
            RemoveThrows = true,
            OnRemove = () => cts.Cancel(),
        };
        DeviceBackupService service = CreateService(backupRoot, ops, new InMemoryFileTransfer());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.CreatePreUpgradeBackupAsync(
                new TikFakeConnection(),
                CreateJob(),
                CreateDevice(),
                "admin",
                "secret",
                cts.Token));

        string[] localFiles = Directory.GetFiles(backupRoot, "*", SearchOption.AllDirectories);
        Assert.Contains(localFiles, static p => p.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(localFiles, static p => p.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(ops.RemovedFiles);
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
    public void BackupFileNaming_UsesSanitisedIdentityAndSharedTimestamp()
    {
        DateTimeOffset stamp = new(2026, 11, 10, 18, 53, 28, 655, TimeSpan.Zero);
        IPAddress ip = IPAddress.Parse("10.0.0.50");
        string baseName = BackupFileNaming.BuildBaseName("MikroTik", ip, stamp);
        Assert.Equal("MikroTik_10.0.0.50_20261110_185328_655", baseName);
        Assert.Equal(baseName + ".rsc", baseName + ".rsc");
        Assert.Equal(baseName + ".backup", baseName + ".backup");
    }

    [Fact]
    public void BackupFileNaming_SanitisesUnsafeIdentityCharacters()
    {
        IPAddress ip = IPAddress.Parse("10.0.0.7");
        string baseName = BackupFileNaming.BuildBaseName(@"CORE:01/..\evil", ip, DateTimeOffset.Parse("2026-11-10T12:00:00.123Z"));
        Assert.Equal("CORE_01_.._evil_10.0.0.7_20261110_120000_123", baseName);
        Assert.Equal(-1, baseName.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.DoesNotContain(":", baseName, StringComparison.Ordinal);
        Assert.DoesNotContain("/", baseName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", baseName, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupFileNaming_EmptyOrInvalidIdentity_FallsBackSafely()
    {
        DateTimeOffset stamp = DateTimeOffset.Parse("2026-11-10T12:00:00.001Z");
        IPAddress ip = IPAddress.Parse("10.0.0.50");
        Assert.Equal("router_10.0.0.50_20261110_120000_001", BackupFileNaming.BuildBaseName(null, ip, stamp));
        Assert.Equal("router_10.0.0.50_20261110_120000_001", BackupFileNaming.BuildBaseName("   ", ip, stamp));
        Assert.Equal("router_10.0.0.50_20261110_120000_001", BackupFileNaming.BuildBaseName("@@@", ip, stamp));
    }

    [Fact]
    public void BackupFileNaming_MillisecondPrecision_PreventsCollisions()
    {
        IPAddress ip = IPAddress.Parse("10.0.0.50");
        string a = BackupFileNaming.BuildBaseName("MikroTik", ip, DateTimeOffset.Parse("2026-11-10T12:00:00.001Z"));
        string b = BackupFileNaming.BuildBaseName("MikroTik", ip, DateTimeOffset.Parse("2026-11-10T12:00:00.002Z"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BackupFileNaming_AllocateUniqueBaseName_KeepsAllThreeArtefactsOnSameStem()
    {
        string root = Directory.CreateTempSubdirectory("bk-stem-").FullName;
        string exports = Path.Combine(root, "Exports");
        string binary = Path.Combine(root, "Binary");
        string manifests = Path.Combine(root, "Manifests");
        DateTimeOffset stamp = DateTimeOffset.Parse("2026-11-10T12:00:00.100Z");
        IPAddress ip = IPAddress.Parse("10.0.0.50");
        using BackupStemReservation first = BackupFileNaming.AllocateUniqueBaseName("MikroTik", ip, stamp, exports, binary, manifests);
        // Marker reserves the stem; next allocation must bump the shared stem for all three extensions.
        using BackupStemReservation second = BackupFileNaming.AllocateUniqueBaseName("MikroTik", ip, stamp, exports, binary, manifests);
        Assert.Equal("MikroTik_10.0.0.50_20261110_120000_100", first.Stem);
        Assert.Equal("MikroTik_10.0.0.50_20261110_120000_100_1", second.Stem);
        Assert.True(File.Exists(first.MarkerPath));
        Assert.True(File.Exists(second.MarkerPath));
        Assert.False(File.Exists(second.ExportLocalPath));
        Assert.False(File.Exists(second.BinaryLocalPath));
        Assert.False(File.Exists(second.ManifestLocalPath));
    }

    [Fact]
    public async Task SuccessfulBackup_UsesIdentityFilenames_AndCleansExactRemoteFiles()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-identity-").FullName;
        RecordingRouterOps ops = new();
        ops.SeedFile("unrelated.rsc", 10);
        DeviceBackupService service = CreateService(
            backupRoot,
            ops,
            new InMemoryFileTransfer(),
            fixedTime: new DateTimeOffset(2026, 11, 10, 19, 0, 0, 123, TimeSpan.Zero));

        MikrotikDeviceInfo device = new()
        {
            Identity = "MikroTik",
            Version = "6.49.23",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardModel = "960PGS",
        };
        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            device,
            "admin",
            "secret",
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("MikroTik_10.0.0.50_20261110_190000_123.rsc", result.Manifest!.ExportFileName);
        Assert.Equal("MikroTik_10.0.0.50_20261110_190000_123.backup", result.Manifest.BinaryBackupFileName);
        Assert.Equal("MikroTik_10.0.0.50_20261110_190000_123.rsc", result.Manifest.RemoteExportFileName);
        Assert.Equal("MikroTik_10.0.0.50_20261110_190000_123.backup", result.Manifest.RemoteBinaryFileName);
        Assert.Equal(
            ["MikroTik_10.0.0.50_20261110_190000_123.rsc", "MikroTik_10.0.0.50_20261110_190000_123.backup"],
            ops.RemovedFiles);
        Assert.True(ops.Files.ContainsKey("unrelated.rsc"));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("export-body"))).ToLowerInvariant(),
            result.Manifest.ExportSha256);
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
        Assert.Empty(ops.RemovedFiles);
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
        IMikrotikFileTransferClient transfer,
        DateTimeOffset? fixedTime = null,
        TimeSpan? scpTimeout = null) =>
        new(
            ops,
            transfer,
            Options.Create(new FirmwareUpgradeOptions
            {
                BackupDirectory = backupRoot,
                BackupFileReadyTimeout = TimeSpan.FromSeconds(2),
                BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(10),
                ScpTimeout = scpTimeout ?? TimeSpan.FromSeconds(5),
            }),
            fixedTime is null ? TimeProvider.System : new FakeTimeProvider(fixedTime.Value),
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
            Version = "6.49.23",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardModel = "960PGS",
        };

    private sealed class SharedRouterFileStore
    {
        private readonly Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public Dictionary<string, long> Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return new Dictionary<string, long>(_files, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public void Set(string name, long size)
        {
            lock (_gate)
            {
                _files[name] = size;
            }
        }

        public bool TryGet(string name, out long size)
        {
            lock (_gate)
            {
                return _files.TryGetValue(name, out size);
            }
        }

        public void Remove(string name)
        {
            lock (_gate)
            {
                _files.Remove(name);
            }
        }
    }

    private sealed class RecordingRouterOps : IMikrotikRouterOperations
    {
        private readonly SharedRouterFileStore _store;

        public RecordingRouterOps(SharedRouterFileStore? store = null)
        {
            _store = store ?? new SharedRouterFileStore();
        }

        public bool ExportThrows { get; init; }

        public bool RemoveThrows { get; init; }

        public Func<Exception>? RemoveExceptionFactory { get; init; }

        public Action? OnRemove { get; init; }

        public long BinarySize { get; init; } = 12;

        public TimeSpan ExportDelay { get; init; }

        public TimeSpan RemoveDelay { get; init; }

        public bool FetchCalled { get; private set; }

        public bool RebootCalled { get; private set; }

        public Dictionary<string, long> Files => _store.Snapshot;

        public List<string> RemovedFiles { get; } = [];

        public void SeedFile(string name, long size) => _store.Set(name, size);

        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDevice());

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([new("routeros-mipsbe", "6.49.23", false)]);

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

            _store.Set(fileNameWithoutExtension + ".rsc", Encoding.UTF8.GetByteCount("export-body"));
        }

        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default)
        {
            _store.Set(fileNameWithoutExtension + ".backup", BinarySize);
            return Task.CompletedTask;
        }

        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_store.TryGet(fileName, out long size))
            {
                return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, size, "*1"));
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

        public async Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            RemovedFiles.Add(fileName);
            OnRemove?.Invoke();
            if (RemoveDelay > TimeSpan.Zero)
            {
                await Task.Delay(RemoveDelay, cancellationToken);
            }

            if (RemoveExceptionFactory is not null)
            {
                throw RemoveExceptionFactory();
            }

            if (RemoveThrows)
            {
                throw new IOException("remove failed");
            }

            _store.Remove(fileName);
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

        public string? FailOnRemoteSuffix { get; init; }

        public bool CorruptLocalSize { get; init; }

        public TimeSpan DownloadDelay { get; init; }

        public Action<string>? OnDownloadStarted { get; init; }

        public List<string> Downloads { get; } = [];

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
            OnDownloadStarted?.Invoke(remoteFileName);

            if (DownloadDelay > TimeSpan.Zero)
            {
                await Task.Delay(DownloadDelay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (FailOnDownload
                || (FailOnRemoteSuffix is not null
                    && remoteFileName.EndsWith(FailOnRemoteSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("sftp authentication failed");
            }

            Downloads.Add(remoteFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            byte[] payload = remoteFileName.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetBytes(CorruptLocalSize ? "x" : "export-body")
                : Enumerable.Repeat((byte)7, CorruptLocalSize ? 1 : 12).ToArray();
            // Match production SFTP: never overwrite an existing local artefact.
            await using FileStream output = new(localFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(payload, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

}
