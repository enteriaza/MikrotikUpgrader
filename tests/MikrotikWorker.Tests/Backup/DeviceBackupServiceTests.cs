// -----------------------------------------------------------------------------
// File: DeviceBackupServiceTests.cs
//
// Description:
// DeviceBackupService behaviour: export, SCP download, remote cleanup, failure modes.
//
// -----------------------------------------------------------------------------
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
/// <summary>
/// Behavioural tests for <c>DeviceBackupService.CreatePreUpgradeBackupAsync</c> and
/// <c>BackupFileNaming</c>: RouterOS export plus binary backup, SFTP download into the central
/// backup tree, size/hash verification, manifest creation, and bounded remote cleanup.
/// All router and transfer I/O is replaced by in-memory fakes, so every test drives the real
/// service against deterministic router state. The recurring invariant is that remote copies are
/// deleted only after the central copies are verified, and only for files this backup created.
/// </summary>
public sealed class DeviceBackupServiceTests
{
    /// <summary>
    /// Happy path: the router creates an export (.rsc) and a binary backup (.backup), both are
    /// downloaded, verified, described in a manifest, and then removed from the router.
    /// Preconditions: Default <c>RecordingRouterOps</c> (export body "export-body", 12-byte binary)
    /// and a fault-free <c>InMemoryFileTransfer</c>; real system clock.
    /// Expected: Result succeeded with all three local files present and exactly two downloads; the
    /// manifest has purpose "pre-upgrade", the SHA-256 of the export body, router identity "CORE-01",
    /// and never contains the password; both remote files are removed; export, binary and manifest
    /// share one stem starting with "CORE-01_10.0.0.50_".
    /// Invariant: A verified backup neither leaks credentials nor triggers a firmware fetch or
    /// reboot.
    /// </summary>
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

    /// <summary>
    /// The files arrive locally with the wrong size, so central verification fails before any remote
    /// cleanup is allowed.
    /// Preconditions: <c>InMemoryFileTransfer.CorruptLocalSize</c> truncates downloads to one byte,
    /// which differs from the size reported by the router.
    /// Expected: Result failed, no remote file removed, the router still lists the .rsc, and
    /// <c>RemoteCleanupFailed</c> stays false because cleanup was never attempted.
    /// Invariant: Router-side copies are the last good evidence and survive until central copies are
    /// verified.
    /// </summary>
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

    /// <summary>
    /// The export downloads fine but the binary (.backup) download fails.
    /// Preconditions: <c>InMemoryFileTransfer.FailOnRemoteSuffix</c> is ".backup".
    /// Expected: Result failed, nothing removed, and both the .rsc and .backup are reported as
    /// remaining (two entries) and still exist on the router.
    /// Invariant: A partial backup never causes remote deletion, even for the leg that was already
    /// downloaded.
    /// </summary>
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

    /// <summary>
    /// The very first download (the .rsc export) fails.
    /// Preconditions: <c>InMemoryFileTransfer.FailOnRemoteSuffix</c> is ".rsc".
    /// Expected: Result failed, nothing removed, exactly one remaining remote file (the .rsc), and no
    /// .backup was ever created on the router.
    /// Invariant: The service stops at the first failed download instead of creating further router
    /// artefacts.
    /// </summary>
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

    /// <summary>
    /// Backup is centrally verified but the router rejects file removal.
    /// Preconditions: <c>RecordingRouterOps.RemoveThrows</c> makes every <c>RemoveFileAsync</c> throw
    /// an IOException.
    /// Expected: Result failed with <c>RemoteCleanupFailed</c> set, the failure reason mentions
    /// "Remote cleanup failed", remaining remote files are listed, and the local export, binary and
    /// manifest all exist.
    /// Invariant: A cleanup failure is surfaced as a failure and never rolls back the verified
    /// central backup.
    /// </summary>
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

    /// <summary>
    /// The router connection drops while remote files are being deleted.
    /// Preconditions: <c>RecordingRouterOps.RemoveExceptionFactory</c> throws IOException("router
    /// disconnected").
    /// Expected: Result failed with <c>RemoteCleanupFailed</c>, the failure reason carries the
    /// "router disconnected" message, local .rsc/.backup exist, and both remote files are reported
    /// remaining.
    /// Invariant: The original cleanup error is not swallowed and the central backup stays intact.
    /// </summary>
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

