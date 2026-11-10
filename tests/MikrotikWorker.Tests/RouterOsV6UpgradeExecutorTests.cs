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
using tik4net.Testing;

namespace MikrotikWorker.Tests;

public sealed class RouterOsV6UpgradeExecutorTests
{
    [Fact]
    public async Task BackupFailure_PreventsFetchAndReboot()
    {
        TrackingOps ops = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup: new FailingBackup(),
            enabled: true);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.BackupFailed, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
        Assert.False(result.RebootInitiated);
    }

    [Fact]
    public async Task SuccessfulBackup_AllowsPackageStagingFetch_WithoutRebootWhenFetchFailsMissingFile()
    {
        TrackingOps ops = new() { OmitFetchedFile = true };
        string root = Directory.CreateTempSubdirectory("upg-fetch-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);

        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup: new SucceedingBackup(),
            enabled: true,
            firmwareRoot: root,
            advertisedBaseUrl: "http://198.18.0.1:8080/firmware_files");

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.True(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
        Assert.Equal(UpgradeOutcome.UpgradeFailed, result.Outcome);
    }

    [Fact]
    public async Task V7Device_IsNeverModified()
    {
        TrackingOps ops = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: true);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            new MikrotikDeviceInfo
            {
                Identity = "CORE-01",
                Version = "7.23.8",
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
            },
            CreateAssessment(DeviceAssessmentStatus.Unsupported, RouterOsVersionClassification.RouterOsV7OrLater),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.PolicyBlocked, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task UpgradeDisabled_SkipsWithoutFetch()
    {
        TrackingOps ops = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: false);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.SkippedUpgradeDisabled, result.Outcome);
        Assert.False(ops.FetchCalled);
    }

    [Fact]
    public async Task MissingOptionalPackage_PreventsFetchAndReboot()
    {
        TrackingOps ops = new()
        {
            Installed =
            [
                new("routeros", "6.49.19", false),
                new("tr069-client", "6.49.19", false),
            ],
        };
        string root = Directory.CreateTempSubdirectory("upg-missing-opt-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);

        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            firmwareRoot: root);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.PackageResolutionFailed, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task CancellationDuringFetch_PreventsReboot()
    {
        TrackingOps ops = new() { FetchDelay = TimeSpan.FromSeconds(5) };
        using CancellationTokenSource cts = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: true);

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            cts.Token);

        await Task.Delay(50);
        cts.Cancel();
        UpgradeExecutionResult result = await task;

        Assert.Equal(UpgradeOutcome.UpgradeInterrupted, result.Outcome);
        Assert.False(ops.RebootCalled);
        Assert.False(result.RebootInitiated);
    }

    [Fact]
    public async Task IncompleteStaging_PreventsReboot()
    {
        TrackingOps ops = new() { OmitFetchedFile = true };
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: true);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeFailed, result.Outcome);
        Assert.True(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task OutsideScheduleWindow_PreventsFetchAndReboot()
    {
        TrackingOps ops = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, new SucceedingBackup(), enabled: true);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => false,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.PolicyBlocked, result.Outcome);
        Assert.False(ops.FetchCalled);
        Assert.False(ops.RebootCalled);
    }

    [Fact]
    public async Task ReconnectionTimeout_AfterReboot_ReportsTimedOut()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: new ThrowingMikrotikConnectionFactory(new IOException("still down")));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.True(ops.RebootCalled);
        Assert.True(result.RebootInitiated);
    }

    [Fact]
    public async Task PostUpgradeVersionMismatch_IsReported()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true, PostUpgradeVersion = "6.49.19" };
        ScriptedMikrotikConnectionFactory reconnect = new(_ => FakeMikrotikConnections.CreateFullyPopulated());
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new SucceedingBackup(),
            enabled: true,
            connectionFactory: reconnect);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CreateDevice(),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeVersionMismatch, result.Outcome);
        Assert.True(result.RebootInitiated);
    }

    private static RouterOsV6UpgradeExecutor CreateExecutor(
        TrackingOps ops,
        IDeviceBackupService backup,
        bool enabled,
        string? firmwareRoot = null,
        string advertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
        IMikrotikConnectionFactory? connectionFactory = null)
    {
        string root = firmwareRoot ?? Directory.CreateTempSubdirectory("upg-fw-").FullName;
        if (firmwareRoot is null)
        {
            File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        }

        return new RouterOsV6UpgradeExecutor(
            ops,
            connectionFactory ?? new ThrowingMikrotikConnectionFactory(new IOException("no reconnect in this test")),
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(root),
                Options.Create(new FirmwareFilesOptions { Path = root })),
            backup,
            Options.Create(new FirmwareUpgradeOptions
            {
                Enabled = enabled,
                TargetRouterOsVersion = "6.49.23",
                AdvertisedBaseUrl = advertisedBaseUrl,
                FetchTimeout = TimeSpan.FromSeconds(5),
                ReconnectInterval = TimeSpan.FromMilliseconds(10),
                RebootTimeout = TimeSpan.FromMilliseconds(50),
                PostUpgradeVerifyTimeout = TimeSpan.FromMilliseconds(50),
            }),
            Options.Create(new MikroTikOptions { Port = 8728 }),
            TimeProvider.System,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
    }

    private static MikrotikScheduledJob CreateJob() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "CORE-01",
            RouterIp = IPAddress.Parse("10.0.0.50"),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    private static MikrotikDeviceInfo CreateDevice() =>
        new()
        {
            Identity = "CORE-01",
            Version = "6.49.19",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

    private static DeviceCompatibilityAssessment CreateAssessment(
        DeviceAssessmentStatus status,
        RouterOsVersionClassification classification = RouterOsVersionClassification.OlderThanTargetV6) =>
        new()
        {
            RouterIp = "10.0.0.50",
            OverallStatus = status,
            RouterOsVersionClassification = classification,
            TargetRouterOsVersion = "6.49.23",
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    private sealed class TrackingOps : IMikrotikRouterOperations
    {
        public bool FetchCalled { get; private set; }

        public bool RebootCalled { get; private set; }

        public bool OmitFetchedFile { get; init; }

        public bool FetchReturnsMatchingSize { get; init; }

        public TimeSpan FetchDelay { get; init; }

        public string PostUpgradeVersion { get; init; } = "6.49.23";

        public IReadOnlyList<InstalledPackageInfo>? Installed { get; init; }

        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MikrotikDeviceInfo
            {
                Identity = "CORE-01",
                Version = PostUpgradeVersion,
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
            });

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Installed ??
            [
                new("routeros", PostUpgradeVersion.StartsWith("6.49.23", StringComparison.Ordinal)
                    ? "6.49.23"
                    : "6.49.19", false),
            ]);

        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            if (OmitFetchedFile && fileName.EndsWith(".npk", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<RouterFileInfo?>(null);
            }

            long size = FetchReturnsMatchingSize || !fileName.EndsWith(".npk", StringComparison.OrdinalIgnoreCase)
                ? 4
                : 4;
            return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, size));
        }

        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            if (FetchDelay > TimeSpan.Zero)
            {
                await Task.Delay(FetchDelay, cancellationToken);
            }
        }

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingBackup : IDeviceBackupService
    {
        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceBackupResult
            {
                Succeeded = false,
                FailureReason = "backup failed",
            });
    }

    private sealed class SucceedingBackup : IDeviceBackupService
    {
        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceBackupResult { Succeeded = true });
    }
}
