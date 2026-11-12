// -----------------------------------------------------------------------------
// File: LegacyFleetUpgradeAutomationCleanup.cs
//
// Description:
// Removes legacy router-side FleetUpgrade automation objects so this
// application remains the sole firmware-management authority.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Removes legacy router-side FleetUpgrade automation objects so this application
/// remains the sole firmware-management authority.
/// </summary>
internal static class LegacyFleetUpgradeObjectNames
{
    /// <summary>
    /// Legacy FleetUpgrade script name removed by cleanup.
    /// </summary>
    public const string ScriptName = "FleetUpgrade";
    /// <summary>
    /// Legacy FleetUpgrade scheduler name removed by cleanup.
    /// </summary>
    public const string SchedulerName = "FleetUpgrade-startup";
}

/// <summary>
/// Removes and verifies absence of legacy router-side FleetUpgrade script/scheduler objects before firmware mutation.
/// </summary>
internal sealed class LegacyFleetUpgradeAutomationCleanup(
    IMikrotikRouterOperations routerOperations,
    ILogger<LegacyFleetUpgradeAutomationCleanup> logger)
{
    /// <summary>
    /// Removes FleetUpgrade script/scheduler objects and verifies they are absent.
    /// </summary>
    public async Task<LegacyFleetUpgradeCleanupResult> CleanupAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(job);

        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogInformation("Legacy FleetUpgrade automation cleanup started");
        }

        using (DeviceLogging.BeginServiceOnly())
        {
            logger.LogInformation(
                "Legacy FleetUpgrade automation cleanup started correlation. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
                job.ImportId,
                job.JobId,
                job.RouterIp);
        }

        int schedulerRemoved = 0;
        int scriptRemoved = 0;

        // 1–3: scheduler find → remove-by-.id (via ops) → verify absent before touching scripts.
        int schedulerBefore;
        try
        {
            schedulerBefore = await routerOperations
                .CountSchedulersByNameAsync(
                    connection,
                    LegacyFleetUpgradeObjectNames.SchedulerName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(
                $"Legacy FleetUpgrade scheduler inspection failed: {ex.Message}",
                schedulerFound: null,
                scriptFound: null);
        }

        logger.LogInformation(
            "Legacy FleetUpgrade scheduler inspection: name={SchedulerName} count={SchedulerCount}",
            LegacyFleetUpgradeObjectNames.SchedulerName,
            schedulerBefore);

        if (schedulerBefore > 0)
        {
            try
            {
                await routerOperations
                    .RemoveSchedulersByNameAsync(
                        connection,
                        LegacyFleetUpgradeObjectNames.SchedulerName,
                        cancellationToken)
                    .ConfigureAwait(false);
                schedulerRemoved = schedulerBefore;
                logger.LogInformation(
                    "Removed legacy scheduler {SchedulerName} (pre-count={Count})",
                    LegacyFleetUpgradeObjectNames.SchedulerName,
                    schedulerBefore);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail(
                    $"Failed to remove legacy scheduler '{LegacyFleetUpgradeObjectNames.SchedulerName}': {ex.Message}",
                    schedulerBefore,
                    scriptFound: null);
            }
        }

        int schedulerAfter;
        try
        {
            schedulerAfter = await routerOperations
                .CountSchedulersByNameAsync(
                    connection,
                    LegacyFleetUpgradeObjectNames.SchedulerName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(
                $"Legacy FleetUpgrade scheduler post-removal verification failed: {ex.Message}",
                schedulerBefore,
                scriptFound: null);
        }

        if (schedulerAfter != 0)
        {
            return Fail(
                $"Legacy scheduler '{LegacyFleetUpgradeObjectNames.SchedulerName}' remains after cleanup (count={schedulerAfter}).",
                schedulerBefore,
                scriptFound: null);
        }

        // 4–6: script find → remove-by-.id → verify absent.
        int scriptBefore;
        try
        {
            scriptBefore = await routerOperations
                .CountSystemScriptsByNameAsync(
                    connection,
                    LegacyFleetUpgradeObjectNames.ScriptName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(
                $"Legacy FleetUpgrade script inspection failed: {ex.Message}",
                schedulerBefore,
                scriptFound: null);
        }

        logger.LogInformation(
            "Legacy FleetUpgrade script inspection: name={ScriptName} count={ScriptCount}",
            LegacyFleetUpgradeObjectNames.ScriptName,
            scriptBefore);

        if (scriptBefore > 0)
        {
            try
            {
                await routerOperations
                    .RemoveSystemScriptsByNameAsync(
                        connection,
                        LegacyFleetUpgradeObjectNames.ScriptName,
                        cancellationToken)
                    .ConfigureAwait(false);
                scriptRemoved = scriptBefore;
                logger.LogInformation(
                    "Removed legacy script {ScriptName} (pre-count={Count})",
                    LegacyFleetUpgradeObjectNames.ScriptName,
                    scriptBefore);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail(
                    $"Failed to remove legacy script '{LegacyFleetUpgradeObjectNames.ScriptName}': {ex.Message}",
                    schedulerBefore,
                    scriptBefore);
            }
        }

        int scriptAfter;
        try
        {
            scriptAfter = await routerOperations
                .CountSystemScriptsByNameAsync(
                    connection,
                    LegacyFleetUpgradeObjectNames.ScriptName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(
                $"Legacy FleetUpgrade script post-removal verification failed: {ex.Message}",
                schedulerBefore,
                scriptBefore);
        }

        if (scriptAfter != 0)
        {
            return Fail(
                $"Legacy script '{LegacyFleetUpgradeObjectNames.ScriptName}' remains after cleanup (count={scriptAfter}).",
                schedulerBefore,
                scriptBefore);
        }

        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogInformation(
                "Legacy FleetUpgrade automation cleanup completed. SchedulerRemoved={SchedulerRemoved}, ScriptRemoved={ScriptRemoved}",
                schedulerRemoved,
                scriptRemoved);
        }

        return new LegacyFleetUpgradeCleanupResult
        {
            Succeeded = true,
            SchedulerFound = schedulerBefore,
            ScriptFound = scriptBefore,
            SchedulerRemoved = schedulerRemoved,
            ScriptRemoved = scriptRemoved,
        };
    }

    /// <summary>
    /// Logs a service-level error and returns a failed cleanup result.
    /// </summary>
    /// <param name="detail">Why the cleanup failed.</param>
    /// <param name="schedulerFound">Count of legacy schedulers found, when known.</param>
    /// <param name="scriptFound">Count of legacy scripts found, when known.</param>
    /// <returns>A failed result carrying the detail and counts.</returns>
    private LegacyFleetUpgradeCleanupResult Fail(string detail, int? schedulerFound, int? scriptFound)
    {
        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogError("Legacy FleetUpgrade automation cleanup failed: {Detail}", detail);
        }

        return new LegacyFleetUpgradeCleanupResult
        {
            Succeeded = false,
            FailureReason = detail,
            SchedulerFound = schedulerFound,
            ScriptFound = scriptFound,
        };
    }
}

/// <summary>
/// Outcome of legacy FleetUpgrade automation cleanup, including found/removed counts.
/// </summary>
internal sealed class LegacyFleetUpgradeCleanupResult
{
    /// <summary>
    /// True when cleanup succeeded or objects were already absent.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Failure detail when <see cref="Succeeded"/> is false.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Number of matching scheduler objects found before removal.
    /// </summary>
    public int? SchedulerFound { get; init; }

    /// <summary>
    /// Number of matching script objects found before removal.
    /// </summary>
    public int? ScriptFound { get; init; }

    /// <summary>
    /// Number of scheduler objects removed.
    /// </summary>
    public int SchedulerRemoved { get; init; }

    /// <summary>
    /// Number of script objects removed.
    /// </summary>
    public int ScriptRemoved { get; init; }
}
