// -----------------------------------------------------------------------------
// File: RouterboardFirmwareUpgradeService.cs
//
// Description:
// Post–RouterOS-7 RouterBOARD (RouterBOOT) assessment, optional upgrade,
// reboot, and verification.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Post–RouterOS-7 RouterBOARD (RouterBOOT) assessment, optional upgrade, reboot, and verification.
/// </summary>
internal sealed class RouterboardFirmwareUpgradeService(
    IMikrotikRouterOperations routerOperations,
    IMikrotikConnectionFactory connectionFactory,
    IOptions<FirmwareUpgradeOptions> upgradeOptions,
    IOptions<MikroTikOptions> mikroTikOptions,
    TimeProvider timeProvider,
    ILogger<RouterboardFirmwareUpgradeService> logger)
{
    /// <summary>
    /// Assesses the RouterBOARD firmware and, when needed, upgrades it, reboots, reconnects, and verifies the result.
    /// Reports success without a reboot when no upgrade is required, and a failure when the assessment is inconclusive or the upgrade cannot be confirmed.
    /// </summary>
    /// <param name="connection">Open connection to the router.</param>
    /// <param name="job">Scheduled job being executed.</param>
    /// <param name="preInfo">Device information read before this stage.</param>
    /// <param name="username">RouterOS user name used to reconnect.</param>
    /// <param name="password">RouterOS password used to reconnect.</param>
    /// <param name="priorRebootInitiated">Whether an earlier stage already rebooted the router.</param>
    /// <param name="completedStageBefore">Last stage completed before this one, reported when this stage fails.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The outcome of the RouterBOARD stage.</returns>
    public async Task<UpgradeExecutionResult> ExecuteAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo preInfo,
        string username,
        string password,
        bool priorRebootInitiated,
        string completedStageBefore,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(preInfo);

        logger.LogInformation(
            "RouterBOARD assessment (raw): model={Model}, type={Type}, current-firmware={Current}, upgrade-firmware={Upgrade}, factory-firmware={Factory}",
            preInfo.RouterboardModel,
            preInfo.RouterboardFirmwareType,
            preInfo.RouterboardCurrentFirmware,
            preInfo.RouterboardUpgradeFirmware,
            preInfo.RouterboardFactoryFirmware);

        RouterboardUpgradeAssessment assessment = RouterboardFirmwareAssessor.Assess(preInfo);
        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogInformation(
                "RouterBOARD assessment result: {Decision}. {Reason}",
                assessment.Decision,
                assessment.Reason);
        }

        if (assessment.Decision == RouterboardUpgradeDecision.Inconclusive)
        {
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.RouterboardUpgradeFailed,
                Detail = assessment.Reason,
                PostUpgradeVersion = preInfo.Version,
                PostUpgradeArchitecture = preInfo.ArchitectureName,
                RebootInitiated = priorRebootInitiated,
                CompletedStage = completedStageBefore,
            };
        }

        if (assessment.Decision == RouterboardUpgradeDecision.NotRequired)
        {
            using (DeviceLogging.BeginServiceSummary())
            {
                logger.LogInformation("RouterBOARD upgrade not required; no additional reboot.");
            }

            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.UpgradeSucceeded,
                Detail = assessment.Reason,
                PostUpgradeVersion = preInfo.Version,
                PostUpgradeArchitecture = preInfo.ArchitectureName,
                RebootInitiated = priorRebootInitiated,
                CompletedStage = UpgradeCompletedStages.RouterboardFirmware,
            };
        }

        string expectedCurrentAfter = assessment.UpgradeFirmwareRaw!.Trim();
        string expectedRosVersion = preInfo.Version;
        string? expectedArch = preInfo.ArchitectureName;

        logger.LogInformation(
            "RouterBOARD upgrade required: current-firmware={Current} → upgrade-firmware={Upgrade}",
            assessment.CurrentFirmwareRaw,
            assessment.UpgradeFirmwareRaw);

        try
        {
            await routerOperations.UpgradeRouterboardAsync(connection, cancellationToken).ConfigureAwait(false);
            using (DeviceLogging.BeginServiceSummary())
            {
                logger.LogInformation("RouterBOARD upgrade command accepted.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RouterBOARD upgrade command failed");
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.RouterboardUpgradeFailed,
                Detail = $"RouterBOARD upgrade command failed: {ex.Message}. Pre: current={assessment.CurrentFirmwareRaw}, upgrade={assessment.UpgradeFirmwareRaw}.",
                PostUpgradeVersion = preInfo.Version,
                PostUpgradeArchitecture = preInfo.ArchitectureName,
                RebootInitiated = priorRebootInitiated,
                CompletedStage = completedStageBefore,
            };
        }

        FirmwareUpgradeOptions options = upgradeOptions.Value;
        logger.LogInformation("Initiating RouterBOARD reboot to activate RouterBOOT firmware");
        await routerOperations.RebootAsync(connection, cancellationToken).ConfigureAwait(false);
        bool rebootInitiated = true;
        DateTimeOffset rebootInitiatedAt = timeProvider.GetUtcNow();
        DateTimeOffset rebootDeadline = rebootInitiatedAt + options.RebootTimeout;
        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogInformation(
                "RouterBOARD reboot started; reconnection deadline {Deadline:yyyy-MM-dd HH:mm:ss} UTC ({Timeout})",
                rebootDeadline.UtcDateTime,
                options.RebootTimeout);
        }

        MikrotikConnectionOptions reconnectOptions = new()
        {
            Host = job.RouterIp.ToString(),
            Username = username,
            Password = password,
            Port = mikroTikOptions.Value.Port,
        };

        ITikConnection? reconnected = null;
        try
        {
            int attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTimeOffset now = timeProvider.GetUtcNow();
                TimeSpan remaining = rebootDeadline - now;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                attempt++;
                logger.LogInformation(
                    "RouterBOARD reconnection attempt {Attempt} (elapsed {Elapsed}, remaining {Remaining})",
                    attempt,
                    FormatDuration(now - rebootInitiatedAt),
                    FormatDuration(remaining));

                using CancellationTokenSource attemptCts = new();
                using ITimer attemptTimer = timeProvider.CreateTimer(
                    static state =>
                    {
                        try
                        {
                            ((CancellationTokenSource)state!).Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    },
                    attemptCts,
                    remaining,
                    Timeout.InfiniteTimeSpan);
                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    attemptCts.Token);

                try
                {
                    reconnected = await connectionFactory
                        .CreateAsync(reconnectOptions, linkedCts.Token)
                        .ConfigureAwait(false);
                    logger.LogInformation(
                        "RouterBOARD reconnect succeeded after {Elapsed}",
                        FormatDuration(timeProvider.GetUtcNow() - rebootInitiatedAt));
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.UpgradeInterrupted,
                        Detail = "Cancelled while waiting for RouterBOARD reboot reconnect.",
                        RebootInitiated = rebootInitiated,
                        CompletedStage = completedStageBefore,
                    };
                }
                catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    TimeSpan delay = options.ReconnectInterval < remaining
                        ? options.ReconnectInterval
                        : remaining;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (reconnected is null)
            {
                using (DeviceLogging.BeginServiceSummary())
                {
                    logger.LogError("RouterBOARD reboot reconnection timed out");
                }

                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.RouterboardUpgradeTimedOut,
                    Detail = $"Router did not reconnect within {options.RebootTimeout} after RouterBOARD upgrade reboot.",
                    RebootInitiated = rebootInitiated,
                    CompletedStage = completedStageBefore,
                };
            }

            MikrotikDeviceInfo post = await routerOperations
                .ReadDeviceInfoAsync(reconnected, job.RouterIp.ToString(), cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "RouterBOARD after reboot (raw): model={Model}, type={Type}, current-firmware={Current}, upgrade-firmware={Upgrade}, factory-firmware={Factory}",
                post.RouterboardModel,
                post.RouterboardFirmwareType,
                post.RouterboardCurrentFirmware,
                post.RouterboardUpgradeFirmware,
                post.RouterboardFactoryFirmware);

            if (!string.Equals(
                    post.RouterboardCurrentFirmware?.Trim(),
                    expectedCurrentAfter,
                    StringComparison.Ordinal))
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.RouterboardVerificationFailed,
                    Detail =
                        $"Post-reboot RouterBOARD current-firmware '{post.RouterboardCurrentFirmware}' did not match expected '{expectedCurrentAfter}'. Pre: current={assessment.CurrentFirmwareRaw}, upgrade={assessment.UpgradeFirmwareRaw}.",
                    PostUpgradeVersion = post.Version,
                    PostUpgradeArchitecture = post.ArchitectureName,
                    RebootInitiated = rebootInitiated,
                    CompletedStage = completedStageBefore,
                };
            }

            if (!RouterOsVersion.TryParse(expectedRosVersion, out RouterOsVersion expectedVer)
                || !RouterOsVersion.TryParse(post.Version, out RouterOsVersion postVer)
                || expectedVer != postVer)
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.RouterboardVerificationFailed,
                    Detail =
                        $"RouterOS version changed during RouterBOARD upgrade (before={expectedRosVersion}, after={post.Version}).",
                    PostUpgradeVersion = post.Version,
                    PostUpgradeArchitecture = post.ArchitectureName,
                    RebootInitiated = rebootInitiated,
                    CompletedStage = completedStageBefore,
                };
            }

            string? beforeArch = string.IsNullOrWhiteSpace(expectedArch)
                ? null
                : FirmwarePackageFileNameParser.NormalizeArchitecture(expectedArch);
            string? afterArch = string.IsNullOrWhiteSpace(post.ArchitectureName)
                ? null
                : FirmwarePackageFileNameParser.NormalizeArchitecture(post.ArchitectureName);
            if (!string.Equals(beforeArch, afterArch, StringComparison.Ordinal))
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.RouterboardVerificationFailed,
                    Detail =
                        $"Architecture changed during RouterBOARD upgrade (before={beforeArch}, after={afterArch}).",
                    PostUpgradeVersion = post.Version,
                    PostUpgradeArchitecture = post.ArchitectureName,
                    RebootInitiated = rebootInitiated,
                    CompletedStage = completedStageBefore,
                };
            }

            using (DeviceLogging.BeginServiceSummary())
            {
                logger.LogInformation(
                    "RouterBOARD upgrade verified. current-firmware={Current}",
                    post.RouterboardCurrentFirmware);
            }

            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.UpgradeSucceeded,
                PostUpgradeVersion = post.Version,
                PostUpgradeArchitecture = post.ArchitectureName,
                RebootInitiated = rebootInitiated,
                CompletedStage = UpgradeCompletedStages.RouterboardFirmware,
            };
        }
        finally
        {
            reconnected?.Dispose();
        }
    }

    /// <summary>
    /// Formats an elapsed or remaining time for log messages, as minutes and seconds or as seconds.
    /// </summary>
    /// <param name="value">Duration to format; negative values are treated as zero.</param>
    /// <returns>The formatted text.</returns>
    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalMinutes >= 1
            ? $"{(int)value.TotalMinutes}m {value.Seconds:D2}s"
            : $"{value.TotalSeconds:0.###}s";
    }
}
