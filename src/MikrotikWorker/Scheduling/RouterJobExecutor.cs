using System.Diagnostics;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Connect, pre-flight, compatibility assessment, optional v6 upgrade, and disconnect for one router job.
/// </summary>
internal sealed class RouterJobExecutor(
    IMikrotikConnectionFactory connectionFactory,
    IMikrotikDeviceInfoReader deviceInfoReader,
    IRouterCredentialResolver credentialResolver,
    IDeviceCompatibilityAssessor compatibilityAssessor,
    IRouterOsV6UpgradeExecutor upgradeExecutor,
    IOptions<MikroTikOptions> mikroTikOptions,
    IOptions<FirmwareAssessmentOptions> assessmentOptions,
    IOptions<FirmwareUpgradeOptions> upgradeOptions,
    TimeProvider timeProvider,
    ILogger<RouterJobExecutor> logger)
{
    public async Task ExecuteAsync(
        RouterJobWorkItem workItem,
        IRouterJobExecutionCompleter completer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(completer);

        MikrotikScheduledJob job = workItem.Job;
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            if (completer.IsImportAbortedOrStopping(job.ImportId) || cancellationToken.IsCancellationRequested)
            {
                CompleteCancelled(workItem, completer, stopwatch.Elapsed, job);
                return;
            }

            DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
            if (utcNow >= job.DateStopUtc)
            {
                logger.LogWarning(
                    "Job expired while waiting for an execution worker. ImportId={ImportId}, JobId={JobId}, RowNumber={RowNumber}, RouterName={RouterName}, RouterIp={RouterIp}, DateStartUtc={DateStartUtc}, DateStopUtc={DateStopUtc}, UtcNow={UtcNow}, JobOutcome={JobOutcome}",
                    job.ImportId,
                    job.JobId,
                    job.RowNumber,
                    job.RouterName,
                    job.RouterIp,
                    job.DateStartUtc,
                    job.DateStopUtc,
                    utcNow,
                    nameof(ScheduledJobTerminalState.Expired));
                completer.CompleteDispatchedJob(
                    workItem.EntryToken,
                    ScheduledJobTerminalState.Expired,
                    stopwatch.Elapsed);
                return;
            }

            if (utcNow < job.DateStartUtc)
            {
                logger.LogWarning(
                    "Job not yet eligible at worker start; returning to scheduler. ImportId={ImportId}, JobId={JobId}, DateStartUtc={DateStartUtc}, UtcNow={UtcNow}",
                    job.ImportId,
                    job.JobId,
                    job.DateStartUtc,
                    utcNow);
                if (!completer.TryRequeueDispatchedJob(workItem.EntryToken))
                {
                    CompleteCancelled(workItem, completer, stopwatch.Elapsed, job);
                }

                return;
            }

            if (!mikroTikOptions.Value.Enabled)
            {
                logger.LogError(
                    "MikroTik integration is disabled; cannot execute job. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, JobOutcome={JobOutcome}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    nameof(ScheduledJobTerminalState.Failed));
                completer.CompleteDispatchedJob(
                    workItem.EntryToken,
                    ScheduledJobTerminalState.Failed,
                    stopwatch.Elapsed);
                return;
            }

            using DeviceLogScope deviceScope = DeviceLogScope.Begin(job);

            logger.LogInformation(
                "Router job started. ImportId={ImportId}, JobId={JobId}, RowNumber={RowNumber}, RouterName={RouterName}, RouterIp={RouterIp}, DateStartUtc={DateStartUtc}, DateStopUtc={DateStopUtc}, DeviceLogKey={DeviceLogKey}",
                job.ImportId,
                job.JobId,
                job.RowNumber,
                job.RouterName,
                job.RouterIp,
                job.DateStartUtc,
                job.DateStopUtc,
                deviceScope.DeviceLogKey);

            logger.LogInformation(
                "Credential resolution attempted. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}",
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp);

            RouterCredentials? credentials;
            try
            {
                credentials = await ResolveCredentialsAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CompleteCancelled(workItem, completer, stopwatch.Elapsed, job);
                return;
            }

            if (credentials is null)
            {
                logger.LogError(
                    "Credential resolution failed. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, JobOutcome={JobOutcome}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    nameof(ScheduledJobTerminalState.Failed));
                completer.CompleteDispatchedJob(
                    workItem.EntryToken,
                    ScheduledJobTerminalState.Failed,
                    stopwatch.Elapsed);
                return;
            }

            logger.LogInformation(
                "Credential resolution successful. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}",
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp);

            MikrotikConnectionOptions connectionOptions = new()
            {
                Host = job.RouterIp.ToString(),
                Username = credentials.Username,
                Password = credentials.Password,
            };

            logger.LogInformation(
                "Connection attempt started. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}",
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp);

            ITikConnection? connection = null;
            try
            {
                connection = await connectionFactory
                    .CreateAsync(connectionOptions, cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Connection succeeded. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp);

                MikrotikDeviceInfo deviceInfo;
                try
                {
                    deviceInfo = await deviceInfoReader
                        .ReadDeviceInfoAsync(connection, connectionOptions.Host, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    logger.LogError(
                        ex,
                        "Device information retrieval failed. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp,
                        nameof(ScheduledJobTerminalState.Failed));
                    SafeDispose(connection, workItem, completer, stopwatch, job, alreadyFailed: true);
                    return;
                }

                deviceScope.SetRouterIdentity(deviceInfo.Identity);
                logger.LogInformation(
                    "Device identity retrieved. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, RouterIdentity={RouterIdentity}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    deviceInfo.Identity);
                logger.LogInformation(
                    "RouterOS and hardware information retrieved. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, RouterIdentity={RouterIdentity}, RouterOsVersion={RouterOsVersion}, Architecture={Architecture}, BoardName={BoardName}, Platform={Platform}, Uptime={Uptime}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    deviceInfo.Identity,
                    deviceInfo.Version,
                    deviceInfo.ArchitectureName,
                    deviceInfo.BoardName,
                    deviceInfo.Platform,
                    deviceInfo.Uptime);

                MikrotikPreflightResult preflight = MikrotikPreflightResult.FromDeviceInfo(
                    connectionOptions.Host,
                    deviceInfo,
                    timeProvider.GetUtcNow(),
                    stopwatch.Elapsed);

                if (preflight.Status != MikrotikPreflightStatus.Succeeded)
                {
                    logger.LogError(
                        "Pre-flight validation failed. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, FailureReason={FailureReason}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp,
                        preflight.FailureReason,
                        nameof(ScheduledJobTerminalState.Failed));
                    SafeDispose(connection, workItem, completer, stopwatch, job, alreadyFailed: true);
                    return;
                }

                logger.LogInformation(
                    "Compatibility assessment started. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, TargetRouterOsVersion={TargetRouterOsVersion}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    assessmentOptions.Value.TargetRouterOsVersion);

                DeviceCompatibilityAssessment assessment = compatibilityAssessor.Assess(
                    connectionOptions.Host,
                    deviceInfo,
                    timeProvider.GetUtcNow());

                deviceScope.SetAssessmentProperties(
                    assessment.RouterOsVersionRaw,
                    assessment.Architecture,
                    assessment.TargetRouterOsVersion,
                    assessment.OverallStatus.ToString());

                foreach (AssessmentFinding finding in assessment.Findings)
                {
                    LogFinding(job, finding);
                }

                logger.LogInformation(
                    "Overall assessment result. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, RouterIdentity={RouterIdentity}, RouterOsVersion={RouterOsVersion}, Architecture={Architecture}, TargetRouterOsVersion={TargetRouterOsVersion}, AssessmentStatus={AssessmentStatus}, PackageCompatibility={PackageCompatibility}, UpgradePath={UpgradePath}, RouterboardAssessment={RouterboardAssessment}, MatchingPackage={MatchingPackage}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    assessment.RouterIdentity,
                    assessment.RouterOsVersionRaw,
                    assessment.Architecture,
                    assessment.TargetRouterOsVersion,
                    assessment.OverallStatus,
                    assessment.PackageCompatibility,
                    assessment.UpgradePath,
                    assessment.RouterboardAssessment,
                    assessment.MatchingPackageFileName);

                UpgradeExecutionResult upgradeResult = await upgradeExecutor
                    .ExecuteAsync(
                        connection,
                        job,
                        deviceInfo,
                        assessment,
                        credentials.Username,
                        credentials.Password,
                        () =>
                        {
                            DateTime now = timeProvider.GetUtcNow().UtcDateTime;
                            return now >= job.DateStartUtc && now < job.DateStopUtc
                                && !completer.IsImportAbortedOrStopping(job.ImportId)
                                && !cancellationToken.IsCancellationRequested;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Final upgrade outcome. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, UpgradeOutcome={UpgradeOutcome}, UpgradeDetail={UpgradeDetail}, PostUpgradeVersion={PostUpgradeVersion}, FirmwareUpgradeEnabled={FirmwareUpgradeEnabled}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    upgradeResult.Outcome,
                    upgradeResult.Detail,
                    upgradeResult.PostUpgradeVersion,
                    upgradeOptions.Value.Enabled);

                if (!upgradeResult.RebootInitiated)
                {
                    Exception? disconnectError = Disconnect(connection, job);
                    connection = null;
                    if (disconnectError is not null)
                    {
                        stopwatch.Stop();
                        logger.LogError(
                            disconnectError,
                            "Disconnection failure. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, AssessmentStatus={AssessmentStatus}, Duration={Duration}, JobOutcome={JobOutcome}",
                            job.ImportId,
                            job.JobId,
                            job.RouterName,
                            job.RouterIp,
                            assessment.OverallStatus,
                            stopwatch.Elapsed,
                            nameof(ScheduledJobTerminalState.Failed));
                        completer.CompleteDispatchedJob(
                            workItem.EntryToken,
                            ScheduledJobTerminalState.Failed,
                            stopwatch.Elapsed,
                            disconnectError);
                        return;
                    }

                    logger.LogInformation(
                        "Connection disconnected successfully. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp);
                }
                else
                {
                    connection = null;
                }

                stopwatch.Stop();
                (ScheduledJobTerminalState terminal, bool success) = MapUpgradeOutcome(upgradeResult.Outcome);
                logger.LogInformation(
                    "Final job outcome. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, RouterIdentity={RouterIdentity}, AssessmentStatus={AssessmentStatus}, UpgradeOutcome={UpgradeOutcome}, Duration={Duration}, JobOutcome={JobOutcome}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    assessment.RouterIdentity,
                    assessment.OverallStatus,
                    upgradeResult.Outcome,
                    stopwatch.Elapsed,
                    terminal);
                completer.CompleteDispatchedJob(
                    workItem.EntryToken,
                    terminal,
                    stopwatch.Elapsed,
                    success
                        ? null
                        : new InvalidOperationException(upgradeResult.Detail ?? upgradeResult.Outcome.ToString()));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (connection is not null)
                {
                    try
                    {
                        connection.Dispose();
                    }
                    catch
                    {
                    }
                }

                CompleteCancelled(workItem, completer, stopwatch.Elapsed, job);
            }
            catch (Exception ex)
            {
                if (connection is not null)
                {
                    try
                    {
                        connection.Dispose();
                    }
                    catch
                    {
                    }
                }

                stopwatch.Stop();
                logger.LogError(
                    ex,
                    "Router connection failed. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, JobOutcome={JobOutcome}",
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    nameof(ScheduledJobTerminalState.Failed));
                completer.CompleteDispatchedJob(
                    workItem.EntryToken,
                    ScheduledJobTerminalState.Failed,
                    stopwatch.Elapsed,
                    ex);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unexpected execution exception. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, JobOutcome={JobOutcome}",
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp,
                nameof(ScheduledJobTerminalState.Failed));
            completer.CompleteDispatchedJob(
                workItem.EntryToken,
                ScheduledJobTerminalState.Failed,
                stopwatch.Elapsed,
                ex);
        }
    }

    private void LogFinding(MikrotikScheduledJob job, AssessmentFinding finding)
    {
        const string template =
            "Compatibility check result. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, FindingSeverity={FindingSeverity}, FindingCode={FindingCode}, FindingMessage={FindingMessage}";

        switch (finding.Severity)
        {
            case AssessmentFindingSeverity.Blocker:
                logger.LogError(
                    template,
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    finding.Severity,
                    finding.Code,
                    finding.Message);
                break;
            case AssessmentFindingSeverity.Warning:
                logger.LogWarning(
                    template,
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    finding.Severity,
                    finding.Code,
                    finding.Message);
                break;
            default:
                logger.LogInformation(
                    template,
                    job.ImportId,
                    job.JobId,
                    job.RouterName,
                    job.RouterIp,
                    finding.Severity,
                    finding.Code,
                    finding.Message);
                break;
        }
    }

    private void SafeDispose(
        ITikConnection connection,
        RouterJobWorkItem workItem,
        IRouterJobExecutionCompleter completer,
        Stopwatch stopwatch,
        MikrotikScheduledJob job,
        bool alreadyFailed)
    {
        Exception? disconnectError = Disconnect(connection, job);
        if (disconnectError is not null)
        {
            logger.LogError(
                disconnectError,
                "Disconnection failure. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, JobOutcome={JobOutcome}",
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp,
                nameof(ScheduledJobTerminalState.Failed));
            completer.CompleteDispatchedJob(
                workItem.EntryToken,
                ScheduledJobTerminalState.Failed,
                stopwatch.Elapsed,
                disconnectError);
            return;
        }

        logger.LogInformation(
            "Connection disconnected successfully. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}",
            job.ImportId,
            job.JobId,
            job.RouterName,
            job.RouterIp);

        if (alreadyFailed)
        {
            logger.LogInformation(
                "Final job outcome. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, JobOutcome={JobOutcome}",
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp,
                nameof(ScheduledJobTerminalState.Failed));
            completer.CompleteDispatchedJob(
                workItem.EntryToken,
                ScheduledJobTerminalState.Failed,
                stopwatch.Elapsed);
        }
    }

    private Exception? Disconnect(ITikConnection connection, MikrotikScheduledJob job)
    {
        try
        {
            logger.LogInformation(
                "Connection disconnect started. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}",
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp);
            connection.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task<RouterCredentials?> ResolveCredentialsAsync(
        MikrotikScheduledJob job,
        CancellationToken cancellationToken)
    {
        try
        {
            return await credentialResolver.ResolveAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Credential resolution failed. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, JobOutcome={JobOutcome}",
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp,
                nameof(ScheduledJobTerminalState.Failed));
            return null;
        }
    }

    private void CompleteCancelled(
        RouterJobWorkItem workItem,
        IRouterJobExecutionCompleter completer,
        TimeSpan elapsed,
        MikrotikScheduledJob job)
    {
        logger.LogWarning(
            "Job cancelled. ImportId={ImportId}, JobId={JobId}, RowNumber={RowNumber}, RouterName={RouterName}, RouterIp={RouterIp}, JobOutcome={JobOutcome}",
            job.ImportId,
            job.JobId,
            job.RowNumber,
            job.RouterName,
            job.RouterIp,
            nameof(ScheduledJobTerminalState.Cancelled));
        completer.CompleteDispatchedJob(
            workItem.EntryToken,
            ScheduledJobTerminalState.Cancelled,
            elapsed);
    }

    private static (ScheduledJobTerminalState Terminal, bool Success) MapUpgradeOutcome(UpgradeOutcome outcome) =>
        outcome switch
        {
            UpgradeOutcome.SkippedUpgradeDisabled => (ScheduledJobTerminalState.Succeeded, true),
            UpgradeOutcome.SkippedAlreadyAtTarget => (ScheduledJobTerminalState.Succeeded, true),
            UpgradeOutcome.NotAttempted => (ScheduledJobTerminalState.Succeeded, true),
            UpgradeOutcome.UpgradeSucceeded => (ScheduledJobTerminalState.Succeeded, true),
            UpgradeOutcome.UpgradeInterrupted => (ScheduledJobTerminalState.Cancelled, false),
            _ => (ScheduledJobTerminalState.Failed, false),
        };
}
