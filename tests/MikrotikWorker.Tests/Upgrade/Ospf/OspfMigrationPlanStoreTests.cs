// -----------------------------------------------------------------------------
// File: OspfMigrationPlanStoreTests.cs
//
// Description:
// Unit tests for OSPF migration plan persistence, load, and delete behaviour.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Unit tests for OSPF migration plan persistence: save/load round-trips, fail-closed loads,
/// and startup orphan cleanup without auto-resume.
/// </summary>
public sealed class OspfMigrationPlanStoreTests
{
    /// <summary>
    /// Scenario: a hashed plan is saved, loaded back with the matching correlation id, then the job's plan is deleted.
    /// Expected: the loaded plan has the same <c>ContentHash</c>, <c>ContentHashMatches</c> is true, and a load after <c>Delete</c> returns null.
    /// Invariant: an untouched plan survives persistence with its integrity hash intact.
    /// </summary>
    [Fact]
    public async Task ValidPlan_RoundTrip_RecomputesMatchingHash()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        OspfMigrationPlan plan = CreateHashedPlan(job, "AP01");

        await store.SaveAsync(plan, CancellationToken.None);
        OspfMigrationPlan? loaded = await store.TryLoadAsync(job, "AP01", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(plan.ContentHash, loaded!.ContentHash);
        Assert.True(OspfMigrationPlanner.ContentHashMatches(loaded));
        store.Delete(job);
        Assert.Null(await store.TryLoadAsync(job, "AP01", CancellationToken.None));
    }

    /// <summary>
    /// Scenario: after saving, the persisted JSON has the plan's router-id replaced with 9.9.9.9 while the stored hash is left unchanged.
    /// Expected: <c>TryLoadAsync</c> returns null.
    /// Invariant: tampered plan content fails hash verification and is never returned for execution.
    /// </summary>
    [Fact]
    public async Task ModifiedPlanContent_IsRejected()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        OspfMigrationPlan plan = CreateHashedPlan(job, "AP01");
        await store.SaveAsync(plan, CancellationToken.None);

        string path = Path.Combine(
            root.Path,
            "OspfPlans",
            OspfMigrationPlanStore.BuildFileName(job.JobId, job.ImportId, job.RouterIp.ToString()));
        string json = await File.ReadAllTextAsync(path);
        json = json.Replace(plan.RouterId, "9.9.9.9", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json);