    /// <summary>
    /// The router export command itself fails.
    /// Preconditions: <c>RecordingRouterOps.ExportThrows</c> is true.
    /// Expected: Result failed; neither a firmware fetch nor a reboot was issued.
    /// Invariant: A failed backup must block every later upgrade action.
    /// </summary>
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

    /// <summary>
    /// The router reports a zero-byte binary backup.
    /// Preconditions: <c>RecordingRouterOps.BinarySize</c> is 0, so the fake <c>WaitForFileAsync</c>
    /// times out with "not ready".
    /// Expected: Result failed and the failure reason mentions "empty" or "not ready".
    /// Invariant: An empty or never-ready backup is never accepted as a valid backup.
    /// </summary>
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

    /// <summary>
    /// Every SFTP download fails.
    /// Preconditions: <c>InMemoryFileTransfer.FailOnDownload</c> is true.
    /// Expected: Result failed, no fetch issued, nothing removed, and at least one file remains on
    /// the router.
    /// Invariant: Partial router artefacts are retained when central retrieval fails.
    /// </summary>
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

    /// <summary>
    /// Other files already on the router must survive cleanup.
    /// Preconditions: The router is seeded with "unrelated-config.rsc" and "other.backup" before the
    /// backup runs.
    /// Expected: Success with exactly two removals (this job's export and binary); both seeded files
    /// remain on the router and were never passed to removal.
    /// Invariant: Cleanup only touches the files this backup created.
    /// </summary>
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

    /// <summary>
    /// The caller cancels while remote deletion is already in progress after central verification.
    /// Preconditions: <c>RemoveDelay</c> is 80 ms per file; the test polls until the first removal
    /// starts and then cancels the token. Transfer timeout is 30 s.
    /// Expected: <c>OperationCanceledException</c> propagates, yet both remote files were removed and
    /// the local .rsc, .backup and .json files remain.
    /// Invariant: Cancellation after verification does not abandon remote cleanup or discard the
    /// central backup.
    /// </summary>
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

    /// <summary>
    /// The caller cancels while the export download is still running.
    /// Preconditions: <c>DownloadDelay</c> is 5 s and <c>OnDownloadStarted</c> cancels the token as
    /// soon as the download begins.
    /// Expected: <c>OperationCanceledException</c> propagates, nothing is removed, and the .rsc is
    /// still on the router.
    /// Invariant: A backup that was never centrally verified leaves its remote artefacts in place.
    /// </summary>
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

    /// <summary>
    /// Remote deletion hangs longer than the allowed cleanup window.
    /// Preconditions: <c>RemoveDelay</c> is 2 s while <c>ScpTimeout</c> (the cleanup bound) is 50 ms.
    /// Expected: Result failed with <c>RemoteCleanupFailed</c>, the reason contains "timed out",
    /// local export and binary exist, and remaining remote files are reported.
    /// Invariant: Cleanup is time-bounded and a timeout is reported instead of hanging the job.
    /// </summary>
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

    /// <summary>
    /// Two backups of the same router run concurrently and share one remote file store.
    /// Preconditions: Both services use one <c>SharedRouterFileStore</c> (which also holds
    /// "unrelated.rsc"), a common backup root, and fixed clocks one millisecond apart.
    /// Expected: Both succeed, each removes exactly two files, the two removal sets do not intersect,
    /// and "unrelated.rsc" is never removed.
    /// Invariant: Distinct stems keep concurrent backups from deleting each other's artefacts.
    /// </summary>
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

    /// <summary>
    /// The export exists on the router but the binary backup comes out empty.
    /// Preconditions: <c>RecordingRouterOps.BinarySize</c> is 0.
    /// Expected: Result failed, nothing removed, exactly one remaining file (the owned .rsc), and
    /// <c>RemoteCleanupFailed</c> is false.
    /// Invariant: Only artefacts the backup actually created are reported as remaining.
    /// </summary>
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

