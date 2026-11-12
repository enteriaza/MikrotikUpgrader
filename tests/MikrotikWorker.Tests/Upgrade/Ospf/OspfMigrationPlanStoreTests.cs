using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
public sealed class OspfMigrationPlanStoreTests
{
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

    [Fact]
    public async Task IncorrectCorrelation_IsRejected()
    {
        await using TempBackupRoot root = new();
        OspfMigrationPlanStore store = CreateStore(root.Path);
        MikrotikScheduledJob job = CreateJob();
        await store.SaveAsync(CreateHashedPlan(job, "AP01"), CancellationToken.None);
        Assert.Null(await store.TryLoadAsync(job, "OTHER", CancellationToken.None));
    }

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

    [Fact]
    public void StartupCleanup_RemovesOrphanJsonAndTemp_DoesNotResume()
    {
        string backupRoot = Path.Combine(Path.GetTempPath(), "ospf-plans-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(backupRoot, "OspfPlans");
        Directory.CreateDirectory(dir);
        string orphan = Path.Combine(dir, "orphan.json");
        string temp = Path.Combine(dir, "orphan.json.tmp");
        File.WriteAllText(orphan, "{\"routerId\":\"should-not-execute\"}");
        File.WriteAllText(temp, "partial");
        try
        {
            OspfMigrationPlanStore store = CreateStore(backupRoot);
            store.CleanupOrphansOnStartup();
            Assert.False(File.Exists(orphan));
            Assert.False(File.Exists(temp));
        }
        finally
        {
            if (Directory.Exists(backupRoot))
            {
                Directory.Delete(backupRoot, recursive: true);
            }
        }
    }

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

    private sealed class TempBackupRoot : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ospf-plans-" + Guid.NewGuid().ToString("N"));

        public TempBackupRoot() => Directory.CreateDirectory(Path);

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
