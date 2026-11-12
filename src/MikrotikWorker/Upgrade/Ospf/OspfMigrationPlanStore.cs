// -----------------------------------------------------------------------------
// File: OspfMigrationPlanStore.cs
//
// Description:
// Persists and loads OSPF migration plans on disk keyed by scheduled job, with
// identity-safe load checks.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Serialization;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Persists, loads, and deletes OSPF migration plans for scheduled jobs.
/// </summary>
internal interface IOspfMigrationPlanStore
{
    /// <summary>
    /// Persists a validated migration plan.
    /// </summary>
    /// <param name="plan">Plan to write.</param>
    /// <param name="cancellationToken">Token to cancel the write.</param>
    /// <returns>A task that completes when the plan is on disk.</returns>
    Task SaveAsync(OspfMigrationPlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the persisted plan for a job if it passes correlation and integrity checks.
    /// </summary>
    /// <param name="job">Scheduled job whose plan is wanted.</param>
    /// <param name="identity">Router identity the plan must match.</param>
    /// <param name="cancellationToken">Token to cancel the read.</param>
    /// <returns>The plan, or null when missing, malformed, mismatched, or failing integrity checks.</returns>
    Task<OspfMigrationPlan?> TryLoadAsync(
        MikrotikScheduledJob job,
        string identity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the persisted plan for a job.
    /// </summary>
    /// <param name="job">Scheduled job whose plan is removed.</param>
    /// <param name="routerIp">Router address to use instead of the job address.</param>
    void Delete(MikrotikScheduledJob job, IPAddress? routerIp = null);

    /// <summary>
    /// Removes orphaned temporary plan files left by an earlier run and logs retained plans.
    /// </summary>
    void CleanupOrphansOnStartup();
}

/// <summary>
/// File-based OSPF migration plan store under the configured plans directory.
/// </summary>
internal sealed class OspfMigrationPlanStore(
    IOptions<FirmwareUpgradeOptions> upgradeOptions,
    ILogger<OspfMigrationPlanStore> logger) : IOspfMigrationPlanStore
{
    // job-{sanitized}_import-{32 hex}_ip-{16 hex}.json
    /// <summary>
    /// Matches file names produced by <see cref="BuildFileName"/> so unrelated files are never touched.
    /// </summary>
    private static readonly Regex OwnedPlanFileName = new(
        @"^job-[A-Za-z0-9\-]+_import-[0-9a-fA-F]{32}_ip-[0-9a-fA-F]{16}\.json$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Plan paths currently being written or retained by this process, protected from cleanup.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _activePaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Persists a validated migration plan for the job.
    /// </summary>
    public async Task SaveAsync(OspfMigrationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string dir = GetDirectory();
        Directory.CreateDirectory(dir);
        string path = GetFilePath(plan.JobId, plan.ImportId, plan.RouterIp);
        string temp = path + ".tmp";
        // Claim before writing so startup/concurrent cleanup cannot remove an in-flight temp.
        _activePaths[path] = 0;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                plan,
                OspfMigrationPlanJsonContext.Default.OspfMigrationPlan);
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            logger.LogInformation("OSPF migration plan persisted ({File})", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            TryDeleteFile(temp, "Failed cleaning temporary OSPF plan after save failure");
            _activePaths.TryRemove(path, out _);
            logger.LogError(ex, "OSPF migration plan persistence failed for {Path}", path);
            throw;
        }
    }

    /// <summary>
    /// Loads a persisted plan for the job when present and identity-safe.
    /// </summary>
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
                .DeserializeAsync(stream, OspfMigrationPlanJsonContext.Default.OspfMigrationPlan, cancellationToken)
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

    /// <summary>
    /// Deletes any persisted plan files for the job.
    /// </summary>
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

    /// <summary>
    /// Deletes orphaned owned temp files and logs retained plan JSON; unrelated files are ignored.
    /// </summary>
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
            try
            {
                if (IsOwnedPlanTempFileName(name))
                {
                    CleanupOwnedTempIfSafe(path, name);
                    continue;
                }

                if (IsOwnedPlanJsonFileName(name))
                {
                    // Diagnostic retention: never delete completed JSON merely because this
                    // process's in-memory active set is empty after restart. No TTL — plans
                    // remain until the established success-path Delete removes them.
                    LogRetainedPlanDiagnostics(path, name);
                    continue;
                }

                // Unrelated files in OspfPlans are left untouched.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed during OSPF plan startup cleanup for {Path}", path);
            }
        }
    }