    /// <summary>
    /// The host shuts down while a very slow remote deletion is in flight.
    /// Preconditions: <c>RemoveDelay</c> is 5 s but <c>ScpTimeout</c> is 100 ms; the token is
    /// cancelled once the first removal starts.
    /// Expected: The call finishes within 3 s by throwing <c>OperationCanceledException</c>; it is
    /// not returned as a failed backup result.
    /// Invariant: Post-verification cleanup is bounded by the timeout, so shutdown cannot hang on it.
    /// </summary>
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

    /// <summary>
    /// Cleanup fails after the caller has cancelled.
    /// Preconditions: <c>RemoveThrows</c> is true and <c>OnRemove</c> cancels the token during the
    /// first removal.
    /// Expected: <c>OperationCanceledException</c> propagates instead of a failure result, the
    /// removal was attempted, and local .rsc and .backup files remain.
    /// Invariant: Caller cancellation wins over a cleanup-failure result and the central backup is
    /// never lost.
    /// </summary>
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

    /// <summary>
    /// <c>EnsureUniquePath</c> is asked twice for the same file name in one directory.
    /// Preconditions: The first returned path is created on disk with content before the second call.
    /// Expected: The second path differs from the first and does not yet exist.
    /// Invariant: An existing backup file is never reused as the target path.
    /// </summary>
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

    /// <summary>
    /// <c>BuildBaseName</c> composes identity, IP and a millisecond UTC timestamp into one stem.
    /// Preconditions: Identity "MikroTik", IP 10.0.0.50, timestamp 2026-11-10 18:53:28.655 UTC.
    /// Expected: The stem is "MikroTik_10.0.0.50_20261110_185328_655".
    /// Invariant: All backup artefact extensions can be derived from a single stem.
    /// </summary>
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

    /// <summary>
    /// A hostile router identity containing path separators, a colon and ".." is used in a stem.
    /// Preconditions: Identity is <c>CORE:01/..\evil</c>.
    /// Expected: Stem is "CORE_01_.._evil_10.0.0.7_20261110_120000_123" and contains no invalid
    /// file-name characters, colon, slash or backslash.
    /// Invariant: A router-supplied identity can never escape the backup directory or produce an
    /// invalid name.
    /// </summary>
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

    /// <summary>
    /// Identities that are null, whitespace or reduced to nothing by sanitising.
    /// Preconditions: Identities null, "   " and "@@@" with the same IP and timestamp.
    /// Expected: Every case produces the stem "router_10.0.0.50_20261110_120000_001".
    /// Invariant: A file name is always produced, even when the router reports no usable identity.
    /// </summary>
    [Fact]
    public void BackupFileNaming_EmptyOrInvalidIdentity_FallsBackSafely()
    {
        DateTimeOffset stamp = DateTimeOffset.Parse("2026-11-10T12:00:00.001Z");
        IPAddress ip = IPAddress.Parse("10.0.0.50");
        Assert.Equal("router_10.0.0.50_20261110_120000_001", BackupFileNaming.BuildBaseName(null, ip, stamp));
        Assert.Equal("router_10.0.0.50_20261110_120000_001", BackupFileNaming.BuildBaseName("   ", ip, stamp));
        Assert.Equal("router_10.0.0.50_20261110_120000_001", BackupFileNaming.BuildBaseName("@@@", ip, stamp));
    }

