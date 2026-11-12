// -----------------------------------------------------------------------------
// File: BackupAllocationLifecycleTests.cs
//
// Description:
// Backup stem reservation and allocation lifecycle regression tests.
//
// -----------------------------------------------------------------------------
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
/// <summary>
/// Regression tests for how <c>BackupFileNaming.AllocateUniqueBaseName</c> and
/// <c>BackupStemReservation</c> claim a shared stem for the .rsc, .backup and .json files of one
/// backup.
/// They cover collisions with existing content or markers, parallel allocation, marker release
/// after success or failure, and the end-to-end <c>DeviceBackupService</c> behaviour that depends
/// on it. The recurring invariant is that an allocation never overwrites or deletes content that
/// already exists.
/// </summary>
public sealed class BackupAllocationLifecycleTests
{
    /// <summary>
    /// Router address (198.18.0.83, benchmark range) embedded in every expected stem.
    /// </summary>
    private static readonly IPAddress Ip = IPAddress.Parse("198.18.0.83");
    /// <summary>
    /// Fixed allocation time, 2026-11-10 22:00:00.123 UTC, which produces the base stem
    /// "MikroTik_198.18.0.83_20261110_220000_123".
    /// </summary>
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-11-10T22:00:00.123Z");

    /// <summary>
    /// The preferred stem is already used by an export file.
    /// Preconditions: A file "{stem}.rsc" with content "existing" is in the Exports folder.
    /// Expected: The reservation uses "{stem}_1", has a marker on disk, the old file keeps its
    /// content, and the new export path does not exist.
    /// Invariant: An existing export is never overwritten or reused.
    /// </summary>
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

    /// <summary>
    /// The preferred stem is already used by a binary backup.
    /// Preconditions: A file "{stem}.backup" with content "existing" is in the Binary folder.
    /// Expected: The reservation uses "{stem}_1" and the old backup keeps its content.
    /// Invariant: A collision in any one of the three folders moves all three artefacts to a new
    /// stem.
    /// </summary>
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

    /// <summary>
    /// The preferred stem is already used by a manifest.
    /// Preconditions: A file "{stem}.json" containing "{}" is in the Manifests folder.
    /// Expected: The reservation uses "{stem}_1" and the manifest content is unchanged.
    /// Invariant: Existing manifests are never overwritten.
    /// </summary>
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

    /// <summary>
    /// Another allocation already holds the stem, shown only by its marker file.
    /// Preconditions: A "{stem}.stem.alloc" marker exists in Manifests but no content files do.
    /// Expected: The reservation uses "{stem}_1", and both the foreign marker and the new marker
    /// exist.
    /// Invariant: A claimed but not yet written stem is treated as occupied.
    /// </summary>
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

    /// <summary>
    /// 24 parallel allocations race for the same timestamp and write their files.
    /// Preconditions: Each task allocates, creates its three files, and calls <c>Complete</c>.
    /// Expected: All 24 stems are distinct and start with the common stem; none uses the
    /// identity-less fallback "MikroTik_20261110_220000_123".
    /// Invariant: The process-wide lock guarantees unique stems under contention.
    /// </summary>
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

    /// <summary>
    /// A backup aborts at the router export step.
    /// Preconditions: <c>RecordingOps.ExportThrows</c> is true and the transfer is a
    /// <c>CreateNewTransfer</c>.
    /// Expected: Result failed; the Manifests folder has no leftover marker and the Exports folder
    /// has no .rsc.
    /// Invariant: A stem that produced no content is released so it can be reused, and nothing is
    /// deleted.
    /// </summary>
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

    /// <summary>
    /// The SFTP download fails after allocation, while an unrelated file already occupies the first
    /// stem the clock would produce.
    /// Preconditions: Fixed time 2026-11-10 20:10:00.000 UTC; an export "do-not-touch" already exists
    /// for that stem; <c>CreateNewTransfer.FailOnDownload</c> is true.
    /// Expected: Result failed, the pre-existing file is unchanged, and the router export was
    /// requested under a bumped stem and not the occupied one.
    /// Invariant: Allocation resolves collisions before any remote command and never overwrites
    /// existing evidence.
    /// </summary>
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

    /// <summary>
    /// A full successful backup with a fixed clock.
    /// Preconditions: <c>RecordingOps</c> and <c>CreateNewTransfer</c> without failures; time is
    /// <c>Stamp</c>.
    /// Expected: Result succeeded, export, binary and manifest share one file-name stem that starts
    /// with "MikroTik_198.18.0.83_", no marker remains in Manifests, and the stem is not the
    /// identity-less fallback.
    /// Invariant: <c>Complete</c> removes the claim marker once real artefacts exist.
    /// </summary>
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

    /// <summary>
    /// Two backups allocate at exactly the same timestamp, the first one having completed.
    /// Preconditions: The first reservation writes all three files and calls <c>Complete</c>.
    /// Expected: The second stem is the first stem plus "_1" and still starts with the device stem.
    /// Invariant: Completed backups keep their stem occupied through their content files.
    /// </summary>
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

