using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MikrotikWorker.MikroTik;
using tik4net;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Connects with a short-lived API session and issues only identity/resource/package/(optional)routerboard print commands.
/// Structurally independent from <see cref="MikrotikApiClient"/> mutation methods and upgrade services.
/// All RouterOS commands are issued exclusively through <see cref="ReadOnlyTikCommandExecutor"/>.
/// </summary>
internal sealed class ReadOnlyMikrotikInventoryCollector(
    IMikrotikConnectionFactory connectionFactory,
    ILogger<ReadOnlyMikrotikInventoryCollector> logger) : IReadOnlyMikrotikInventoryCollector
{
    public async Task<ReadOnlyInventoryCollectionResult> CollectAsync(
        string routerIp,
        string username,
        string password,
        int port,
        TimeSpan connectionTimeout,
        string? routerName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routerIp);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        if (connectionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout), connectionTimeout, "Must be greater than zero.");
        }

        Stopwatch sw = Stopwatch.StartNew();
        ReadOnlyTikCommandExecutor executor = new();
        LiveIdentityCollectionStatus identityStatus = LiveIdentityCollectionStatus.NotAttempted;
        string? identity = null;
        string? version = null;
        string? architecture = null;
        List<InstalledPackageInfo> packages = [];
        int unparsablePackageRows = 0;
        bool packagePrintSucceeded = false;
        bool resourcePrintSucceeded = false;

        MikrotikConnectionOptions connectionOptions = new()
        {
            Host = routerIp,
            Port = port,
            Username = username,
            Password = password,
            ConnectTimeout = connectionTimeout,
        };

        try
        {
            using CancellationTokenSource timeoutCts = new(connectionTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);

            using ITikConnection connection = await connectionFactory
                .CreateAsync(connectionOptions, linked.Token)
                .ConfigureAwait(false);

            try
            {
                identity = await executor
                    .ExecuteIdentityPrintAsync(connection, linked.Token)
                    .ConfigureAwait(false);
                identityStatus = string.IsNullOrWhiteSpace(identity)
                    ? LiveIdentityCollectionStatus.Unavailable
                    : LiveIdentityCollectionStatus.Succeeded;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                identityStatus = LiveIdentityCollectionStatus.Failed;
                logger.LogWarning(
                    "Identity print failed for {RouterIp} ({ExceptionType}); continuing with remaining inventory reads",
                    routerIp,
                    ex.GetType().Name);
            }

            try
            {
                ITikReSentence resource = await executor
                    .ExecuteResourcePrintAsync(connection, linked.Token)
                    .ConfigureAwait(false);
                version = NullIfWhiteSpace(resource.GetResponseFieldOrDefault("version", null));
                architecture = NullIfWhiteSpace(resource.GetResponseFieldOrDefault("architecture-name", null));
                resourcePrintSucceeded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Resource print failed for {RouterIp} ({ExceptionType})",
                    routerIp,
                    ex.GetType().Name);
                sw.Stop();
                return BuildResult(
                    routerIp,
                    routerName,
                    LiveInventoryCollectionStatus.Partial,
                    identityStatus,
                    identity,
                    version,
                    architecture,
                    packages,
                    "RouterOS resource print failed.",
                    sw.Elapsed,
                    executor.CommandsIssued);
            }

            try
            {
                IReadOnlyList<ITikReSentence> rows = await executor
                    .ExecutePackagePrintAsync(connection, linked.Token)
                    .ConfigureAwait(false);
                (packages, unparsablePackageRows) = ParsePackages(rows);
                packagePrintSucceeded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Package print failed for {RouterIp} ({ExceptionType})",
                    routerIp,
                    ex.GetType().Name);
                sw.Stop();
                return BuildResult(
                    routerIp,
                    routerName,
                    LiveInventoryCollectionStatus.Partial,
                    identityStatus,
                    identity,
                    version,
                    architecture,
                    packages,
                    "RouterOS package inventory print failed.",
                    sw.Elapsed,
                    executor.CommandsIssued);
            }

            // Optional; failure does not degrade inventory completeness for catalogue compatibility.
            try
            {
                _ = await executor
                    .ExecuteRouterboardPrintAsync(connection, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // RouterBOARD print is optional for catalogue compatibility.
            }

            sw.Stop();

            bool inventoryIncomplete = !resourcePrintSucceeded
                || string.IsNullOrWhiteSpace(version)
                || string.IsNullOrWhiteSpace(architecture)
                || !packagePrintSucceeded
                || packages.Count == 0
                || unparsablePackageRows > 0;

            // Identity unavailability/failure is tracked separately and does not make inventory incomplete.
            string? detail = null;
            if (unparsablePackageRows > 0)
            {
                detail =
                    $"Package inventory contained {unparsablePackageRows} unparsable row(s); inventory marked incomplete.";
            }
            else if (inventoryIncomplete)
            {
                detail = "One or more required inventory fields were unavailable.";
            }

            return BuildResult(
                routerIp,
                routerName,
                inventoryIncomplete
                    ? LiveInventoryCollectionStatus.Partial
                    : LiveInventoryCollectionStatus.Succeeded,
                identityStatus,
                identity,
                version,
                architecture,
                packages,
                detail,
                sw.Elapsed,
                executor.CommandsIssued);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return BuildResult(
                routerIp,
                routerName,
                LiveInventoryCollectionStatus.Cancelled,
                identityStatus,
                identity,
                version,
                architecture,
                packages,
                "Audit cancelled.",
                sw.Elapsed,
                executor.CommandsIssued);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return BuildResult(
                routerIp,
                routerName,
                LiveInventoryCollectionStatus.TimedOut,
                identityStatus,
                identity,
                version,
                architecture,
                packages,
                "Connection or inventory read timed out.",
                sw.Elapsed,
                executor.CommandsIssued);
        }
        catch (Exception ex) when (IsAuthenticationFailure(ex))
        {
            sw.Stop();
            logger.LogWarning(
                "Authentication failed for live audit of {RouterIp} ({ExceptionType})",
                routerIp,
                ex.GetType().Name);
            return BuildResult(
                routerIp,
                routerName,
                LiveInventoryCollectionStatus.AuthenticationFailed,
                identityStatus,
                identity,
                version,
                architecture,
                packages,
                "Authentication failed.",
                sw.Elapsed,
                executor.CommandsIssued);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(
                "Connection failed for live audit of {RouterIp} ({ExceptionType})",
                routerIp,
                ex.GetType().Name);
            return BuildResult(
                routerIp,
                routerName,
                LiveInventoryCollectionStatus.ConnectionFailed,
                identityStatus,
                identity,
                version,
                architecture,
                packages,
                "Connection failed.",
                sw.Elapsed,
                executor.CommandsIssued);
        }
    }

    internal static (List<InstalledPackageInfo> Packages, int UnparsableRows) ParsePackages(
        IEnumerable<ITikReSentence> rows)
    {
        List<InstalledPackageInfo> packages = [];
        int unparsable = 0;
        foreach (ITikReSentence row in rows)
        {
            string name = row.GetResponseFieldOrDefault("name", string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                unparsable++;
                continue;
            }

            string version = row.GetResponseFieldOrDefault("version", string.Empty) ?? string.Empty;
            string disabledRaw = row.GetResponseFieldOrDefault("disabled", "false") ?? "false";
            bool disabled = disabledRaw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || disabledRaw.Equals("yes", StringComparison.OrdinalIgnoreCase);
            // Bundle is authoritative; never infer from package names.
            string? bundle = NullIfWhiteSpace(row.GetResponseFieldOrDefault("bundle", null));
            packages.Add(new InstalledPackageInfo(name.Trim(), version.Trim(), disabled, bundle));
        }

        return (packages, unparsable);
    }

    /// <summary>
    /// Prefer tik4net's login exception type; fall back to conservative known message fragments.
    /// Broad "authentication" substring matching is intentionally avoided.
    /// </summary>
    internal static bool IsAuthenticationFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is TikConnectionLoginException)
            {
                return true;
            }

            string message = current.Message ?? string.Empty;
            if (message.Contains("invalid user name or password", StringComparison.OrdinalIgnoreCase)
                || message.Contains("cannot log in", StringComparison.OrdinalIgnoreCase)
                || message.Equals("login failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlyInventoryCollectionResult BuildResult(
        string routerIp,
        string? routerName,
        LiveInventoryCollectionStatus status,
        LiveIdentityCollectionStatus identityStatus,
        string? identity,
        string? version,
        string? architecture,
        IReadOnlyList<InstalledPackageInfo> packages,
        string? detail,
        TimeSpan duration,
        IReadOnlyList<string> commands) =>
        new()
        {
            RouterIp = routerIp,
            RouterName = routerName,
            CollectionStatus = status,
            IdentityStatus = identityStatus,
            Identity = NullIfWhiteSpace(identity),
            RouterOsVersion = version,
            Architecture = architecture,
            Packages = packages,
            FailureDetail = detail,
            Duration = duration,
            CommandsIssued = commands.ToList(),
        };

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
