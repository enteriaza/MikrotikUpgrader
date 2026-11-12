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
internal sealed class StubUpgradeExecutor(UpgradeOutcome outcome = UpgradeOutcome.UpgradeSucceeded) : IRouterOsV6UpgradeExecutor
{
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

internal sealed class StaticFirmwarePackageCatalog(string root) : IFirmwarePackageCatalog
{
    public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
        GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

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
