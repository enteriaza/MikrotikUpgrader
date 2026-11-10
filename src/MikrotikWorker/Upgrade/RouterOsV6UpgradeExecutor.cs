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
            logger.LogInformation("Upgrade skipped because FirmwareUpgrade:Enabled is false");
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
            "Upgrade policy decision: proceed (RouterOS {RouterOsVersion} → {TargetRouterOsVersion})",
            deviceInfo.Version,
            target);
        logger.LogInformation(
            "RouterBOARD firmware before upgrade: model={RouterboardModel}, type={FirmwareType}, current={CurrentFirmware}, upgrade={UpgradeFirmware}, factory={FactoryFirmware}",
            deviceInfo.RouterboardModel,
            deviceInfo.RouterboardFirmwareType,
            deviceInfo.RouterboardCurrentFirmware,
            deviceInfo.RouterboardUpgradeFirmware,
            deviceInfo.RouterboardFactoryFirmware);

        IReadOnlyList<InstalledPackageInfo> installed = await routerOperations
            .ListInstalledPackagesAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        PackageInventorySummary inventory = PackageInventoryReporting.Summarize(installed);
        logger.LogInformation(
            "Installed package count: {IndependentCount}",
            inventory.IndependentCount);
        logger.LogInformation(
            "Installed packages: {Packages}",
            PackageInventoryReporting.FormatIndependentPackages(inventory.IndependentPackages));
        logger.LogInformation(
            "Bundled components: {BundledCount}",
            inventory.BundledCount);
        logger.LogDebug(
            "Bundled component inventory: {BundledPackages}",
            PackageInventoryReporting.FormatIndependentPackages(inventory.BundledComponents));

        UpgradePackageResolution resolution = packageResolver.Resolve(
            deviceInfo.ArchitectureName,
            installed,
            target);
        if (!resolution.Succeeded)
        {
            logger.LogError("Package resolution failed: {Reason}", resolution.FailureReason);
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.PackageResolutionFailed,
                Detail = resolution.FailureReason,
            };
        }

        logger.LogInformation(
            "Required packages resolved: {SelectedPackages}",
            string.Join(", ", resolution.Packages.Select(p => $"{p.CatalogPackage.FileName}:{p.CatalogPackage.SizeBytes}")));

        // Mandatory backup gate — no fetch/reboot before this succeeds.
        DeviceBackupResult backup = await backupService
            .CreatePreUpgradeBackupAsync(connection, job, deviceInfo, username, password, cancellationToken)
            .ConfigureAwait(false);
        if (!backup.Succeeded)
        {
            logger.LogError("Backup prerequisite failed; firmware fetch will not start: {Reason}", backup.FailureReason);
            return new UpgradeExecutionResult
            {
                Outcome = UpgradeOutcome.BackupFailed,
                Detail = backup.FailureReason,
            };
        }

        logger.LogInformation("Backup prerequisite passed");

        if (!isWithinScheduleWindow())
        {
            return PolicyBlock("Job left its scheduled window after backup; firmware fetch aborted.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string baseUrl = options.AdvertisedBaseUrl.TrimEnd('/');
        logger.LogInformation("Firmware server URL selected: {AdvertisedBaseUrl}", baseUrl);

        foreach (ResolvedUpgradePackage package in resolution.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isWithinScheduleWindow())
            {
                return PolicyBlock("Job left its scheduled window during package download.");
            }

            string url = $"{baseUrl}/{package.RelativeUrlPath}";
            string dst = package.CatalogPackage.FileName;
            logger.LogInformation("Fetching firmware package {PackageFile}", dst);

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
                logger.LogError(ex, "Fetch failed for {PackageFile}", dst);
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail = $"Fetch failed for '{dst}': {ex.Message}",
                };
            }

            // RouterOS can report an intermediate size briefly after /tool/fetch finishes.
            RouterFileInfo? fetched = await WaitForFetchedPackageSizeAsync(
                    connection,
                    dst,
                    package.CatalogPackage.SizeBytes,
                    options.BackupFileReadyTimeout,
                    options.BackupFileReadyPollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fetched is null)
            {
                RouterFileInfo? current = await routerOperations
                    .FindFileAsync(connection, dst, cancellationToken)
                    .ConfigureAwait(false);
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeFailed,
                    Detail = current is null
                        ? $"Downloaded file '{dst}' is missing on the router."
                        : current.SizeBytes <= 0
                            ? $"Downloaded file '{dst}' is empty."
                            : $"Downloaded file '{dst}' size mismatch (router={current.SizeBytes}, expected={package.CatalogPackage.SizeBytes}).",
                };
            }

            logger.LogInformation(
                "Staged package verified: {PackageFile} ({SizeBytes} bytes)",
                dst,
                fetched.SizeBytes);
        }

        logger.LogInformation("Package staging complete ({PackageCount} package(s))", resolution.Packages.Count);

        if (!isWithinScheduleWindow())
        {
            return PolicyBlock("Job left its scheduled window before reboot; installation aborted.");
        }

        logger.LogInformation("Initiating reboot for package installation");
        await routerOperations.RebootAsync(connection, cancellationToken).ConfigureAwait(false);
        bool rebootInitiated = true;
        DateTimeOffset rebootInitiatedAt = timeProvider.GetUtcNow();
        DateTimeOffset rebootDeadline = rebootInitiatedAt + options.RebootTimeout;
        logger.LogInformation(
            "Reboot initiated; reconnection deadline {Deadline:yyyy-MM-dd HH:mm:ss} UTC ({Timeout})",
            rebootDeadline.UtcDateTime,
            options.RebootTimeout);

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
                TimeSpan elapsed = now - rebootInitiatedAt;
                logger.LogInformation(
                    "Reconnection attempt {Attempt} (elapsed {Elapsed}, remaining {Remaining})",
                    attempt,
                    FormatDuration(elapsed),
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
                            // Attempt already finished and disposed the CTS.
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
                        "Reconnected successfully after {Elapsed}",
                        FormatDuration(timeProvider.GetUtcNow() - rebootInitiatedAt));
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
                catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
                {
                    // Absolute deadline consumed by the connection attempt itself.
                    break;
                }
                catch
                {
                    now = timeProvider.GetUtcNow();
                    remaining = rebootDeadline - now;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    TimeSpan delay = options.ReconnectInterval < remaining
                        ? options.ReconnectInterval
                        : remaining;
                    try
                    {
                        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
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
                }
            }

            if (reconnected is null)
            {
                TimeSpan waited = timeProvider.GetUtcNow() - rebootInitiatedAt;
                logger.LogError(
                    "Reconnection timed out after {Elapsed} (limit {Timeout})",
                    FormatDuration(waited),
                    options.RebootTimeout);
                return new UpgradeExecutionResult
                {
                    Outcome = UpgradeOutcome.UpgradeTimedOut,
                    Detail = $"Router did not become reachable within {options.RebootTimeout}.",
                    RebootInitiated = rebootInitiated,
                };
            }

            using ITikConnection live = reconnected;
            reconnected = null;

            // Absolute post-reboot ceiling remains rebootDeadline. PostUpgradeVerifyTimeout may only
            // shorten the poll window; it must never extend past the five-minute reboot window.
            DateTimeOffset verifyPollDeadline = timeProvider.GetUtcNow() + options.PostUpgradeVerifyTimeout;
            if (verifyPollDeadline > rebootDeadline)
            {
                verifyPollDeadline = rebootDeadline;
            }

            MikrotikDeviceInfo? postInfo = null;
            IReadOnlyList<InstalledPackageInfo>? postPackages = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan remaining = rebootDeadline - timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    return TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout);
                }

                using CancellationTokenSource verifyCts = new();
                using ITimer verifyTimer = timeProvider.CreateTimer(
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
                    verifyCts,
                    remaining,
                    Timeout.InfiniteTimeSpan);
                using CancellationTokenSource linkedVerifyCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    verifyCts.Token);

                try
                {
                    postInfo = await routerOperations
                        .ReadDeviceInfoAsync(live, job.RouterIp.ToString(), linkedVerifyCts.Token)
                        .ConfigureAwait(false);
                    postPackages = await routerOperations
                        .ListInstalledPackagesAsync(live, linkedVerifyCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.UpgradeInterrupted,
                        Detail = "Cancelled during post-reboot verification.",
                        RebootInitiated = rebootInitiated,
                    };
                }
                catch (OperationCanceledException) when (verifyCts.IsCancellationRequested)
                {
                    return TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout);
                }

                if (RouterOsVersion.TryParse(postInfo.Version, out RouterOsVersion postVersion)
                    && postVersion == target)
                {
                    break;
                }

                if (timeProvider.GetUtcNow() >= verifyPollDeadline
                    || timeProvider.GetUtcNow() >= rebootDeadline)
                {
                    break;
                }

                remaining = rebootDeadline - timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    return TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout);
                }

                TimeSpan delay = options.ReconnectInterval < remaining
                    ? options.ReconnectInterval
                    : remaining;
                try
                {
                    await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new UpgradeExecutionResult
                    {
                        Outcome = UpgradeOutcome.UpgradeInterrupted,
                        Detail = "Cancelled during post-reboot verification.",
                        RebootInitiated = rebootInitiated,
                    };
                }
            }

            if (postInfo is null || postPackages is null)
            {
                return TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout);
            }

            bool deadlineReached = timeProvider.GetUtcNow() >= rebootDeadline;
            bool versionMatches = RouterOsVersion.TryParse(postInfo.Version, out RouterOsVersion verified)
                && verified == target;

            // If the absolute window elapsed before a matching version was observed, that is a timeout —
            // not a version-mismatch classification.
            if (deadlineReached && !versionMatches)
            {
                return TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout);
            }

            logger.LogInformation("Post-upgrade RouterOS version: {RouterOsVersion}", postInfo.Version);

            PackageInventorySummary postInventory = PackageInventoryReporting.Summarize(postPackages);
            logger.LogInformation(
                "Post-upgrade installed package count: {IndependentCount}",
                postInventory.IndependentCount);
            logger.LogInformation(
                "Post-upgrade installed packages: {Packages}",
                PackageInventoryReporting.FormatIndependentPackages(postInventory.IndependentPackages));
            logger.LogInformation(
                "Post-upgrade bundled components: {BundledCount}",
                postInventory.BundledCount);
            logger.LogInformation(
                "RouterBOARD firmware after upgrade: model={RouterboardModel}, type={FirmwareType}, current={CurrentFirmware}, upgrade={UpgradeFirmware}, factory={FactoryFirmware}",
                postInfo.RouterboardModel,
                postInfo.RouterboardFirmwareType,
                postInfo.RouterboardCurrentFirmware,
                postInfo.RouterboardUpgradeFirmware,
                postInfo.RouterboardFactoryFirmware);

            if (timeProvider.GetUtcNow() > rebootDeadline)
            {
                return TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout);
            }

            if (!versionMatches)
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

            if (timeProvider.GetUtcNow() > rebootDeadline)
            {
                return TimedOutAfterReboot(rebootInitiated, rebootInitiatedAt, options.RebootTimeout);
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

    private async Task<RouterFileInfo?> WaitForFetchedPackageSizeAsync(
        ITikConnection connection,
        string fileName,
        long expectedSizeBytes,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow() + timeout;
        while (timeProvider.GetUtcNow() <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RouterFileInfo? file = await routerOperations
                .FindFileAsync(connection, fileName, cancellationToken)
                .ConfigureAwait(false);
            if (file is not null && file.SizeBytes == expectedSizeBytes)
            {
                return file;
            }

            await Task.Delay(pollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private UpgradeExecutionResult PolicyBlock(string detail)
    {
        logger.LogWarning("Upgrade policy blocked device: {Detail}", detail);
        return new UpgradeExecutionResult
        {
            Outcome = UpgradeOutcome.PolicyBlocked,
            Detail = detail,
        };
    }

    private UpgradeExecutionResult TimedOutAfterReboot(
        bool rebootInitiated,
        DateTimeOffset rebootInitiatedAt,
        TimeSpan rebootTimeout)
    {
        TimeSpan waited = timeProvider.GetUtcNow() - rebootInitiatedAt;
        logger.LogError(
            "Post-reboot upgrade window timed out after {Elapsed} (limit {Timeout})",
            FormatDuration(waited),
            rebootTimeout);
        return new UpgradeExecutionResult
        {
            Outcome = UpgradeOutcome.UpgradeTimedOut,
            Detail = $"Router did not complete post-reboot verification within {rebootTimeout}.",
            RebootInitiated = rebootInitiated,
        };
    }

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