    /// <summary>
    /// Timestamps one millisecond apart for the same identity and IP.
    /// Preconditions: Timestamps 12:00:00.001 and 12:00:00.002 UTC.
    /// Expected: The two stems are different.
    /// Invariant: Millisecond precision is the first line of defence against stem collisions.
    /// </summary>
    [Fact]
    public void BackupFileNaming_MillisecondPrecision_PreventsCollisions()
    {
        IPAddress ip = IPAddress.Parse("10.0.0.50");
        string a = BackupFileNaming.BuildBaseName("MikroTik", ip, DateTimeOffset.Parse("2026-11-10T12:00:00.001Z"));
        string b = BackupFileNaming.BuildBaseName("MikroTik", ip, DateTimeOffset.Parse("2026-11-10T12:00:00.002Z"));
        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Two allocations with an identical timestamp must yield distinct stems that stay shared by the
    /// .rsc, .backup and .json paths.
    /// Preconditions: Temporary Exports/Binary/Manifests folders; the first reservation is still held
    /// when the second allocation runs.
    /// Expected: First stem is the plain stem, the second has a "_1" suffix, both marker files exist,
    /// and no content file is created for the second reservation.
    /// Invariant: Reserving a stem claims a marker only and never creates empty content files.
    /// </summary>
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

    /// <summary>
    /// A full backup with a fixed clock names files after the router identity and removes exactly
    /// them.
    /// Preconditions: Fixed time 2026-11-10 19:00:00.123 UTC, identity "MikroTik", and an unrelated
    /// "unrelated.rsc" already on the router.
    /// Expected: Manifest local and remote names are
    /// "MikroTik_10.0.0.50_20261110_190000_123.rsc/.backup"; the removed files are exactly those two
    /// in order; the unrelated file remains; the export hash is correct.
    /// Invariant: Remote cleanup removes exact stem-matched files rather than patterns.
    /// </summary>
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

    /// <summary>
    /// The SFTP login is rejected, so the download fails.
    /// Preconditions: <c>FailOnDownload</c> simulates the failed authentication; the supplied
    /// password is "wrong-password".
    /// Expected: Result failed; no firmware fetch and no reboot.
    /// Invariant: Authentication problems stop the workflow before any upgrade action.
    /// </summary>
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

    /// <summary>
    /// The local file size does not match the size reported by the router, so the integrity gate
    /// rejects it before hashing.
    /// Preconditions: <c>CorruptLocalSize</c> truncates downloaded content.
    /// Expected: Result failed with "size mismatch" in the reason, no fetch, no remote removals.
    /// Invariant: A truncated download is never accepted as a valid backup.
    /// </summary>
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

    /// <summary>
    /// The caller cancels while the router export is still being created.
    /// Preconditions: <c>ExportDelay</c> is 5 s and the token is cancelled after 30 ms.
    /// Expected: <c>OperationCanceledException</c> propagates and no firmware fetch is issued.
    /// Invariant: Cancellation is propagated to the caller instead of being converted into a failed
    /// result.
    /// </summary>
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

    /// <summary>
    /// Builds a <c>DeviceBackupService</c> wired to the supplied fakes with short test timeouts.
    /// </summary>
    /// <param name="backupRoot">Temporary directory used as <c>BackupDirectory</c> for
    /// Exports/Binary/Manifests.</param>
    /// <param name="ops">Fake router operations that record calls and hold router-side files.</param>
    /// <param name="transfer">Fake SFTP client used for central downloads.</param>
    /// <param name="fixedTime">When set, a <c>FakeTimeProvider</c> pinned to this instant; otherwise
    /// the system clock.</param>
    /// <param name="scpTimeout">Overrides <c>ScpTimeout</c>, which also bounds remote cleanup;
    /// defaults to 5 seconds.</param>
    /// <returns>A service whose file-ready timeout is 2 s with a 10 ms poll interval.</returns>
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

    /// <summary>
    /// Creates the scheduled job used by every backup test.
    /// </summary>
    /// <returns>A job for router "CORE-01" at 10.0.0.50 whose schedule window is open now.</returns>
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

    /// <summary>
    /// Creates the device information passed to the backup service.
    /// </summary>
    /// <returns>A hEX PoE (mipsbe, RouterOS 6.49.23) with identity "CORE-01".</returns>
    private static MikrotikDeviceInfo CreateDevice() =>
        new()
        {
            Identity = "CORE-01",
            Version = "6.49.23",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardModel = "960PGS",
        };

    /// <summary>
    /// Thread-safe stand-in for one router's file list, so several <c>RecordingRouterOps</c>
    /// instances can operate on the same simulated device concurrently.
    /// </summary>
    private sealed class SharedRouterFileStore
    {
        /// <summary>
        /// Router file names (case-insensitive) mapped to their sizes in bytes.
        /// </summary>
        private readonly Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Guards <c>_files</c> against concurrent access from parallel backups.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// Copy of the current router files and sizes, safe to enumerate while other backups mutate the
        /// store.
        /// </summary>
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

        /// <summary>
        /// Creates or replaces a file on the simulated router.
        /// </summary>
        /// <param name="name">File name on the router.</param>
        /// <param name="size">Size in bytes.</param>
        public void Set(string name, long size)
        {
            lock (_gate)
            {
                _files[name] = size;
            }
        }

        /// <summary>
        /// Looks up a file on the simulated router.
        /// </summary>
        /// <param name="name">File name to find.</param>
        /// <param name="size">Receives the size when the file exists.</param>
        /// <returns><c>true</c> if the file exists.</returns>
        public bool TryGet(string name, out long size)
        {
            lock (_gate)
            {
                return _files.TryGetValue(name, out size);
            }
        }

        /// <summary>
        /// Deletes a file from the simulated router; a missing file is ignored.
        /// </summary>
        /// <param name="name">File name to remove.</param>
        public void Remove(string name)
        {
            lock (_gate)
            {
                _files.Remove(name);
            }
        }
    }

    /// <summary>
    /// Fake <c>IMikrotikRouterOperations</c> that keeps router files in memory, records removals,
    /// fetches and reboots, and can be told to fail or delay specific steps. It lets the tests assert
    /// exactly which remote files a backup created and deleted.
    /// </summary>
    private sealed class RecordingRouterOps : IMikrotikRouterOperations
    {
        /// <summary>
        /// Router file store; private to this instance unless a shared one was injected.
        /// </summary>
        private readonly SharedRouterFileStore _store;

        /// <summary>
        /// Creates the fake against an existing or fresh router file store.
        /// </summary>
        /// <param name="store">Shared store to simulate one router seen by several callers; <c>null</c>
        /// creates an isolated store.</param>
        public RecordingRouterOps(SharedRouterFileStore? store = null)
        {
            _store = store ?? new SharedRouterFileStore();
        }

        /// <summary>
        /// When true, <c>ExportConfigurationToFileAsync</c> throws InvalidOperationException("export
        /// failed").
        /// </summary>
        public bool ExportThrows { get; init; }

        /// <summary>
        /// When true, <c>RemoveFileAsync</c> records the attempt then throws IOException("remove
        /// failed").
        /// </summary>
        public bool RemoveThrows { get; init; }

        /// <summary>
        /// Supplies a custom exception thrown by <c>RemoveFileAsync</c> after recording; takes precedence
        /// over <c>RemoveThrows</c>.
        /// </summary>
        public Func<Exception>? RemoveExceptionFactory { get; init; }

        /// <summary>
        /// Callback invoked at the start of each removal, used to trigger cancellation at an exact point.
        /// </summary>
        public Action? OnRemove { get; init; }

        /// <summary>
        /// Size in bytes of the binary backup the fake router creates (default 12); 0 simulates an empty
        /// backup.
        /// </summary>
        public long BinarySize { get; init; } = 12;

        /// <summary>
        /// Artificial delay applied to export creation, honouring cancellation.
        /// </summary>
        public TimeSpan ExportDelay { get; init; }

        /// <summary>
        /// Artificial delay applied to each file removal, honouring the cleanup cancellation token.
        /// </summary>
        public TimeSpan RemoveDelay { get; init; }

        /// <summary>
        /// True once a firmware fetch was requested; backup tests assert it stays false.
        /// </summary>
        public bool FetchCalled { get; private set; }

        /// <summary>
        /// True once a reboot was requested; backup tests assert it stays false.
        /// </summary>
        public bool RebootCalled { get; private set; }

        /// <summary>
        /// Snapshot of the files currently on the simulated router.
        /// </summary>
        public Dictionary<string, long> Files => _store.Snapshot;

        /// <summary>
        /// File names passed to <c>RemoveFileAsync</c>, in call order, including attempts that threw.
        /// </summary>
        public List<string> RemovedFiles { get; } = [];

        /// <summary>
        /// Pre-populates the router with a file that is not part of the backup.
        /// </summary>
        /// <param name="name">File name to create.</param>
        /// <param name="size">Size in bytes.</param>
        public void SeedFile(string name, long size) => _store.Set(name, size);

        /// <summary>
        /// Returns the canned device from <c>CreateDevice</c>.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="host">Unused host.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task with the canned device information.</returns>
        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDevice());

        /// <summary>
        /// Reports a single <c>routeros-mipsbe</c> 6.49.23 package.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task with a one-package inventory.</returns>
        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([new("routeros-mipsbe", "6.49.23", false)]);

        /// <summary>
        /// Simulates <c>/export</c>: optionally delays or throws, then stores "{name}.rsc" sized as the
        /// UTF-8 byte count of "export-body".
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="fileNameWithoutExtension">Backup stem; ".rsc" is appended.</param>
        /// <param name="cancellationToken">Cancels the optional <c>ExportDelay</c>.</param>
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

        /// <summary>
        /// Simulates a binary backup by storing "{name}.backup" with <c>BinarySize</c> bytes.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="fileNameWithoutExtension">Backup stem; ".backup" is appended.</param>
        /// <param name="backupPassword">Ignored.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default)
        {
            _store.Set(fileNameWithoutExtension + ".backup", BinarySize);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Looks the file up in the store, throwing if the token is already cancelled.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="fileName">File name to find.</param>
        /// <param name="cancellationToken">Checked for cancellation up front.</param>
        /// <returns>The file's info, or <c>null</c> if it does not exist.</returns>
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

        /// <summary>
        /// Succeeds immediately when the file exists with a positive size and otherwise throws
        /// TimeoutException("not ready"), without actually waiting.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="fileName">File to wait for.</param>
        /// <param name="timeout">Ignored.</param>
        /// <param name="pollInterval">Ignored.</param>
        /// <param name="cancellationToken">Passed to <c>FindFileAsync</c>.</param>
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

        /// <summary>
        /// Records the removal, then applies the configured callback, delay and failure injection before
        /// deleting the file from the store.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="fileName">File to delete.</param>
        /// <param name="cancellationToken">Cancels <c>RemoveDelay</c>; the service passes its bounded
        /// cleanup token.</param>
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

        /// <summary>
        /// Records that a firmware fetch was requested without downloading anything.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="url">Ignored.</param>
        /// <param name="destinationFileName">Ignored.</param>
        /// <param name="timeout">Ignored.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
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

        /// <summary>
        /// Records that a reboot was requested.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fake <c>IMikrotikFileTransferClient</c> that writes small deterministic payloads to disk
    /// instead of using SFTP, with switches for failure, delay, truncation and download observation.
    /// </summary>
    private sealed class InMemoryFileTransfer : IMikrotikFileTransferClient
    {
        /// <summary>
        /// When true, every download throws IOException("sftp authentication failed").
        /// </summary>
        public bool FailOnDownload { get; init; }

        /// <summary>
        /// When set, only downloads whose remote name ends with this suffix (for example ".backup") fail.
        /// </summary>
        public string? FailOnRemoteSuffix { get; init; }

        /// <summary>
        /// When true, writes one byte instead of the real payload so the size no longer matches the
        /// router.
        /// </summary>
        public bool CorruptLocalSize { get; init; }

        /// <summary>
        /// Artificial delay before each download, honouring cancellation.
        /// </summary>
        public TimeSpan DownloadDelay { get; init; }

        /// <summary>
        /// Callback invoked with the remote file name as each download begins, used to cancel
        /// mid-transfer.
        /// </summary>
        public Action<string>? OnDownloadStarted { get; init; }

        /// <summary>
        /// Remote file names that were downloaded successfully.
        /// </summary>
        public List<string> Downloads { get; } = [];

        /// <summary>
        /// Simulates an SFTP download: applies the configured delay and failures, then writes
        /// "export-body" for .rsc files or twelve 0x07 bytes otherwise with <see
        /// cref="FileMode.CreateNew"/> so an existing local file is never overwritten, as in production.
        /// </summary>
        /// <param name="host">Ignored.</param>
        /// <param name="port">Ignored.</param>
        /// <param name="username">Ignored.</param>
        /// <param name="password">Ignored.</param>
        /// <param name="remoteFileName">Name used to choose the payload and failure rules.</param>
        /// <param name="localFilePath">Local file to create.</param>
        /// <param name="timeout">Ignored.</param>
        /// <param name="cancellationToken">Cancels the optional delay and the writes.</param>
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