    /// <summary>
    /// Deletes an owned temporary plan file unless its plan is actively being written.
    /// </summary>
    /// <param name="path">Full path of the temp file.</param>
    /// <param name="name">File name, used for logging.</param>
    private void CleanupOwnedTempIfSafe(string path, string name)
    {
        string jsonPath = path[..^4]; // strip ".tmp"
        if (_activePaths.ContainsKey(jsonPath))
        {
            logger.LogInformation(
                "Retaining active OSPF plan temporary file during startup cleanup ({File})",
                name);
            return;
        }

        try
        {
            File.Delete(path);
            logger.LogInformation("Deleted orphaned OSPF plan temporary file ({File})", name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed deleting orphaned OSPF plan temporary file {Path}", path);
        }
    }

    /// <summary>
    /// Logs why a plan file is retained, without deleting it.
    /// </summary>
    /// <param name="path">Full path of the plan JSON.</param>
    /// <param name="name">File name, used for logging.</param>
    private void LogRetainedPlanDiagnostics(string path, string name)
    {
        if (_activePaths.ContainsKey(path))
        {
            logger.LogInformation(
                "Retaining active OSPF migration plan during startup cleanup ({File})",
                name);
            return;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            OspfMigrationPlan? plan = JsonSerializer.Deserialize(
                stream,
                OspfMigrationPlanJsonContext.Default.OspfMigrationPlan);
            if (plan is null)
            {
                logger.LogWarning(
                    "Retaining OSPF migration plan that deserialized to null ({File})",
                    name);
                return;
            }

            logger.LogInformation(
                "Retaining inactive OSPF migration plan for diagnosis ({File})",
                name);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex,
                "Retaining malformed OSPF migration plan for diagnosis ({File})",
                name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Retaining OSPF migration plan that could not be inspected ({File})",
                name);
        }
    }

    /// <summary>
    /// Deletes a file if it exists, logging a warning instead of throwing on failure.
    /// </summary>
    /// <param name="path">File to delete.</param>
    /// <param name="warningMessage">Message logged when deletion fails.</param>
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

    /// <summary>
    /// Resolves the folder that holds persisted migration plan files: an <c>OspfPlans</c> subfolder of the
    /// configured absolute backup directory. The path is computed only; the folder is not created here.
    /// </summary>
    /// <returns>The absolute plans directory path.</returns>
    private string GetDirectory()
    {
        string root = upgradeOptions.Value.GetAbsoluteBackupDirectory();
        return Path.Combine(root, "OspfPlans");
    }

    /// <summary>
    /// Builds the full path of the plan file for a job, import, and router.
    /// </summary>
    /// <param name="jobId">Scheduled job identifier.</param>
    /// <param name="importId">Import identifier.</param>
    /// <param name="routerIp">Router IP address text.</param>
    /// <returns>The full file path.</returns>
    private string GetFilePath(string jobId, Guid importId, string routerIp) =>
        Path.Combine(GetDirectory(), BuildFileName(jobId, importId, routerIp));

    /// <summary>
    /// Builds the deterministic plan file name from sanitised job ID, import ID, and a hash of the router IP.
    /// </summary>
    /// <param name="jobId">Scheduled job identifier.</param>
    /// <param name="importId">Import identifier.</param>
    /// <param name="routerIp">Router IP address text.</param>
    /// <returns>The plan file name.</returns>
    internal static string BuildFileName(string jobId, Guid importId, string routerIp)
    {
        static string Sanitize(string value) => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        string ipKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(routerIp)))[..16];
        return $"job-{Sanitize(jobId)}_import-{importId:N}_ip-{ipKey}.json";
    }

    /// <summary>
    /// Determines whether a file name has the plan JSON naming pattern.
    /// </summary>
    /// <param name="fileName">File name without directory.</param>
    /// <returns><see langword="true"/> when the name matches.</returns>
    internal static bool IsOwnedPlanJsonFileName(string fileName) =>
        OwnedPlanFileName.IsMatch(fileName);

    /// <summary>
    /// Determines whether a file name is a plan JSON name with a <c>.tmp</c> suffix.
    /// </summary>
    /// <param name="fileName">File name without directory.</param>
    /// <returns><see langword="true"/> when the name matches.</returns>
    internal static bool IsOwnedPlanTempFileName(string fileName) =>
        fileName.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase)
        && IsOwnedPlanJsonFileName(fileName[..^4]);
}

/// <summary>
/// Hosted service that periodically cleans expired OSPF migration plan files.
/// </summary>
internal sealed class OspfMigrationPlanStoreCleanupHostedService(
    IOspfMigrationPlanStore store,
    ILogger<OspfMigrationPlanStoreCleanupHostedService> logger) : IHostedService
{
    /// <summary>
    /// Runs the one-time orphan cleanup at startup, logging rather than propagating failures.
    /// </summary>
    /// <param name="cancellationToken">Startup cancellation token.</param>
    /// <returns>A completed task.</returns>
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

    /// <summary>
    /// Does nothing; there is no state to release on shutdown.
    /// </summary>
    /// <param name="cancellationToken">Shutdown cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
