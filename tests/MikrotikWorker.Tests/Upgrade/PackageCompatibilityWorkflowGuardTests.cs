// -----------------------------------------------------------------------------
// File: PackageCompatibilityWorkflowGuardTests.cs
//
// Description:
// Proves incompatible catalogues block upload/reboot and that ACE/BR skip v7
// resolution.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade;
/// <summary>
/// Proves incompatible catalogues block upload/reboot and that ACE/BR skip v7 resolution.
/// </summary>
public sealed class PackageCompatibilityWorkflowGuardTests
{
    /// <summary>
    /// Wrong arch only — mipsbe device cannot resolve.
    /// With only an <c>arm</c> 6.49.23 package in the catalogue, a mipsbe router cannot resolve its v6 stepping
    /// package. The executor must return <c>PackageResolutionFailed</c> without fetching, rebooting or taking a
    /// backup (<c>CallCount</c> stays 0).
    /// </summary>
    [Fact]
    public async Task IncompatibleV6Catalogue_BlocksBeforeUploadOrReboot()
    {
        TrackingOps ops = new();
        CountingBackup backup = new();
        string root = Directory.CreateTempSubdirectory("compat-wf-v6-").FullName;
        // Wrong arch only — mipsbe device cannot resolve.
        File.WriteAllBytes(Path.Combine(root, "routeros-arm-6.49.23.npk"), [1, 2, 3, 4]);
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, root, backup: backup);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.PackageResolutionFailed, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
        Assert.Equal(0, backup.CallCount);
    }

    /// <summary>
    /// No v7 package.
    /// With a mipsbe 6.49.23 package but no RouterOS 7 package, the v6 stage runs to completion (one reboot,
    /// 6.49.23 fetched) and the executor then returns <c>PackageResolutionFailed</c>. No 7.23.8 file may be
    /// fetched.
    /// </summary>
    [Fact]
    public async Task IncompatibleV7Catalogue_BlocksAfterSuccessfulV6_BeforeV7Upload()
    {
        StageOps ops = new("AP01.POP001.001.access")
        {
            VersionsAfterReboot = ["6.49.23"],
        };
        string root = Directory.CreateTempSubdirectory("compat-wf-v7-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        // No v7 package.
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.23"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, root, reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(identity: "AP01.POP001.001.access", version: "6.49.10"),
            CreateJob(),
            CreateDevice("AP01.POP001.001.access", "6.49.10"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.PackageResolutionFailed, result.Outcome);
        Assert.Equal(1, ops.RebootCount);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("6.49.23", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    /// <summary>
    /// An ACE identity with both a mipsbe 6.49.23 package and a 7.23.8 package in the catalogue must audit as
    /// <c>Compatible</c> for the v6 stepping stage and for the RouterOS 7 stage, with
    /// <c>Classification.ShouldUpgradeToV7</c> true.
    /// </summary>
    [Fact]
    public void Ace_ResolvesV7InAudit()
    {
        string root = Directory.CreateTempSubdirectory("compat-wf-ace-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [1]);
        DirectoryCatalog catalog = new(root);
        PackageCompatibilityAuditor auditor = new(
            new UpgradePackageResolver(catalog, Options.Create(new FirmwareFilesOptions { Path = root })),
            catalog,
            new FixedRouterOsV7TargetProvider("7.23.8"));

        PackageCompatibilityDeviceReport report = auditor.AssessDevice(
            SyntheticPackageInventoryFixtures.AceIdentity());

        Assert.Equal(
            CatalogueCompatibilityStatus.Compatible,
            report.Stages.Single(static s => s.Stage == PackageCompatibilityUpgradeStage.V6Stepping).Status);
        Assert.Equal(
            CatalogueCompatibilityStatus.Compatible,
            report.Stages.Single(static s => s.Stage == PackageCompatibilityUpgradeStage.RouterOs7).Status);
        Assert.True(report.Classification.ShouldUpgradeToV7);
    }

    /// <summary>
    /// Auditor type only depends on resolver/catalog/target provider — no router ops.
    /// Reflects over the first <c>PackageCompatibilityAuditor</c> constructor and asserts that none of its
    /// parameters is an <c>IMikrotikRouterOperations</c> or an <c>IRouterOsV6UpgradeExecutor</c>, so the audit
    /// cannot reach a router.
    /// </summary>
    [Fact]
    public void Audit_DoesNotExposeMutationSurface()
    {
        // Auditor type only depends on resolver/catalog/target provider — no router ops.
        Type auditor = typeof(PackageCompatibilityAuditor);
        Assert.DoesNotContain(
            auditor.GetConstructors()[0].GetParameters(),
            static p => typeof(IMikrotikRouterOperations).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(
            auditor.GetConstructors()[0].GetParameters(),
            static p => typeof(IRouterOsV6UpgradeExecutor).IsAssignableFrom(p.ParameterType));
    }

    /// <summary>
    /// Builds a <see cref="RouterOsV6UpgradeExecutor"/> over the supplied fakes with short test timeouts and a
    /// RouterOS 7.23.8 target; reconnects throw unless a factory is supplied and backups default to a new <see
    /// cref="CountingBackup"/>.
    /// </summary>
    private static RouterOsV6UpgradeExecutor CreateExecutor(
        IMikrotikRouterOperations ops,
        string firmwareRoot,
        IMikrotikConnectionFactory? connectionFactory = null,
        CountingBackup? backup = null)
    {
        backup ??= new CountingBackup();
        IMikrotikConnectionFactory factory =
            connectionFactory ?? new ThrowingMikrotikConnectionFactory(new IOException("no reconnect"));
        IOptions<FirmwareUpgradeOptions> upgradeOptions = Options.Create(new FirmwareUpgradeOptions
        {
            Enabled = true,
            AdvertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
            FetchTimeout = TimeSpan.FromSeconds(5),
            ReconnectInterval = TimeSpan.FromMilliseconds(10),
            RebootTimeout = TimeSpan.FromSeconds(5),
            PostUpgradeVerifyTimeout = TimeSpan.FromSeconds(5),
            BackupFileReadyTimeout = TimeSpan.FromMilliseconds(100),
            BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(20),
        });
        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot });
        IOptions<MikroTikOptions> mikrotikOptions = Options.Create(new MikroTikOptions { Port = 8728 });

        return new RouterOsV6UpgradeExecutor(
            ops,
            factory,
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(firmwareRoot),
                firmwareFilesOptions),
            backup,
            new FixedRouterOsV7TargetProvider("7.23.8"),
            new LegacyFleetUpgradeAutomationCleanup(
                ops,
                NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance),
            new RouterboardFirmwareUpgradeService(
                ops,
                factory,
                upgradeOptions,
                mikrotikOptions,
                TimeProvider.System,
                NullLogger<RouterboardFirmwareUpgradeService>.Instance),
            new PassingOspfMigrationService(),
            new PassingOspfStaticSummaryRedistributionService(),
            upgradeOptions,
            mikrotikOptions,
            TimeProvider.System,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
    }

    /// <summary>Builds a minimal scheduled job (router <c>R1</c>, 10.0.0.50) for executor calls.</summary>
    private static MikrotikScheduledJob CreateJob() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "R1",
            RouterIp = IPAddress.Parse("10.0.0.50"),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    /// <summary>
    /// Builds <see cref="MikrotikDeviceInfo"/> (mipsbe, hEX PoE) with the given identity and version.
    /// </summary>
    private static MikrotikDeviceInfo CreateDevice(string identity, string version) =>
        new()
        {
            Identity = identity,
            Version = version,
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

    /// <summary>Builds a ready-for-upgrade, compatible assessment targeting RouterOS 7.23.8.</summary>
    private static DeviceCompatibilityAssessment CreateAssessment() =>
        new()
        {
            RouterIp = "10.0.0.50",
            OverallStatus = DeviceAssessmentStatus.ReadyForUpgrade,
            RouterOsVersionClassification = RouterOsVersionClassification.OlderThanTargetV6,
            TargetRouterOsVersion = "7.23.8",
            PackageCompatibility = MikrotikWorker.Assessment.PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>Backup fake that always succeeds and counts how many backups were requested.</summary>
    private sealed class CountingBackup : IDeviceBackupService
    {
        /// <summary>Number of pre-upgrade backups requested.</summary>
        public int CallCount { get; private set; }

        /// <summary>Counts the call and returns success with remote cleanup completed.</summary>
        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new DeviceBackupResult { Succeeded = true, RemoteCleanupSucceeded = true });
        }
    }

    /// <summary>
    /// Router operations fake for a RouterOS 6.49.10 access router that records fetch, reboot and backup-export calls
    /// so tests can prove none happen when the package-compatibility guard blocks the upgrade.
    /// </summary>
    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        /// <summary>
        /// True once <see cref="FetchUrlToFileAsync"/> has been invoked, i.e. the firmware package fetch was
        /// attempted.
        /// </summary>
        public bool FetchCalled { get; private set; }

        /// <summary>
        /// True once <see cref="RebootAsync"/> has been invoked, i.e. the executor requested a router reboot.
        /// </summary>
        public bool RebootCalled { get; private set; }

        /// <summary>Number of configuration exports requested (the first backup step).</summary>
        public int BackupCalls { get; private set; }

        /// <summary>Returns RouterOS 6.49.10 device info for <c>AP01.POP001.001.access</c>.</summary>
        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDevice("AP01.POP001.001.access", "6.49.10"));

        /// <summary>Returns a single enabled <c>routeros-mipsbe</c> 6.49.10 package.</summary>
        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([new("routeros-mipsbe", "6.49.10", false)]);

        /// <summary>Counts the export in <see cref="BackupCalls"/> without writing a file.</summary>
        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default)
        {
            BackupCalls++;
            return Task.CompletedTask;
        }

        /// <summary>No-op: pretends the binary <c>.backup</c> system backup was saved on the router.</summary>
        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>Always reports the file as missing.</summary>
        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(null);

        /// <summary>
        /// No-op: files are reported ready immediately, so the executor never waits on backup-file polling.
        /// </summary>
        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>No-op: pretends the file was deleted from the router.</summary>
        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>Marks the fetch as called; no data is transferred.</summary>
        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            return Task.CompletedTask;
        }

        /// <summary>Marks the reboot as requested.</summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reports zero matching system scripts, so legacy automation cleanup finds nothing to inspect.
        /// </summary>
        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>No-op: there are no legacy scripts to remove.</summary>
        public Task RemoveSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Reports zero matching schedulers, so legacy automation cleanup finds nothing to inspect.
        /// </summary>
        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>No-op: there are no legacy schedulers to remove.</summary>
        public Task RemoveSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Stateful router operations fake for a given identity: the reported version starts at 6.49.10 and follows <see
    /// cref="VersionsAfterReboot"/> after each reboot.
    /// </summary>
    private sealed class StageOps(string identity) : IMikrotikRouterOperations
    {
        /// <summary>
        /// 1-based index of the latest reboot into <see cref="VersionsAfterReboot"/>; zero before any reboot.
        /// </summary>
        private int _rebootIndex;

        /// <summary>Versions reported after each successive reboot; the last entry repeats once exhausted.</summary>
        public List<string> VersionsAfterReboot { get; init; } = [];

        /// <summary>Number of reboots requested so far.</summary>
        public int RebootCount { get; private set; }

        /// <summary>Destination file names passed to <see cref="FetchUrlToFileAsync"/>, in call order.</summary>
        public List<string> FetchedFiles { get; } = [];

        /// <summary>
        /// Version reported now: 6.49.10 before any reboot, then the entry of <see cref="VersionsAfterReboot"/>
        /// selected by the latest reboot.
        /// </summary>
        private string CurrentVersion =>
            RebootCount == 0
                ? "6.49.10"
                : VersionsAfterReboot[Math.Min(_rebootIndex - 1, VersionsAfterReboot.Count - 1)];

        /// <summary>Returns device info for the configured identity at the current version.</summary>
        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MikrotikDeviceInfo
            {
                Identity = identity,
                Version = CurrentVersion,
                ArchitectureName = "mipsbe",
                BoardName = "hEX",
            });

        /// <summary>
        /// Returns a single main package (<c>routeros-mipsbe</c> on RouterOS 6, <c>routeros</c> on 7+) at the current
        /// version.
        /// </summary>
        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            RouterOsVersion ver = RouterOsVersion.Parse(CurrentVersion);
            string main = ver.IsV7OrLater ? "routeros" : "routeros-mipsbe";
            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([
                new(main, ver.ToString(), false),
            ]);
        }

        /// <summary>No-op: pretends the <c>.rsc</c> configuration export was written on the router.</summary>
        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>No-op: pretends the binary <c>.backup</c> system backup was saved on the router.</summary>
        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>Reports every file as present with 4 bytes, matching the stub firmware payloads.</summary>
        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, 4));

        /// <summary>
        /// No-op: files are reported ready immediately, so the executor never waits on backup-file polling.
        /// </summary>
        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>No-op: pretends the file was deleted from the router.</summary>
        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>Records the destination file name without transferring anything.</summary>
        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchedFiles.Add(destinationFileName);
            return Task.CompletedTask;
        }

        /// <summary>Counts the reboot and selects the next post-reboot version.</summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCount++;
            _rebootIndex = RebootCount;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reports zero matching system scripts, so legacy automation cleanup finds nothing to inspect.
        /// </summary>
        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>No-op: there are no legacy scripts to remove.</summary>
        public Task RemoveSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Reports zero matching schedulers, so legacy automation cleanup finds nothing to inspect.
        /// </summary>
        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>No-op: there are no legacy schedulers to remove.</summary>
        public Task RemoveSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Firmware package catalog that enumerates <c>.npk</c> files under a directory and parses their names, so tests
    /// can use stub packages without the production catalog.
    /// </summary>
    private sealed class DirectoryCatalog(string root) : IFirmwarePackageCatalog
    {
        /// <summary>Returns only the main <c>routeros</c> packages from <see cref="GetPackages"/>.</summary>
        public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
            GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

        /// <summary>
        /// Scans the directory recursively and returns an entry for each <c>.npk</c> whose file name parses as a
        /// firmware package.
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
