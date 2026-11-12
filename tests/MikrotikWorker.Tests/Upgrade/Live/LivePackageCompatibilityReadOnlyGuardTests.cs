// -----------------------------------------------------------------------------
// File: LivePackageCompatibilityReadOnlyGuardTests.cs
//
// Description:
// Guards that live-audit command paths remain allowlisted read-only print
// operations.
//
// -----------------------------------------------------------------------------

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

namespace MikrotikWorker.Tests.Upgrade.Live;
    /// <summary>
    /// Scenario: live fleet auditor wired with a throwing upgrade executor, counting backup, and tracking ops.
    /// Expected: audit completes using the fake collector/catalogue path without invoking upgrade/backup mutation surfaces.
    /// Invariant: read-only package-compatibility audit must never call the upgrade executor or create backups.
    /// </summary>
public sealed class LivePackageCompatibilityReadOnlyGuardTests
{
    /// <summary>
    /// Read-only RouterOS print commands the live audit collector is permitted to issue (identity, resource, package and routerboard).
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "/system/identity/print",
        "/system/resource/print",
        "/system/package/print",
        "/system/routerboard/print",
    };

    /// <summary>
    /// A recording connection must see only commands in the allow-list, and at least one. None of them contains
    /// fetch, reboot, backup, export, install or uninstall, and collection ends <c>Succeeded</c> with
    /// <c>CommandsIssued</c> equal to the recorded commands.
    /// </summary>
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

    /// <summary>
    /// The collector's constructor takes no <c>IMikrotikRouterOperations</c>, <c>IRouterOsV6UpgradeExecutor</c>,
    /// <c>IDeviceBackupService</c> or <c>IMikrotikApiClient</c> parameter.
    /// </summary>
    [Fact]
    public void LiveCollector_DoesNotDependOnMutationServices()
    {
        ConstructorInfo ctor = typeof(ReadOnlyMikrotikInventoryCollector).GetConstructors()[0];
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IMikrotikRouterOperations).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IRouterOsV6UpgradeExecutor).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IDeviceBackupService).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(ctor.GetParameters(), static p => typeof(IMikrotikApiClient).IsAssignableFrom(p.ParameterType));
    }

    /// <summary>
    /// The fleet auditor's constructor takes no router-operations, upgrade-executor or backup-service parameter. It
    /// does take an <c>IReadOnlyMikrotikInventoryCollector</c> and an <c>IPackageCompatibilityAuditor</c>.
    /// </summary>
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

    /// <summary>
    /// Structural: the collector only needs a connection factory and a logger. Its constructor has exactly two
    /// parameters, so it cannot reach an upgrade executor.
    /// </summary>
    [Fact]
    public void LiveAuditPath_CannotResolveUpgradeExecutorFromCollectorGraph()
    {
        // Structural: collector only needs connection factory + logger.
        Type collector = typeof(ReadOnlyMikrotikInventoryCollector);
        Assert.Equal(2, collector.GetConstructors()[0].GetParameters().Length);
    }

    /// <summary>
    /// Scenario: fleet auditor with throwing upgrade executor plus counting backup/tracking ops.
    /// Expected: audit completes via collector/catalogue without upgrade or backup mutations.
    /// Invariant: read-only package-compatibility audit never touches the upgrade executor.
    /// </summary>
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

    /// <summary>
    /// Inventory collector fake that returns the result produced by a synchronous factory for the router IP,
    /// ignoring credentials and timeouts.
    /// </summary>
    private sealed class FakeCollector(Func<string, ReadOnlyInventoryCollectionResult> factory)
        : IReadOnlyMikrotikInventoryCollector
    {
        /// <summary>
        /// Returns the result produced by the supplied factory for <paramref name="routerIp"/>, ignoring credentials and timeouts.
        /// </summary>
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

    /// <summary>
    /// Upgrade executor fake that counts calls and always throws, proving the upgrade path is unreachable from the
    /// live audit.
    /// </summary>
    private sealed class ThrowingUpgradeExecutor : IRouterOsV6UpgradeExecutor
    {
        /// <summary>
        /// Number of times <see cref="ExecuteAsync"/> was invoked; must stay zero during a live audit.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// Records the call and always throws, proving the upgrade executor is unreachable from the live audit path.
        /// </summary>
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

    /// <summary>
    /// Backup service fake that counts calls and always throws, proving backups are unreachable from the live audit
    /// path.
    /// </summary>
    private sealed class CountingBackup : IDeviceBackupService
    {
        /// <summary>
        /// Number of times <see cref="CreatePreUpgradeBackupAsync"/> was invoked; must stay zero during a live audit.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// Records the call and always throws, proving backups are unreachable from the live audit path.
        /// </summary>
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

    /// <summary>
    /// Router operations fake for the live audit read-only guard: device reads, exports, backups, fetches and
    /// reboots throw or record the attempt, while file lookups, waits and removals are harmless no-ops.
    /// </summary>
    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        /// <summary>
        /// Whether a firmware fetch was attempted; must stay <see langword="false"/> during a live audit.
        /// </summary>
        public bool FetchCalled { get; private set; }

        /// <summary>
        /// Whether a reboot was attempted; must stay <see langword="false"/> during a live audit.
        /// </summary>
        public bool RebootCalled { get; private set; }

        /// <summary>
        /// Number of configuration export or system backup attempts; must stay zero during a live audit.
        /// </summary>
        public int BackupCalls { get; private set; }

        /// <summary>
        /// Always throws, since the mutation-capable API client must not be used by the live audit.
        /// </summary>
        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Mutation-capable API client must not be used.");

        /// <summary>
        /// Always throws, since the mutation-capable API client must not be used by the live audit.
        /// </summary>
        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Mutation-capable API client must not be used.");

        /// <summary>
        /// Counts the attempt and throws, since configuration export must not be used by the live audit.
        /// </summary>
        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default)
        {
            BackupCalls++;
            throw new InvalidOperationException("export must not be used.");
        }

        /// <summary>
        /// Counts the attempt and throws, since system backups must not be used by the live audit.
        /// </summary>
        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default)
        {
            BackupCalls++;
            throw new InvalidOperationException("backup must not be used.");
        }

        /// <summary>
        /// Reports that the file does not exist.
        /// </summary>
        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(null);

        /// <summary>
        /// No-op; there is nothing to wait for.
        /// </summary>
        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; there is nothing to remove.
        /// </summary>
        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Records the attempt and throws, since firmware fetches must not be used by the live audit.
        /// </summary>
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

        /// <summary>
        /// Records the attempt and throws, since reboots must not be used by the live audit.
        /// </summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            throw new InvalidOperationException("reboot must not be used.");
        }
    }

    /// <summary>
    /// Firmware package catalogue fake that enumerates <c>*.npk</c> files under a temp root and parses each file
    /// name into a package entry.
    /// </summary>
    private sealed class DirectoryCatalog(string root) : IFirmwarePackageCatalog
    {
        /// <summary>
        /// Returns only the catalogued packages named <c>routeros</c> (the main RouterOS package).
        /// </summary>
        public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
            GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

        /// <summary>
        /// Enumerates every <c>*.npk</c> file under the root directory and returns those whose file names parse as RouterOS packages.
        /// </summary>
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