        Assert.Null(await store.TryLoadAsync(job, "AP01", CancellationToken.None));
    }

    /// <summary>
    /// Scenario: after saving, the stored content hash in the persisted JSON is replaced with "DEADBEEF".
    /// Expected: <c>TryLoadAsync</c> returns null.
    /// Invariant: a stored hash that does not match the recomputed hash invalidates the plan.
    /// </summary>
    [Fact]
    public async Task ModifiedHash_IsRejected()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        OspfMigrationPlan plan = CreateHashedPlan(job, "AP01");
        await store.SaveAsync(plan, CancellationToken.None);

        string path = Path.Combine(
            root.Path,
            "OspfPlans",
            OspfMigrationPlanStore.BuildFileName(job.JobId, job.ImportId, job.RouterIp.ToString()));
        string json = await File.ReadAllTextAsync(path);
        json = json.Replace(plan.ContentHash, "DEADBEEF", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json);

        Assert.Null(await store.TryLoadAsync(job, "AP01", CancellationToken.None));
    }

    /// <summary>
    /// Scenario: The plan file at the expected OspfPlans path for the job contains truncated JSON (an unterminated
    /// object that begins with the routerId property).
    /// Expected: TryLoadAsync returns null.
    /// Invariant: A corrupt plan file is rejected and never loaded as a valid migration plan.
    /// </summary>
    [Fact]
    public async Task TruncatedJson_IsRejected()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        string dir = Path.Combine(root.Path, "OspfPlans");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(
            dir,
            OspfMigrationPlanStore.BuildFileName(job.JobId, job.ImportId, job.RouterIp.ToString()));
        await File.WriteAllTextAsync(path, "{\"routerId\":\"10.0.0.1\"");

        Assert.Null(await store.TryLoadAsync(job, "AP01", CancellationToken.None));
    }

    /// <summary>
    /// Scenario: a plan saved for correlation id "AP01" is loaded with the id "OTHER".
    /// Expected: <c>TryLoadAsync</c> returns null.
    /// Invariant: a plan can only be loaded by the correlation it was created for.
    /// </summary>
    [Fact]
    public async Task IncorrectCorrelation_IsRejected()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        await store.SaveAsync(CreateHashedPlan(job, "AP01"), CancellationToken.None);
        Assert.Null(await store.TryLoadAsync(job, "OTHER", CancellationToken.None));
    }

    /// <summary>
    /// Scenario: a plan file is written directly to disk with a blank (whitespace) <c>ContentHash</c>.
    /// Expected: <c>TryLoadAsync</c> returns null.
    /// Invariant: a plan without a usable hash is untrusted and not loaded.
    /// </summary>
    [Fact]
    public async Task MissingOrMalformedHash_IsRejected()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        OspfMigrationPlan plan = CreateHashedPlan(job, "AP01");
        plan.ContentHash = " ";
        string dir = Path.Combine(root.Path, "OspfPlans");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(
            dir,
            OspfMigrationPlanStore.BuildFileName(job.JobId, job.ImportId, job.RouterIp.ToString()));
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(plan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        Assert.Null(await store.TryLoadAsync(job, "AP01", CancellationToken.None));
    }

    /// <summary>
    /// Scenario: a directory occupies the <c>.tmp</c> path the store wants to write, so the save fails.
    /// Expected: <c>SaveAsync</c> throws, and no final plan file is published.
    /// Invariant: a failed save never leaves a partially written plan at the final path.
    /// </summary>
    [Fact]
    public async Task FailedSave_CleansTemporaryFile()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        OspfMigrationPlan plan = CreateHashedPlan(job, "AP01");

        string plansDir = Path.Combine(root.Path, "OspfPlans");
        Directory.CreateDirectory(plansDir);
        // Create a directory where the temp file should be written so WriteAllBytes fails.
        string finalPath = Path.Combine(
            plansDir,
            OspfMigrationPlanStore.BuildFileName(job.JobId, job.ImportId, job.RouterIp.ToString()));
        string tempPath = finalPath + ".tmp";
        Directory.CreateDirectory(tempPath);

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(plan, CancellationToken.None));
        // Temp path was a directory we created; cleanup attempts delete file — directory may remain.
        // Ensure no abandoned plan JSON was published.
        Assert.False(File.Exists(finalPath));
    }

    /// <summary>
    /// Scenario: <c>SaveAsync</c> is called with an already-cancelled token.
    /// Expected: it throws <c>OperationCanceledException</c>, and neither the final plan file nor its <c>.tmp</c> file exists.
    /// Invariant: cancelling persistence leaves no published or temporary plan files behind.
    /// </summary>
    [Fact]
    public async Task CancellationDuringPersistence_DoesNotLeavePublishedPlan()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(CreateHashedPlan(job, "AP01"), cts.Token));

        string finalPath = Path.Combine(
            root.Path,
            "OspfPlans",
            OspfMigrationPlanStore.BuildFileName(job.JobId, job.ImportId, job.RouterIp.ToString()));
        Assert.False(File.Exists(finalPath));
        Assert.False(File.Exists(finalPath + ".tmp"));
    }

    /// <summary>
    /// Scenario: new store instance (empty active set) with inactive owned JSON and an orphan temp.
    /// Expected: inactive JSON retained, orphan temp removed, no plan resumed into memory.
    /// Invariant: startup cleanup must not auto-resume persisted plans after restart.
    /// </summary>
    [Fact]
    public async Task StartupCleanup_RetainsInactiveJson_RemovesOrphanTemp_DoesNotResume()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        OspfMigrationPlan plan = CreateHashedPlan(job, "AP01");
        await store.SaveAsync(plan, CancellationToken.None);
        // Simulate process restart: new store instance has empty in-memory active set.
        OspfMigrationPlanStore restarted = CreateStore(root.Path);

        string planPath = Path.Combine(
            root.Path,
            "OspfPlans",
            OspfMigrationPlanStore.BuildFileName(job.JobId, job.ImportId, job.RouterIp.ToString()));
        string orphanTemp = planPath + ".tmp";
        await File.WriteAllTextAsync(orphanTemp, "partial-interrupted-write");
        string unrelatedJson = Path.Combine(root.Path, "OspfPlans", "notes-not-a-plan.json");
        await File.WriteAllTextAsync(unrelatedJson, "{\"hello\":true}");

        restarted.CleanupOrphansOnStartup();

        Assert.True(File.Exists(planPath));
        Assert.False(File.Exists(orphanTemp));
        Assert.True(File.Exists(unrelatedJson));
        // Cleanup does not auto-associate retained plans with workflow state; explicit TryLoad
        // still works only when the caller supplies the original job correlation.
        OspfMigrationPlan? loaded = await restarted.TryLoadAsync(job, "AP01", CancellationToken.None);
        Assert.NotNull(loaded);
    }

    /// <summary>
    /// Scenario: two plans for different jobs are saved and the store is recreated as after a restart, then startup cleanup runs.
    /// Expected: both plan files still exist.
    /// Invariant: startup cleanup removes only orphaned temp files, not valid plans of inactive jobs.
    /// </summary>
    [Fact]
    public async Task StartupCleanup_RetainsMultipleInactivePlans()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob jobA = CreateJob();
        MikrotikScheduledJob jobB = CreateJob();
        await store.SaveAsync(CreateHashedPlan(jobA, "AP01"), CancellationToken.None);
        await store.SaveAsync(CreateHashedPlan(jobB, "AP01"), CancellationToken.None);

        OspfMigrationPlanStore restarted = CreateStore(root.Path);
        restarted.CleanupOrphansOnStartup();

        Assert.True(File.Exists(PlanPath(root.Path, jobA)));
        Assert.True(File.Exists(PlanPath(root.Path, jobB)));
    }

    /// <summary>
    /// Scenario: same-process store with an active plan registered after SaveAsync.
    /// Expected: startup cleanup leaves the active plan JSON and its temp file untouched.
    /// Invariant: cleanup must not delete artefacts of an in-memory-active plan.
    /// </summary>
    [Fact]
    public async Task StartupCleanup_DoesNotDeleteActivePlanOrTemp()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        await store.SaveAsync(CreateHashedPlan(job, "AP01"), CancellationToken.None);

        string planPath = PlanPath(root.Path, job);
        string tempPath = planPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, "in-flight");

        // Same process: plan is registered active after SaveAsync.
        store.CleanupOrphansOnStartup();

        Assert.True(File.Exists(planPath));
        Assert.True(File.Exists(tempPath));
    }

    /// <summary>
    /// Scenario: truncated/malformed owned plan JSON on disk, then cleanup and TryLoadAsync.
    /// Expected: malformed file retained; load returns null.
    /// Invariant: malformed owned plans are not deleted and are not treated as loadable state.
    /// </summary>
    [Fact]
    public async Task StartupCleanup_RetainsMalformedOwnedJson()
    {
        await using TempBackupRoot root = new();
        MikrotikScheduledJob job = CreateJob();
        string dir = Path.Combine(root.Path, "OspfPlans");
        Directory.CreateDirectory(dir);
        string path = PlanPath(root.Path, job);
        await File.WriteAllTextAsync(path, "{\"routerId\":\"truncated");

        OspfMigrationPlanStore store = CreateStore(root.Path);
        store.CleanupOrphansOnStartup();

        Assert.True(File.Exists(path));
        Assert.Null(await store.TryLoadAsync(job, "AP01", CancellationToken.None));
    }

    /// <summary>
    /// Scenario: the <c>OspfPlans</c> folder holds a README.txt, an <c>other.tmp</c> and an <c>orphan.json</c> that the store did not create.
    /// Expected: after startup cleanup all three files still exist.
    /// Invariant: cleanup only deletes files matching the store's own naming scheme.
    /// </summary>
    [Fact]
    public void StartupCleanup_LeavesUnrelatedFilesUntouched()
    {
        string backupRoot = Path.Combine(Path.GetTempPath(), "ospf-plans-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(backupRoot, "OspfPlans");
        Directory.CreateDirectory(dir);
        string readme = Path.Combine(dir, "README.txt");
        string randomTmp = Path.Combine(dir, "other.tmp");
        string randomJson = Path.Combine(dir, "orphan.json");
        File.WriteAllText(readme, "keep");
        File.WriteAllText(randomTmp, "keep");
        File.WriteAllText(randomJson, "{\"keep\":true}");
        try
        {
            OspfMigrationPlanStore store = CreateStore(backupRoot);
            store.CleanupOrphansOnStartup();
            Assert.True(File.Exists(readme));
            Assert.True(File.Exists(randomTmp));
            Assert.True(File.Exists(randomJson));
        }
        finally
        {
            if (Directory.Exists(backupRoot))
            {
                Directory.Delete(backupRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Scenario: the store's <c>.tmp</c> file is held open with an exclusive lock while startup cleanup runs.
    /// Expected: cleanup does not throw and the locked file still exists.
    /// Invariant: an undeletable orphan must not crash application startup.
    /// </summary>
    [Fact]
    public async Task StartupCleanup_InaccessibleTemp_DoesNotCrash()
    {
        await using TempBackupRoot root = new();
        MikrotikScheduledJob job = CreateJob();
        string dir = Path.Combine(root.Path, "OspfPlans");
        Directory.CreateDirectory(dir);
        string tempPath = PlanPath(root.Path, job) + ".tmp";
        await File.WriteAllTextAsync(tempPath, "locked");

        using (FileStream locked = new(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            OspfMigrationPlanStore store = CreateStore(root.Path);
            store.CleanupOrphansOnStartup(); // must not throw
            Assert.True(File.Exists(tempPath));
            GC.KeepAlive(locked);
        }
    }

    /// <summary>
    /// Scenario: a saved plan has an orphaned <c>.tmp</c> file beside it, and startup cleanup runs twice on a restarted store.
    /// Expected: the plan file remains and the temp file is gone.
    /// Invariant: repeated cleanup is safe and converges on the same state.
    /// </summary>
    [Fact]
    public async Task StartupCleanup_IsIdempotent()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        await store.SaveAsync(CreateHashedPlan(job, "AP01"), CancellationToken.None);
        string planPath = PlanPath(root.Path, job);
        string tempPath = planPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, "partial");

        OspfMigrationPlanStore restarted = CreateStore(root.Path);
        restarted.CleanupOrphansOnStartup();
        restarted.CleanupOrphansOnStartup();

        Assert.True(File.Exists(planPath));
        Assert.False(File.Exists(tempPath));
    }

    /// <summary>
    /// Scenario: a plan is retained across a restart, then a new job with a different import id and job id is created for the same router and correlation.
    /// Expected: loading for the new job returns null, while the original plan file still exists on disk.
    /// Invariant: a retained plan is never reused by a later CSV import.
    /// </summary>
    [Fact]
    public async Task RetainedPlan_IsNotAssociatedWithNewCsvImport()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob original = CreateJob();
        await store.SaveAsync(CreateHashedPlan(original, "AP01"), CancellationToken.None);

        OspfMigrationPlanStore restarted = CreateStore(root.Path);
        restarted.CleanupOrphansOnStartup();

        MikrotikScheduledJob reimported = CreateJob(); // new ImportId + JobId
        Assert.NotEqual(original.ImportId, reimported.ImportId);
        Assert.NotEqual(original.JobId, reimported.JobId);
        Assert.Null(await restarted.TryLoadAsync(reimported, "AP01", CancellationToken.None));
        Assert.True(File.Exists(PlanPath(root.Path, original)));
    }

    /// <summary>
    /// Scenario: ownership checks are run against a name produced by <c>BuildFileName</c> and against "orphan.json" files.
    /// Expected: the built name is recognised as an owned plan JSON, its <c>.tmp</c> variant as an owned temp file, and "orphan.json" and "orphan.json.tmp" are not.
    /// Invariant: the cleanup ownership test agrees with the file naming scheme.
    /// </summary>
    [Fact]
    public void OwnedPlanFileName_MatchesBuildFileName()
    {
        MikrotikScheduledJob job = CreateJob();
        string name = OspfMigrationPlanStore.BuildFileName(job.JobId, job.ImportId, job.RouterIp.ToString());
        Assert.True(OspfMigrationPlanStore.IsOwnedPlanJsonFileName(name));
        Assert.True(OspfMigrationPlanStore.IsOwnedPlanTempFileName(name + ".tmp"));
        Assert.False(OspfMigrationPlanStore.IsOwnedPlanJsonFileName("orphan.json"));
        Assert.False(OspfMigrationPlanStore.IsOwnedPlanTempFileName("orphan.json.tmp"));
    }

    /// <summary>
    /// Returns the expected plan file path under the backup root's <c>OspfPlans</c> folder for the job.
    /// </summary>
    private static string PlanPath(string backupRoot, MikrotikScheduledJob job) =>
        Path.Combine(
            backupRoot,
            "OspfPlans",
            OspfMigrationPlanStore.BuildFileName(job.JobId, job.ImportId, job.RouterIp.ToString()));

    /// <summary>
    /// Builds a minimal valid plan for the job and identity and stamps it with its computed content hash.
    /// </summary>
    private static OspfMigrationPlan CreateHashedPlan(MikrotikScheduledJob job, string identity)
    {
        OspfMigrationPlan plan = PassingOspfMigrationService.CreateMinimalValidPlan(
            job,
            identity,
            "6.49.23",
            "mipsbe");
        plan.ContentHash = OspfMigrationPlanner.ComputeContentHash(plan);
        return plan;
    }

    /// <summary>Creates a plan store whose backups live under <paramref name="backupRoot"/> (firmware upgrade disabled).</summary>
    /// <param name="backupRoot">Backup directory used by the store.</param>
    private static OspfMigrationPlanStore CreateStore(string backupRoot) =>
        new(
            Options.Create(new FirmwareUpgradeOptions
            {
                Enabled = false,
                BackupDirectory = backupRoot,
                AdvertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
                FetchTimeout = TimeSpan.FromSeconds(5),
                ReconnectInterval = TimeSpan.FromSeconds(1),
                RebootTimeout = TimeSpan.FromSeconds(5),
                PostUpgradeVerifyTimeout = TimeSpan.FromSeconds(5),
                BackupFileReadyTimeout = TimeSpan.FromSeconds(1),
            }),
            NullLogger<OspfMigrationPlanStore>.Instance);

    /// <summary>
    /// Creates a scheduled job for a fixed router with a window around the current time.
    /// </summary>
    private static MikrotikScheduledJob CreateJob() => new()
    {
        ImportId = Guid.NewGuid(),
        SourceFileName = "ospf.csv",
        RowNumber = 3,
        RouterName = "AP01",
        RouterIp = IPAddress.Parse("10.0.0.9"),
        DateStartUtc = DateTime.UtcNow.AddMinutes(-5),
        DateStopUtc = DateTime.UtcNow.AddHours(1),
    };

    /// <summary>
    /// Temp directory fixture that creates a unique backup root and deletes it on async dispose.
    /// </summary>
    private sealed class TempBackupRoot : IAsyncDisposable
    {
        /// <summary>Full path of the temp backup directory.</summary>
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ospf-plans-" + Guid.NewGuid().ToString("N"));

        /// <summary>Creates the temp backup directory.</summary>
        public TempBackupRoot() => Directory.CreateDirectory(Path);

        /// <summary>Deletes the temp backup directory and its contents.</summary>
        /// <returns>A completed task.</returns>
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
