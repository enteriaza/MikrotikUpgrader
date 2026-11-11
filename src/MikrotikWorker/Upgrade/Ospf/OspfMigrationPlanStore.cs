using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Upgrade.Ospf;

internal interface IOspfMigrationPlanStore
{
    Task SaveAsync(OspfMigrationPlan plan, CancellationToken cancellationToken);

    Task<OspfMigrationPlan?> TryLoadAsync(
        MikrotikScheduledJob job,
        string identity,
        CancellationToken cancellationToken);

    void Delete(MikrotikScheduledJob job, IPAddress? routerIp = null);

    void CleanupOrphansOnStartup();
}

internal sealed class OspfMigrationPlanStore(
    IOptions<FirmwareUpgradeOptions> upgradeOptions,
    ILogger<OspfMigrationPlanStore> logger) : IOspfMigrationPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ConcurrentDictionary<string, byte> _activePaths = new(StringComparer.OrdinalIgnoreCase);

    public async Task SaveAsync(OspfMigrationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string dir = GetDirectory();
        Directory.CreateDirectory(dir);
        string path = GetFilePath(plan.JobId, plan.ImportId, plan.RouterIp);
        string temp = path + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions);
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            _activePaths[path] = 0;
            logger.LogInformation("OSPF migration plan persisted ({File})", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            TryDeleteFile(temp, "Failed cleaning temporary OSPF plan after save failure");
            logger.LogError(ex, "OSPF migration plan persistence failed for {Path}", path);
            throw;
        }
    }

    public async Task<OspfMigrationPlan?> TryLoadAsync(
        MikrotikScheduledJob job,
        string identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        string path = GetFilePath(job.JobId, job.ImportId, job.RouterIp.ToString());
        if (!File.Exists(path))
        {
            return null;
        }

        OspfMigrationPlan? plan;
        try
        {
            await using FileStream stream = File.OpenRead(path);
            plan = await JsonSerializer
                .DeserializeAsync<OspfMigrationPlan>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "OSPF migration plan at {Path} is truncated or malformed JSON", path);
            return null;
        }

        if (plan is null)
        {
            return null;
        }

        if (plan.JobId != job.JobId
            || plan.ImportId != job.ImportId
            || !string.Equals(plan.RouterIp, job.RouterIp.ToString(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(plan.Identity, identity, StringComparison.Ordinal))
        {
            logger.LogError("OSPF migration plan correlation mismatch for {Path}", path);
            return null;
        }

        if (string.IsNullOrWhiteSpace(plan.ContentHash))
        {
            logger.LogError("OSPF migration plan at {Path} is missing content hash", path);
            return null;
        }

        if (!plan.IsValid)
        {
            logger.LogError("OSPF migration plan at {Path} failed validation markers", path);
            return null;
        }

        if (!OspfMigrationPlanner.ContentHashMatches(plan))
        {
            logger.LogError(
                "OSPF migration plan at {Path} failed content-hash integrity check (stored={Stored})",
                path,
                plan.ContentHash);
            return null;
        }

        return plan;
    }

    public void Delete(MikrotikScheduledJob job, IPAddress? routerIp = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        string path = GetFilePath(job.JobId, job.ImportId, (routerIp ?? job.RouterIp).ToString());
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                logger.LogInformation("OSPF migration plan deleted ({File})", Path.GetFileName(path));
            }

            TryDeleteFile(path + ".tmp", "Failed deleting OSPF plan temp alongside plan delete");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete OSPF migration plan {Path}", path);
        }
        finally
        {
            _activePaths.TryRemove(path, out _);
        }
    }

    public void CleanupOrphansOnStartup()
    {
        string dir = GetDirectory();
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(path);
            bool isPlanJson = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase);
            bool isTemp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            if (!isPlanJson && !isTemp)
            {
                continue;
            }

            string activeKey = isTemp && name.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase)
                ? path[..^4] // strip ".tmp"
                : path;

            if (isPlanJson && _activePaths.ContainsKey(path))
            {
                continue;
            }

            if (isTemp && _activePaths.ContainsKey(activeKey))
            {
                // Active plan's sibling temp should not exist; still safe to remove abandoned temp only.
            }

            try
            {
                File.Delete(path);
                logger.LogInformation("Deleted orphaned OSPF plan artefact {File}", name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed deleting orphaned OSPF plan artefact {Path}", path);
            }
        }
    }

    private void TryDeleteFile(string path, string warningMessage)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Message}: {Path}", warningMessage, path);
        }
    }

    private string GetDirectory()
    {
        string root = upgradeOptions.Value.GetAbsoluteBackupDirectory();
        return Path.Combine(root, "OspfPlans");
    }

    private string GetFilePath(string jobId, Guid importId, string routerIp) =>
        Path.Combine(GetDirectory(), BuildFileName(jobId, importId, routerIp));

    internal static string BuildFileName(string jobId, Guid importId, string routerIp)
    {
        static string Sanitize(string value) => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        string ipKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(routerIp)))[..16];
        return $"job-{Sanitize(jobId)}_import-{importId:N}_ip-{ipKey}.json";
    }
}

internal sealed class OspfMigrationPlanStoreCleanupHostedService(
    IOspfMigrationPlanStore store,
    ILogger<OspfMigrationPlanStoreCleanupHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            store.CleanupOrphansOnStartup();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OSPF plan orphan cleanup failed on startup");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
