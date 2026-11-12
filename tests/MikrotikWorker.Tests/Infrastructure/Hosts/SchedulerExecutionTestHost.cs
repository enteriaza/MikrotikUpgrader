// -----------------------------------------------------------------------------
// File: SchedulerExecutionTestHost.cs
//
// Description:
// Test host wiring for scheduler execution scenarios with controllable fakes and
// in-memory job scheduling.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests.Infrastructure.Hosts;
/// <summary>
/// Wires scheduler + bounded router execution workers for unit tests.
/// </summary>
internal static class SchedulerExecutionTestHost
{
    /// <summary>
    /// Builds an <see cref="InMemoryJobScheduler"/>, its execution queue and a pool of worker loops that run
    /// <see cref="RouterJobExecutor"/> against the supplied fakes.
    /// </summary>
    /// <param name="options">Scheduled-jobs options; <c>MaxConcurrentRouterJobs</c> is raised to at least 12.</param>
    /// <param name="paths">Job folder layout.</param>
    /// <param name="fileMover">File mover used for Accepted/Completed moves.</param>
    /// <param name="time">Time provider shared by scheduler and executor.</param>
    /// <param name="schedulerLogger">Scheduler logger; defaults to a null logger.</param>
    /// <param name="connectionFactory">Connection factory; defaults to one that throws on any connect.</param>
    /// <param name="deviceInfoReader">Device-info reader; defaults to a real client over <paramref name="connectionFactory"/>.</param>
    /// <param name="credentialResolver">Credential resolver; defaults to app-settings credentials.</param>
    /// <param name="compatibilityAssessor">Assessor; defaults to one over a temp firmware catalogue and RouterOS 7.23.8.</param>
    /// <param name="upgradeExecutor">Upgrade executor; defaults to <see cref="StubUpgradeExecutor"/>.</param>
    /// <param name="mikroTikEnabled">Value forced onto <c>MikroTikOptions.Enabled</c>.</param>
    /// <param name="executionWorkers">Requested number of worker loops (minimum 12 are used).</param>
    /// <param name="mikroTikOptions">MikroTik options override; defaults to shared test credentials.</param>
    /// <param name="executorLogger">Executor logger; defaults to a null logger.</param>
    /// <param name="firmwareRoot">Firmware directory for the default assessor; defaults to a temp tree.</param>
    /// <param name="jobRetryOptions">Retry options; defaults to <see cref="JobRetryOptions"/> defaults.</param>
    /// <returns>
    /// The scheduler, its queue, the task running the workers, and the source that cancels those workers.
    /// </returns>
    public static (
        InMemoryJobScheduler Scheduler,
        RouterJobExecutionQueue Queue,
        Task ExecutionTask,
        CancellationTokenSource ExecutionCts) Create(
        ScheduledJobsOptions options,
        ScheduledJobsPaths paths,
        IScheduledJobFileMover fileMover,
        TimeProvider time,
        ILogger<InMemoryJobScheduler>? schedulerLogger = null,
        IMikrotikConnectionFactory? connectionFactory = null,
        IMikrotikDeviceInfoReader? deviceInfoReader = null,
        IRouterCredentialResolver? credentialResolver = null,
        IDeviceCompatibilityAssessor? compatibilityAssessor = null,
        IRouterOsV6UpgradeExecutor? upgradeExecutor = null,
        bool mikroTikEnabled = false,
        int executionWorkers = 12,
        MikroTikOptions? mikroTikOptions = null,
        ILogger<RouterJobExecutor>? executorLogger = null,
        string? firmwareRoot = null,
        JobRetryOptions? jobRetryOptions = null)
    {
        options.MaxConcurrentRouterJobs = Math.Max(12, executionWorkers);
        RouterJobExecutionQueue queue = new(Options.Create(options));
        InMemoryJobScheduler scheduler = new(
            Options.Create(options),
            paths,
            fileMover,
            queue,
            time,
            schedulerLogger ?? NullLogger<InMemoryJobScheduler>.Instance,
            Options.Create(jobRetryOptions ?? new JobRetryOptions()));

        MikroTikOptions mikroTik = mikroTikOptions ?? new MikroTikOptions
        {
            Enabled = mikroTikEnabled,
            Username = "shared-user",
            Password = "shared-pass",
            Port = 8728,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            ReconnectInterval = TimeSpan.FromSeconds(30),
        };
        mikroTik.Enabled = mikroTikEnabled;

        IMikrotikConnectionFactory connections = connectionFactory
            ?? new ThrowingMikrotikConnectionFactory(new InvalidOperationException("Unexpected connection."));
        IRouterCredentialResolver credentials = credentialResolver
            ?? new AppSettingsRouterCredentialResolver(Options.Create(mikroTik));
        IMikrotikDeviceInfoReader reader = deviceInfoReader
            ?? new MikrotikApiClient(
                connections,
                Options.Create(mikroTik),
                NullLogger<MikrotikApiClient>.Instance);

        string packagesRoot = firmwareRoot ?? CreateTempFirmwareRootWithMipsbe();
        IDeviceCompatibilityAssessor assessor = compatibilityAssessor
            ?? new DeviceCompatibilityAssessor(
                new StaticFirmwarePackageCatalog(packagesRoot),
                new FixedRouterOsV7TargetProvider("7.23.8"));

        IRouterOsV6UpgradeExecutor upgrades = upgradeExecutor
            ?? new StubUpgradeExecutor();

        RouterJobExecutor executor = new(
            connections,
            reader,
            credentials,
            assessor,
            upgrades,
            Options.Create(mikroTik),
            new FixedRouterOsV7TargetProvider("7.23.8"),
            Options.Create(new FirmwareUpgradeOptions { Enabled = false }),
            time,
            executorLogger ?? NullLogger<RouterJobExecutor>.Instance);

        CancellationTokenSource cts = new();
        Task executionTask = RunWorkersAsync(queue, executor, scheduler, options.MaxConcurrentRouterJobs, cts.Token);
        return (scheduler, queue, executionTask, cts);
    }