    /// <summary>
    /// A reservation is disposed after an exception while another reservation is active.
    /// Preconditions: A foreign reservation is held; a second reservation "own" is created for the
    /// same timestamp and then disposed from a catch block.
    /// Expected: The own marker is deleted and the foreign marker still exists.
    /// Invariant: Dispose releases only the claim it owns.
    /// </summary>
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

    /// <summary>
    /// Reproduces a live defect: an empty .rsc left over by the earlier CreateNew-based reservation
    /// scheme.
    /// Preconditions: A zero-byte "{stem}.rsc" exists for the stem the fixed clock (20:15:00.000 UTC)
    /// would choose first.
    /// Expected: The backup succeeds under a different file name, the stale empty file stays empty,
    /// and the new export is non-empty.
    /// Invariant: A stale placeholder neither fails nor gets overwritten by a new backup.
    /// </summary>
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

    /// <summary>
    /// Creates an isolated temporary backup tree.
    /// </summary>
    /// <returns>Paths of the new empty Exports, Binary and Manifests folders.</returns>
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

    /// <summary>
    /// Builds a <c>DeviceBackupService</c> over the fakes with a temporary backup folder and short
    /// timeouts.
    /// </summary>
    /// <param name="backupRoot">Folder used as the backup directory.</param>
    /// <param name="ops">Fake router operations.</param>
    /// <param name="transfer">Fake SFTP client.</param>
    /// <param name="fixedTime">Pins the service clock to this instant when set; otherwise the system
    /// clock is used.</param>
    /// <returns>A service with a 2 s file-ready timeout, 10 ms poll interval and 5 s transfer
    /// timeout.</returns>
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

    /// <summary>
    /// Creates the job passed to the backup service.
    /// </summary>
    /// <returns>A job for router "LIVE-TEST" at <c>Ip</c> whose schedule window is open
    /// now.</returns>
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

    /// <summary>
    /// Creates the device information passed to the backup service.
    /// </summary>
    /// <returns>A hEX PoE (mipsbe, RouterOS 6.49.23) with identity "MikroTik".</returns>
    private static MikrotikDeviceInfo CreateDevice() =>
        new()
        {
            Identity = "MikroTik",
            Version = "6.49.23",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardModel = "960PGS",
        };

    /// <summary>
    /// Minimal fake <c>IMikrotikRouterOperations</c> that stores router files in a dictionary and
    /// creates export and backup files instantly. It lets allocation tests see which remote stems the
    /// service asked for without any timing or failure machinery.
    /// </summary>
    private sealed class RecordingOps : IMikrotikRouterOperations
    {
        /// <summary>
        /// When true, <c>ExportConfigurationToFileAsync</c> throws InvalidOperationException("export
        /// failed").
        /// </summary>
        public bool ExportThrows { get; init; }

        /// <summary>
        /// Simulated router files (case-insensitive name to size in bytes).
        /// </summary>
        public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

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
        /// Simulates <c>/export</c> by recording "{name}.rsc" sized as the UTF-8 byte count of
        /// "export-body", or throwing when <c>ExportThrows</c> is set.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="fileNameWithoutExtension">Backup stem; ".rsc" is appended.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
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

        /// <summary>
        /// Simulates a binary backup by recording "{name}.backup" with 12 bytes.
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
            Files[fileNameWithoutExtension + ".backup"] = 12;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns immediately because files created by this fake are always ready.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="fileName">Ignored.</param>
        /// <param name="timeout">Ignored.</param>
        /// <param name="pollInterval">Ignored.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Looks the file up in <c>Files</c>.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="fileName">File name to find.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>The file's info, or <c>null</c> when it is not on the simulated router.</returns>
        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Files.TryGetValue(fileName, out long size)
                    ? new RouterFileInfo(fileName, size, "*1")
                    : null);

        /// <summary>
        /// Deletes the file from <c>Files</c>; a missing file is ignored.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="fileName">File to delete.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            Files.Remove(fileName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op; allocation tests never download firmware.
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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; allocation tests never reboot a router.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Fake <c>IMikrotikFileTransferClient</c> that writes small deterministic payloads with <see
    /// cref="FileMode.CreateNew"/>, matching the production SFTP client's refusal to overwrite local
    /// files.
    /// </summary>
    private sealed class CreateNewTransfer : IMikrotikFileTransferClient
    {
        /// <summary>
        /// When true, every download throws IOException("sftp download failed").
        /// </summary>
        public bool FailOnDownload { get; init; }

        /// <summary>
        /// Writes "export-body" for .rsc files or twelve 0x07 bytes otherwise, throwing if the local file
        /// already exists or <c>FailOnDownload</c> is set.
        /// </summary>
        /// <param name="host">Ignored.</param>
        /// <param name="port">Ignored.</param>
        /// <param name="username">Ignored.</param>
        /// <param name="password">Ignored.</param>
        /// <param name="remoteFileName">Name used to choose the payload.</param>
        /// <param name="localFilePath">Local file to create.</param>
        /// <param name="timeout">Ignored.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <returns>A completed task.</returns>
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
