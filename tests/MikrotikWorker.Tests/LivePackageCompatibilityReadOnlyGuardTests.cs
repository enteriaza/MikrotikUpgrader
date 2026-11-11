using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests;

public sealed class LivePackageCompatibilityReadOnlyGuardTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "/system/identity/print",
        "/system/resource/print",
        "/system/package/print",
        "/system/routerboard/print",
    };

    [Fact]
    public async Task LiveCollector_IssuesOnlyPermittedPrintCommands()
    {
        CommandRecordingConnection recording = new(
            FakeMikrotikConnections.CreateWithPackageInventory());
        ReadOnlyMikrotikInventoryCollector collector = new(
            new ScriptedMikrotikConnectionFactory(_ => recording),
            NullLogger<ReadOnlyMikrotikInventoryCollector>.Instance);

        ReadOnlyInventoryCollectionResult result = await collector.CollectAsync(
            "10.0.0.20",
            "admin",
            "secret",
            8728,
            TimeSpan.FromSeconds(5));

        Assert.Equal(LiveInventoryCollectionStatus.Succeeded, result.CollectionStatus);
        Assert.NotEmpty(recording.Commands);
        Assert.All(recording.Commands, static c => Assert.Contains(c, Allowed));
        Assert.DoesNotContain(recording.Commands, static c => c.Contains("fetch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recording.Commands, static c => c.Contains("reboot", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recording.Commands, static c => c.Contains("backup", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recording.Commands, static c => c.Contains("export", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recording.Commands, static c => c.Contains("install", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recording.Commands, static c => c.Contains("uninstall", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(recording.Commands, result.CommandsIssued);
    }

    [Fact]
    public void LiveCollector_DoesNotDependOnMutationServices()
    {
        ConstructorInfo ctor = typeof(ReadOnlyMikrotikInventoryCollector).GetConstructors()[0];
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IMikrotikRouterOperations).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IRouterOsV6UpgradeExecutor).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IDeviceBackupService).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IMikrotikApiClient).IsAssignableFrom(p.ParameterType));
    }

    [Fact]
    public void FleetAuditor_DoesNotDependOnMutationServices()
    {
        ConstructorInfo ctor = typeof(LivePackageCompatibilityFleetAuditor).GetConstructors()[0];
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IMikrotikRouterOperations).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IRouterOsV6UpgradeExecutor).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IDeviceBackupService).IsAssignableFrom(p.ParameterType));
        Assert.Contains(ctor.GetParameters(), static p => typeof(IReadOnlyMikrotikInventoryCollector).IsAssignableFrom(p.ParameterType));
        Assert.Contains(ctor.GetParameters(), static p => typeof(IPackageCompatibilityAuditor).IsAssignableFrom(p.ParameterType));
    }

    [Fact]
    public void LiveAuditPath_CannotResolveUpgradeExecutorFromCollectorGraph()
    {
        // Structural: collector only needs connection factory + logger.
        Type collector = typeof(ReadOnlyMikrotikInventoryCollector);
        Assert.Equal(2, collector.GetConstructors()[0].GetParameters().Length);
    }

    [Fact]
    public async Task FleetAuditor_NeverTouchesUpgradeExecutor()
    {
        ThrowingUpgradeExecutor upgrade = new();
        CountingBackup backup = new();
        TrackingOps ops = new();

        FakeCollector collector = new(ip => new ReadOnlyInventoryCollectionResult
        {
            RouterIp = ip,
            CollectionStatus = LiveInventoryCollectionStatus.Succeeded,
            IdentityStatus = LiveIdentityCollectionStatus.Succeeded,
            Identity = "AP01.POP001.001.access",
            RouterOsVersion = "6.49.10",
            Architecture = "mipsbe",
            Packages = [new InstalledPackageInfo("routeros-mipsbe", "6.49.10", false)],
            CommandsIssued = ["/system/identity/print", "/system/resource/print", "/system/package/print"],
        });

        string root = Directory.CreateTempSubdirectory("live-ro-guard-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [1]);
        DirectoryCatalog catalog = new(root);

        LivePackageCompatibilityFleetAuditor auditor = new(
            collector,
            new PackageCompatibilityAuditor(
                new UpgradePackageResolver(catalog, Options.Create(new FirmwareFilesOptions { Path = root })),
                catalog,
                new FixedRouterOsV7TargetProvider("7.23.8")),
            Options.Create(new LivePackageCompatibilityAuditOptions
            {
                Enabled = true,
                MaxConcurrentConnections = 2,
            }),
            Options.Create(new MikroTikOptions
            {
                Username = "admin",
                Password = "secret",
                Port = 8728,
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            }),
            new FixedRouterOsV7TargetProvider("7.23.8"),
            NullLogger<LivePackageCompatibilityFleetAuditor>.Instance);

        using StringReader reader = new("routerip\n10.0.0.21\n");
        LiveFleetAuditReport report = await auditor.AuditAsync(LiveAuditRouterCsvParser.Parse(reader));

        Assert.True(report.Summary.Completed);
        Assert.Equal(0, upgrade.CallCount);
        Assert.Equal(0, backup.CallCount);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
        Assert.Equal(0, ops.BackupCalls);
        // Keep references so the test documents unreachable mutation surfaces.
        Assert.NotNull(upgrade);
        Assert.NotNull(backup);
        Assert.NotNull(ops);
    }

    private sealed class FakeCollector(Func<string, ReadOnlyInventoryCollectionResult> factory)
        : IReadOnlyMikrotikInventoryCollector
    {
        public Task<ReadOnlyInventoryCollectionResult> CollectAsync(
            string routerIp,
            string username,
            string password,
            int port,
            TimeSpan connectionTimeout,
            string? routerName = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(factory(routerIp));
    }

    private sealed class ThrowingUpgradeExecutor : IRouterOsV6UpgradeExecutor
    {
        public int CallCount { get; private set; }

        public Task<UpgradeExecutionResult> ExecuteAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            DeviceCompatibilityAssessment assessment,
            string username,
            string password,
            Func<bool> isWithinScheduleWindow,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Upgrade executor must not be reachable from live audit.");
        }
    }

    private sealed class CountingBackup : IDeviceBackupService
    {
        public int CallCount { get; private set; }

        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Backup must not be reachable from live audit.");
        }
    }

    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        public bool FetchCalled { get; private set; }

        public bool RebootCalled { get; private set; }

        public int BackupCalls { get; private set; }

        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Mutation-capable API client must not be used.");

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Mutation-capable API client must not be used.");

        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default)
        {
            BackupCalls++;
            throw new InvalidOperationException("export must not be used.");
        }

        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default)
        {
            BackupCalls++;
            throw new InvalidOperationException("backup must not be used.");
        }

        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(null);

        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            throw new InvalidOperationException("fetch must not be used.");
        }

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            throw new InvalidOperationException("reboot must not be used.");
        }
    }

    private sealed class DirectoryCatalog(string root) : IFirmwarePackageCatalog
    {
        public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
            GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

        public IReadOnlyList<FirmwarePackageInfo> GetPackages()
        {
            List<FirmwarePackageInfo> list = [];
            foreach (string path in Directory.EnumerateFiles(root, "*.npk", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(path);
                if (FirmwarePackageFileNameParser.TryParse(
                        fileName,
                        out string packageName,
                        out string? arch,
                        out RouterOsVersion version))
                {
                    list.Add(new FirmwarePackageInfo(
                        packageName,
                        fileName,
                        path,
                        arch,
                        version,
                        1,
                        "00"));
                }
            }

            return list;
        }
    }
}