    /// <summary>Creates a temp firmware tree containing placeholder RouterOS 6 and 7 packages for mipsbe and arm.</summary>
    /// <returns>Full path of the temp firmware root.</returns>
    private static string CreateTempFirmwareRootWithMipsbe()
    {
        string root = Directory.CreateTempSubdirectory("mikrotik-fw-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "mikrotik"));
        File.WriteAllBytes(Path.Combine(root, "mikrotik", "routeros-mipsbe-6.49.23.npk"), [0x01]);
        File.WriteAllBytes(Path.Combine(root, "mikrotik", "routeros-arm-6.49.23.npk"), [0x01]);
        File.WriteAllBytes(Path.Combine(root, "mikrotik", "routeros-7.23.8-mipsbe.npk"), [0x01]);
        File.WriteAllBytes(Path.Combine(root, "mikrotik", "routeros-7.23.8-arm.npk"), [0x01]);
        return root;
    }

    /// <summary>Runs <paramref name="workerCount"/> worker loops until they finish or are cancelled.</summary>
    /// <param name="queue">Queue the workers read from.</param>
    /// <param name="executor">Executor that processes each work item.</param>
    /// <param name="completer">Completer notified when items finish.</param>
    /// <param name="workerCount">Number of concurrent worker loops.</param>
    /// <param name="cancellationToken">Token that stops the workers; cancellation is swallowed.</param>
    /// <returns>A task that completes when all workers have stopped.</returns>
    private static async Task RunWorkersAsync(
        RouterJobExecutionQueue queue,
        RouterJobExecutor executor,
        IRouterJobExecutionCompleter completer,
        int workerCount,
        CancellationToken cancellationToken)
    {
        Task[] workers = Enumerable
            .Range(0, workerCount)
            .Select(_ => RunOneAsync(queue, executor, completer, cancellationToken))
            .ToArray();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Reads work items from <paramref name="queue"/> and executes them until cancelled.</summary>
    /// <param name="queue">Queue to read from.</param>
    /// <param name="executor">Executor that processes each work item.</param>
    /// <param name="completer">Completer notified when items finish.</param>
    /// <param name="cancellationToken">Token that stops the loop; cancellation is swallowed.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    private static async Task RunOneAsync(
        RouterJobExecutionQueue queue,
        RouterJobExecutor executor,
        IRouterJobExecutionCompleter completer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (RouterJobWorkItem item in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await executor.ExecuteAsync(item, completer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Fake upgrade executor for scheduler/preflight tests. Defaults to a verified-target success so those tests
/// exercise the connect/assess/disconnect path; pass another outcome to test job-outcome mapping.
/// </summary>
/// <param name="outcome">Outcome returned by every <see cref="ExecuteAsync"/> call.</param>
internal sealed class StubUpgradeExecutor(UpgradeOutcome outcome = UpgradeOutcome.UpgradeSucceeded) : IRouterOsV6UpgradeExecutor
{
    /// <summary>Returns the configured outcome without touching the router.</summary>
    /// <param name="connection">Router connection (unused).</param>
    /// <param name="job">Scheduled job (unused).</param>
    /// <param name="deviceInfo">Device info (unused).</param>
    /// <param name="assessment">Compatibility assessment (unused).</param>
    /// <param name="username">Router user name (unused).</param>
    /// <param name="password">Router password (unused).</param>
    /// <param name="isWithinScheduleWindow">Schedule-window check (unused).</param>
    /// <param name="cancellationToken">Token (unused).</param>
    /// <returns>A completed task containing a result with the configured outcome.</returns>
    public Task<UpgradeExecutionResult> ExecuteAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        MikrotikDeviceInfo deviceInfo,
        DeviceCompatibilityAssessment assessment,
        string username,
        string password,
        Func<bool> isWithinScheduleWindow,
        CancellationToken cancellationToken) =>
        Task.FromResult(new UpgradeExecutionResult { Outcome = outcome, Detail = $"Stub outcome {outcome}" });
}

/// <summary>
/// Fixed in-memory firmware catalogue for scheduler execution tests.
/// </summary>
/// <param name="root">Directory scanned recursively for <c>*.npk</c> packages.</param>
internal sealed class StaticFirmwarePackageCatalog(string root) : IFirmwarePackageCatalog
{
    /// <summary>Gets only the main <c>routeros</c> packages under the root.</summary>
    /// <returns>The RouterOS packages found, or an empty list when the root is missing.</returns>
    public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
        GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

    /// <summary>Scans the root for <c>*.npk</c> files whose names parse as firmware packages.</summary>
    /// <returns>The parsed packages; files with unparseable names are skipped, and a missing root yields an empty list.</returns>
    public IReadOnlyList<FirmwarePackageInfo> GetPackages()
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        List<FirmwarePackageInfo> packages = [];
        foreach (string path in Directory.EnumerateFiles(root, "*.npk", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(path);
            if (!FirmwarePackageFileNameParser.TryParse(
                    fileName,
                    out string packageName,
                    out string? architecture,
                    out RouterOsVersion version))
            {
                continue;
            }

            FileInfo info = new(path);
            packages.Add(new FirmwarePackageInfo(
                packageName,
                fileName,
                path,
                architecture,
                version,
                info.Length,
                "00"));
        }

        return packages;
    }
}
