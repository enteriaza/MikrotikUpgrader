using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Backup;
public sealed class BackupAllocationLifecycleTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("198.18.0.83");
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-11-10T22:00:00.123Z");

    [Fact]
    public void ExistingExportFile_ForcesNewStem()
    {
        (string exports, string binary, string manifests) = TempDirs();
        string occupied = "MikroTik_198.18.0.83_20261110_220000_123";
        File.WriteAllText(Path.Combine(exports, occupied + ".rsc"), "existing");

        using BackupStemReservation reservation =
            BackupFileNaming.AllocateUniqueBaseName("MikroTik", Ip, Stamp, exports, binary, manifests);

        Assert.Equal(occupied + "_1", reservation.Stem);
        Assert.True(File.Exists(reservation.MarkerPath));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(exports, occupied + ".rsc")));
        Assert.False(File.Exists(reservation.ExportLocalPath));
    }

    [Fact]
    public void ExistingBinaryFile_ForcesNewStem()
    {
        (string exports, string binary, string manifests) = TempDirs();
        string occupied = "MikroTik_198.18.0.83_20261110_220000_123";
        File.WriteAllText(Path.Combine(binary, occupied + ".backup"), "existing");

        using BackupStemReservation reservation =
            BackupFileNaming.AllocateUniqueBaseName("MikroTik", Ip, Stamp, exports, binary, manifests);

        Assert.Equal(occupied + "_1", reservation.Stem);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(binary, occupied + ".backup")));
    }

    [Fact]
    public void ExistingManifestFile_ForcesNewStem()
    {
        (string exports, string binary, string manifests) = TempDirs();
        string occupied = "MikroTik_198.18.0.83_20261110_220000_123";
        File.WriteAllText(Path.Combine(manifests, occupied + ".json"), "{}");

        using BackupStemReservation reservation =
            BackupFileNaming.AllocateUniqueBaseName("MikroTik", Ip, Stamp, exports, binary, manifests);

        Assert.Equal(occupied + "_1", reservation.Stem);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(manifests, occupied + ".json")));
    }

    [Fact]
    public void ExistingReservationMarker_ForcesNewStem()
    {
        (string exports, string binary, string manifests) = TempDirs();
        string occupied = "MikroTik_198.18.0.83_20261110_220000_123";
        File.WriteAllText(
            Path.Combine(manifests, occupied + BackupFileNaming.StemAllocationMarkerExtension),
            string.Empty);

        using BackupStemReservation reservation =
            BackupFileNaming.AllocateUniqueBaseName("MikroTik", Ip, Stamp, exports, binary, manifests);

        Assert.Equal(occupied + "_1", reservation.Stem);
        Assert.True(File.Exists(Path.Combine(manifests, occupied + BackupFileNaming.StemAllocationMarkerExtension)));
        Assert.True(File.Exists(reservation.MarkerPath));
    }

    [Fact]
    public async Task ConcurrentAllocation_NeverOverlaps()
    {
        (string exports, string binary, string manifests) = TempDirs();

        string[] stems = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
        {
            BackupStemReservation reservation =
                BackupFileNaming.AllocateUniqueBaseName("MikroTik", Ip, Stamp, exports, binary, manifests);
            File.WriteAllText(reservation.ExportLocalPath, "x");
            File.WriteAllText(reservation.BinaryLocalPath, "x");
            File.WriteAllText(reservation.ManifestLocalPath, "{}");
            reservation.Complete();
            return reservation.Stem;
        })));

        Assert.Equal(24, stems.Distinct(StringComparer.Ordinal).Count());
        Assert.All(stems, s => Assert.StartsWith("MikroTik_198.18.0.83_20261110_220000_123", s, StringComparison.Ordinal));
        Assert.DoesNotContain(stems, static s => s.Equals("MikroTik_20261110_220000_123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbortedBackup_ReleasesMarkerWithoutDeletingContent()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-abort-").FullName;
        RecordingOps ops = new() { ExportThrows = true };
        DeviceBackupService service = CreateService(backupRoot, ops, new CreateNewTransfer());

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        string manifests = Path.Combine(backupRoot, "Manifests");
        Assert.Empty(Directory.GetFiles(manifests, "*" + BackupFileNaming.StemAllocationMarkerExtension));
        Assert.Empty(Directory.GetFiles(Path.Combine(backupRoot, "Exports"), "*.rsc"));
    }

    [Fact]
    public async Task FailedSftpDownload_LeavesReservationBlockingStem_DoesNotOverwrite()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-sftp-fail-").FullName;
        string exports = Path.Combine(backupRoot, "Exports");
        Directory.CreateDirectory(exports);
        // Pre-create the exact stem the fixed clock will try first — allocator must bump before remote.
        string firstStem = "MikroTik_198.18.0.83_20261110_201000_000";
        File.WriteAllText(Path.Combine(exports, firstStem + ".rsc"), "do-not-touch");

        RecordingOps ops = new();
        DeviceBackupService service = CreateService(
            backupRoot,
            ops,
            new CreateNewTransfer { FailOnDownload = true },
            fixedTime: DateTimeOffset.Parse("2026-11-10T20:10:00.000Z"));

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("do-not-touch", File.ReadAllText(Path.Combine(exports, firstStem + ".rsc")));
        // Remote export was issued against a bumped stem, not the occupied one.
        Assert.DoesNotContain(ops.Files.Keys, k => k.StartsWith(firstStem + ".", StringComparison.Ordinal));
        Assert.Contains(
            ops.Files.Keys,
            k => k.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase)
                && !k.StartsWith(firstStem + ".", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulAllocation_CompletesAndRemovesMarker()
    {
        string backupRoot = Directory.CreateTempSubdirectory("bk-ok-alloc-").FullName;
        DeviceBackupService service = CreateService(
            backupRoot,
            new RecordingOps(),
            new CreateNewTransfer(),
            fixedTime: Stamp);

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.StartsWith("MikroTik_198.18.0.83_", Path.GetFileNameWithoutExtension(result.ExportLocalPath!), StringComparison.Ordinal);
        Assert.Equal(
            Path.GetFileNameWithoutExtension(result.ExportLocalPath),
            Path.GetFileNameWithoutExtension(result.BinaryLocalPath));
        Assert.Equal(
            Path.GetFileNameWithoutExtension(result.ExportLocalPath),
            Path.GetFileNameWithoutExtension(result.ManifestLocalPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(backupRoot, "Manifests"), "*" + BackupFileNaming.StemAllocationMarkerExtension));
        Assert.False(Path.GetFileName(result.ExportLocalPath)!.Equals(
            "MikroTik_20261110_220000_123.rsc",
            StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedBackup_IdenticalTimestamp_AllocatesDistinctSharedStems()
    {
        (string exports, string binary, string manifests) = TempDirs();
        using BackupStemReservation first =
            BackupFileNaming.AllocateUniqueBaseName("MikroTik", Ip, Stamp, exports, binary, manifests);
        File.WriteAllText(first.ExportLocalPath, "1");
        File.WriteAllText(first.BinaryLocalPath, "1");
        File.WriteAllText(first.ManifestLocalPath, "{}");
        first.Complete();

        using BackupStemReservation second =
            BackupFileNaming.AllocateUniqueBaseName("MikroTik", Ip, Stamp, exports, binary, manifests);
        Assert.NotEqual(first.Stem, second.Stem);
        Assert.Equal(first.Stem + "_1", second.Stem);
        Assert.StartsWith("MikroTik_198.18.0.83_", second.Stem, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservationCleanup_AfterException_ReleasesOnlyOwnMarker()
    {
        (string exports, string binary, string manifests) = TempDirs();
        using BackupStemReservation foreign =
            BackupFileNaming.AllocateUniqueBaseName("MikroTik", Ip, Stamp, exports, binary, manifests);
        string foreignMarker = foreign.MarkerPath;

        BackupStemReservation own =
            BackupFileNaming.AllocateUniqueBaseName("MikroTik", Ip, Stamp, exports, binary, manifests);
        Assert.True(File.Exists(own.MarkerPath));
        Assert.True(File.Exists(foreignMarker));

        try
        {
            throw new InvalidOperationException("boom");
        }
        catch
        {
            own.Dispose();
        }

        Assert.False(File.Exists(own.MarkerPath));
        Assert.True(File.Exists(foreignMarker));
    }

    [Fact]
    public async Task PreExistingEmptyReservationArtefact_DoesNotBlockSuccessfulBackup()
    {
        // Reproduces the live defect: empty .rsc left by the old CreateNew reservation strategy.
        string backupRoot = Directory.CreateTempSubdirectory("bk-empty-rsc-").FullName;
        string exports = Path.Combine(backupRoot, "Exports");
        Directory.CreateDirectory(exports);
        string staleStem = "MikroTik_198.18.0.83_20261110_201500_000";
        File.WriteAllBytes(Path.Combine(exports, staleStem + ".rsc"), []);

        DeviceBackupService service = CreateService(
            backupRoot,
            new RecordingOps(),
            new CreateNewTransfer(),
            fixedTime: DateTimeOffset.Parse("2026-11-10T20:15:00.000Z"));

        DeviceBackupResult result = await service.CreatePreUpgradeBackupAsync(
            new TikFakeConnection(),
            CreateJob(),
            CreateDevice(),
            "admin",
            "secret",
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotEqual(staleStem + ".rsc", result.Manifest!.ExportFileName);
        Assert.Equal(0, new FileInfo(Path.Combine(exports, staleStem + ".rsc")).Length);
        Assert.True(new FileInfo(result.ExportLocalPath!).Length > 0);
    }

    private static (string Exports, string Binary, string Manifests) TempDirs()
    {
        string root = Directory.CreateTempSubdirectory("alloc-").FullName;
        string exports = Path.Combine(root, "Exports");
        string binary = Path.Combine(root, "Binary");
        string manifests = Path.Combine(root, "Manifests");
        Directory.CreateDirectory(exports);
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(manifests);
        return (exports, binary, manifests);
    }

    private static DeviceBackupService CreateService(
        string backupRoot,
        RecordingOps ops,
        IMikrotikFileTransferClient transfer,
        DateTimeOffset? fixedTime = null) =>
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
            fixedTime is null ? TimeProvider.System : new FakeTimeProvider(fixedTime.Value),
            NullLogger<DeviceBackupService>.Instance);

    private static MikrotikScheduledJob CreateJob() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "LIVE-TEST",
            RouterIp = Ip,
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    private static MikrotikDeviceInfo CreateDevice() =>
        new()
        {
            Identity = "MikroTik",
            Version = "6.49.23",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardModel = "960PGS",
        };

    private sealed class RecordingOps : IMikrotikRouterOperations
    {
        public bool ExportThrows { get; init; }

        public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDevice());

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([new("routeros-mipsbe", "6.49.23", false)]);

        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default)
        {
            if (ExportThrows)
            {
                throw new InvalidOperationException("export failed");
            }

            Files[fileNameWithoutExtension + ".rsc"] = Encoding.UTF8.GetByteCount("export-body");
            return Task.CompletedTask;
        }

        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default)
        {
            Files[fileNameWithoutExtension + ".backup"] = 12;
            return Task.CompletedTask;
        }

        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Files.TryGetValue(fileName, out long size)
                    ? new RouterFileInfo(fileName, size, "*1")
                    : null);

        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            Files.Remove(fileName);
            return Task.CompletedTask;
        }

        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CreateNewTransfer : IMikrotikFileTransferClient
    {
        public bool FailOnDownload { get; init; }

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
                throw new IOException("sftp download failed");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(localFilePath)!);
            byte[] payload = remoteFileName.EndsWith(".rsc", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetBytes("export-body")
                : Enumerable.Repeat((byte)7, 12).ToArray();
            using FileStream output = new(localFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(payload);
            return Task.CompletedTask;
        }
    }
}
