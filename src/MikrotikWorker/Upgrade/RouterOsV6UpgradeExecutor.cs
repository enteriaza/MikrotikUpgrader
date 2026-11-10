using System.Net;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;

namespace MikrotikWorker.Upgrade;

internal interface IRouterOsV6UpgradeExecutor
{
    Task<UpgradeExecutionResult> ExecuteAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        DeviceCompatibilityAssessment assessment,
        string username,
        string password,
        Func<bool> isWithinScheduleWindow,
        CancellationToken cancellationToken);
}

internal sealed class RouterOsV6UpgradeExecutor(
    IMikrotikRouterOperations routerOperations,
    IMikrotikConnectionFactory connectionFactory,
    IUpgradePackageResolver packageResolver,
    IDeviceBackupService backupService,
    IOptions<FirmwareUpgradeOptions> upgradeOptions,
    IOptions<MikroTikOptions> mikroTikOptions,
    TimeProvider timeProvider,
    ILogger<RouterOsV6UpgradeExecutor> logger) : IRouterOsV6UpgradeExecutor
{
    public async Task<UpgradeExecutionResult> ExecuteAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        DeviceCompatibilityAssessment assessment,
        string username,
        string password,
        Func<bool> isWithinScheduleWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(isWithinScheduleWindow);

        FirmwareUpgradeOptions options = upgradeOptions.Value;
        RouterOsVersion target = RouterOsVersion.Parse(options.TargetRouterOsVersion);

        if (!options.Enabled)
        {
            logger.LogInformation(
                "Upgrade skipped because FirmwareUpgrade:Enabled is false. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
                job.ImportId,
                job.JobId,
                job.RouterIp);
            return new UpgradeExecutionResult { Outcome = UpgradeOutcome.SkippedUpgradeDisabled };
        }

        if (assessment.OverallStatus == DeviceAssessmentStatus.AlreadyAtTarget)
        {
            return new UpgradeExecutionResult { Outcome = UpgradeOutcome.SkippedAlreadyAtTarget };
        }

        if (assessment.OverallStatus == DeviceAssessmentStatus.Unsupported
            || assessment.RouterOsVersionClassification == RouterOsVersionClassification.RouterOsV7OrLater)
        {
            return PolicyBlock("RouterOS v7 devices are outside the v6 upgrade milestone.");
        }

        if (assessment.RouterOsVersionClassification == RouterOsVersionClassification.NewerThanTargetV6)
        {
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.ManualReviewRequired,
                Detail = "Device RouterOS v6 version is newer than the configured target; automatic downgrade is not performed.",
            };
        }

        if (assessment.OverallStatus != DeviceAssessmentStatus.ReadyForUpgrade)
        {
            return PolicyBlock($"Device assessment status '{assessment.OverallStatus}' is not eligible for automated upgrade.");
        }

        if (!isWithinScheduleWindow())
        {
            return PolicyBlock("Job is outside its scheduled execution window.");
        }

        logger.LogInformation(
            "Upgrade policy decision. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RouterOsVersion={RouterOsVersion}, TargetRouterOsVersion={TargetRouterOsVersion}, Decision=Proceed",
            job.ImportId,
            job.JobId,
            job.RouterIp,
            deviceInfo.Version,
            target);

        IReadOnlyList<InstalledPackageInfo> installed = await routerOperations
            .ListInstalledPackagesAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Installed package inventory retrieved. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, PackageCount={PackageCount}, Packages={Packages}",
            job.ImportId,
            job.JobId,
            job.RouterIp,
            installed.Count,
            string.Join(", ", installed.Select(p => $"{p.Name}/{p.Version}")));

        UpgradePackageResolution resolution = packageResolver.Resolve(
            deviceInfo.ArchitectureName,
            installed,
            target);
        if (!resolution.Succeeded)
        {
            logger.LogError(
                "Package resolution failed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, Reason={Reason}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                resolution.FailureReason);
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.PackageResolutionFailed,
                Detail = resolution.FailureReason,
            };
        }

        logger.LogInformation(
            "Required package inventory resolved. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, SelectedPackages={SelectedPackages}",
            job.ImportId,
            job.JobId,
            job.RouterIp,
            string.Join(", ", resolution.Packages.Select(p => $"{p.CatalogPackage.FileName}:{p.CatalogPackage.SizeBytes}")));

        // Mandatory backup gate — no fetch/reboot before this succeeds.
        DeviceBackupResult backup = await backupService
            .CreatePreUpgradeBackupAsync(connection, job, deviceInfo, username, password, cancellationToken)
            .ConfigureAwait(false);
        if (!backup.Succeeded)
        {
            logger.LogError(
                "Backup prerequisite failed; firmware fetch will not start. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, Reason={Reason}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                backup.FailureReason);
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.BackupFailed,
                Detail = backup.FailureReason,
            };
        }

        logger.LogInformation(
            "Backup prerequisite passed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
            job.ImportId,
            job.JobId,
            job.RouterIp);

        if (!isWithinScheduleWindow())
        {
            return PolicyBlock("Job left its scheduled window after backup; firmware fetch aborted.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string baseUrl = options.AdvertisedBaseUrl.TrimEnd('/');
        logger.LogInformation(
            "Firmware server URL selected. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, AdvertisedBaseUrl={AdvertisedBaseUrl}",
            job.ImportId,
            job.JobId,
            job.RouterIp,
            baseUrl);

        foreach (ResolvedUpgradePackage package in resolution.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isWithinScheduleWindow())
            {
                return PolicyBlock("Job left its scheduled window during package download.");
            }

            string url = $"{baseUrl}/{package.RelativeUrlPath}";
            string dst = package.CatalogPackage.FileName;
            logger.LogInformation(
                "Fetch command initiation. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, PackageFile={PackageFile}, Url={Url}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                dst,
                url);

            try
            {
                await routerOperations
                    .FetchUrlToFileAsync(connection, url, dst, options.FetchTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeInterrupted,
                    Detail = "Cancelled during package download.",
                };
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Fetch failed. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, PackageFile={PackageFile}",
                    job.ImportId,
                    job.JobId,
                    job.RouterIp,
                    dst);
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail = $"Fetch failed for '{dst}': {ex.Message}",
                };
            }

            RouterFileInfo? fetched = await routerOperations
                .FindFileAsync(connection, dst, cancellationToken)
                .ConfigureAwait(false);
            if (fetched is null)
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail = $"Downloaded file '{dst}' is missing on the router.",
                };
            }

            if (fetched.SizeBytes <= 0)
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail = $"Downloaded file '{dst}' is empty.",
                };
            }

            if (fetched.SizeBytes != package.CatalogPackage.SizeBytes)
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail =
                        $"Downloaded file '{dst}' size mismatch (router={fetched.SizeBytes}, expected={package.CatalogPackage.SizeBytes}).",
                };
            }

            logger.LogInformation(
                "Download verification succeeded. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, PackageFile={PackageFile}, SizeBytes={SizeBytes}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                dst,
                fetched.SizeBytes);
        }

        logger.LogInformation(
            "Package staging completion. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, PackageCount={PackageCount}",
            job.ImportId,
            job.JobId,
            job.RouterIp,
            resolution.Packages.Count);

        if (!isWithinScheduleWindow())
        {
            return PolicyBlock("Job left its scheduled window before reboot; installation aborted.");
        }

        logger.LogInformation(
            "Installation/reboot initiation. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
            job.ImportId,
            job.JobId,
            job.RouterIp);

        await routerOperations.RebootAsync(connection, cancellationToken).ConfigureAwait(false);
        bool rebootInitiated = true;
        logger.LogInformation(
            "Device unavailable during reboot expected. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
            job.ImportId,
            job.JobId,
            job.RouterIp);

        MikrotikConnectionOptions reconnectOptions = new()
        {
            Host = job.RouterIp.ToString(),
            Username = username,
            Password = password,
            Port = mikroTikOptions.Value.Port,
        };

        DateTimeOffset rebootDeadline = timeProvider.GetUtcNow() + options.RebootTimeout;
        ITikConnection? reconnected = null;
        try
        {
            while (timeProvider.GetUtcNow() <= rebootDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogInformation(
                    "Reconnection attempt. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
                    job.ImportId,
                    job.JobId,
                    job.RouterIp);
                try
                {
                    reconnected = await connectionFactory
                        .CreateAsync(reconnectOptions, cancellationToken)
                        .ConfigureAwait(false);
                    logger.LogInformation(
                        "Reconnection success. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
                        job.ImportId,
                        job.JobId,
                        job.RouterIp);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.UpgradeInterrupted,
                        Detail = "Cancelled while waiting for reboot reconnect.",
                        RebootInitiated = rebootInitiated,
                    };
                }
                catch
                {
                    await Task.Delay(options.ReconnectInterval, cancellationToken).ConfigureAwait(false);
                }
            }

            if (reconnected is null)
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeTimedOut,
                    Detail = $"Router did not become reachable within {options.RebootTimeout}.",
                    RebootInitiated = rebootInitiated,
                };
            }

            using ITikConnection live = reconnected;
            reconnected = null;

            DateTimeOffset verifyDeadline = timeProvider.GetUtcNow() + options.PostUpgradeVerifyTimeout;
            MikrotikDeviceInfo postInfo;
            IReadOnlyList<InstalledPackageInfo> postPackages;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                postInfo = await routerOperations
                    .ReadDeviceInfoAsync(live, job.RouterIp.ToString(), cancellationToken)
                    .ConfigureAwait(false);
                postPackages = await routerOperations
                    .ListInstalledPackagesAsync(live, cancellationToken)
                    .ConfigureAwait(false);

                if (RouterOsVersion.TryParse(postInfo.Version, out RouterOsVersion postVersion)
                    && postVersion == target)
                {
                    break;
                }

                if (timeProvider.GetUtcNow() > verifyDeadline)
                {
                    break;
                }

                await Task.Delay(options.ReconnectInterval, cancellationToken).ConfigureAwait(false);
            }
            while (true);

            logger.LogInformation(
                "Post-upgrade RouterOS version. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, RouterOsVersion={RouterOsVersion}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                postInfo.Version);
            logger.LogInformation(
                "Post-upgrade installed package inventory. ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}, Packages={Packages}",
                job.ImportId,
                job.JobId,
                job.RouterIp,
                string.Join(", ", postPackages.Select(p => $"{p.Name}/{p.Version}")));

            if (!RouterOsVersion.TryParse(postInfo.Version, out RouterOsVersion verified)
                || verified != target)
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeVersionMismatch,
                    Detail = $"Expected RouterOS {target}, observed '{postInfo.Version}'.",
                    PostUpgradeVersion = postInfo.Version,
                    RebootInitiated = rebootInitiated,
                };
            }

            string? expectedArch = FirmwarePackageFileNameParser.NormalizeArchitecture(
                deviceInfo.ArchitectureName ?? string.Empty);
            string? actualArch = string.IsNullOrWhiteSpace(postInfo.ArchitectureName)
                ? null
                : FirmwarePackageFileNameParser.NormalizeArchitecture(postInfo.ArchitectureName);
            if (!string.Equals(expectedArch, actualArch, StringComparison.Ordinal))
            {
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail = $"Architecture changed after upgrade (before={expectedArch}, after={actualArch}).",
                    PostUpgradeVersion = postInfo.Version,
                    RebootInitiated = rebootInitiated,
                };
            }

            foreach (ResolvedUpgradePackage required in resolution.Packages)
            {
                InstalledPackageInfo? found = postPackages.FirstOrDefault(p =>
                    p.Name.Equals(required.InstalledPackageName, StringComparison.OrdinalIgnoreCase));
                if (found is null
                    || !RouterOsVersion.TryParse(found.Version, out RouterOsVersion pkgVersion)
                    || pkgVersion != target)
                {
                    return new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.UpgradePackageMismatch,
                        Detail =
                            $"Installed package '{required.InstalledPackageName}' was missing or not at {target} after upgrade.",
                        PostUpgradeVersion = postInfo.Version,
                        RebootInitiated = rebootInitiated,
                    };
                }
            }

            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.UpgradeSucceeded,
                PostUpgradeVersion = postInfo.Version,
                RebootInitiated = rebootInitiated,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.UpgradeInterrupted,
                Detail = "Cancelled after reboot was initiated.",
                RebootInitiated = rebootInitiated,
            };
        }
        finally
        {
            reconnected?.Dispose();
        }
    }

    private UpgradeExecutionResult PolicyBlock(string detail)
    {
        logger.LogWarning("Upgrade policy blocked device. Detail={Detail}", detail);
        return new UpgradeExecutionResult
        {
            Outcome = UpgradeOutcome.PolicyBlocked,
            Detail = detail,
        };
    }
}
