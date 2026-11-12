using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Tests.Infrastructure.Fakes;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade;

/// <summary>
/// RES-001 ownership contract:
/// <list type="bullet">
/// <item><see cref="RouterJobExecutor"/> always owns Conn₀ and is its sole disposer.</item>
/// <item><c>ExecuteStageAsync</c> never disposes its input; it owns only reconnect sessions until transfer.</item>
/// <item>Intermediate <c>LiveConnection</c> values are owned by <see cref="RouterOsV6UpgradeExecutor"/> via disposeLive / disposePostV7.</item>
/// </list>
/// </summary>
public sealed class ConnectionDisposalAfterRebootTests
{
    [Fact]
    public async Task SuccessfulRebootAndReconnect_LeavesConn0ForCaller_DisposesReplacementOnce()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        DisposeTrackingConnection original = CreateStrictOriginal();
        RecordingReconnectFactory factory = new(postVersion: "6.49.23");
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            factory,
            timeProvider: TimeProvider.System,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromMilliseconds(5));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            original,
            CreateJob(),
            CreateDevice(),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.True(ops.RebootCalled);
        Assert.True(result.RebootInitiated);
        Assert.Equal(0, original.DisposeCount);
        Assert.True(factory.Created.Count >= 1);
        Assert.All(factory.Created, c => Assert.Equal(1, c.DisposeCount));

        SimulateJobExecutorDispose(original, result);
        Assert.Equal(1, original.DisposeCount);
    }

    [Fact]
    public async Task ReconnectionFailure_LeavesConn0ForCaller_AndReportsTimedOut()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        DisposeTrackingConnection original = CreateStrictOriginal();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new ThrowingMikrotikConnectionFactory(new IOException("still down")),
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(15));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            original,
            CreateJob(),
            CreateDevice(),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        for (int i = 0; i < 25 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(15));
            await Task.Yield();
        }

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.True(result.RebootInitiated);
        Assert.Equal(0, original.DisposeCount);

        SimulateJobExecutorDispose(original, result);
        Assert.Equal(1, original.DisposeCount);
    }

    [Fact]
    public async Task CancellationDuringReconnection_LeavesConn0ForCaller_AndReportsInterrupted()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        DisposeTrackingConnection original = CreateStrictOriginal();
        HangingReconnectFactory factory = new();
        using CancellationTokenSource cts = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            factory,
            timeProvider: TimeProvider.System,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromMilliseconds(10));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            original,
            CreateJob(),
            CreateDevice(),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            cts.Token);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpgradeOutcome.UpgradeInterrupted, result.Outcome);
        Assert.True(result.RebootInitiated);
        Assert.Equal(0, original.DisposeCount);

        SimulateJobExecutorDispose(original, result);
        Assert.Equal(1, original.DisposeCount);
    }

    [Fact]
    public async Task TwoStageReboots_DisposeEveryOwnedReplacement_ExactlyOnce_Conn0LeftForCaller()
    {
        string root = Directory.CreateTempSubdirectory("upg-dispose-2stage-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-7.23.8.npk"), [1, 2, 3, 4]);

            TwoStageTrackingOps ops = new();
            DisposeTrackingConnection original = CreateStrictOriginal(identity: "BR01.POP001.001.core");
            TwoStageReconnectFactory factory = new();
            RouterOsV6UpgradeExecutor executor = CreateExecutor(
                ops,
                factory,
                timeProvider: TimeProvider.System,
                rebootTimeout: TimeSpan.FromMinutes(5),
                reconnectInterval: TimeSpan.FromMilliseconds(5),
                firmwareRoot: root,
                v7Target: "7.23.8",
                identity: "BR01.POP001.001.core");

            UpgradeExecutionResult result = await executor.ExecuteAsync(
                original,
                CreateJob(),
                CreateDevice(identity: "BR01.POP001.001.core"),
                CreateAssessment(),
                "admin",
                "secret",
                static () => true,
                CancellationToken.None);

            Assert.True(result.RebootInitiated);
            Assert.Equal(0, original.DisposeCount);
            Assert.True(factory.Created.Count >= 2);
            Assert.All(factory.Created, c => Assert.Equal(1, c.DisposeCount));
            Assert.NotEqual(UpgradeOutcome.NotAttempted, result.Outcome);
            Assert.True(ops.RebootCalled);

            SimulateJobExecutorDispose(original, result);
            Assert.Equal(1, original.DisposeCount);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task DisposeThrowsOnConn0_DoesNotMaskReconnectionTimeoutOutcome()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        ThrowingDisposeConnection original = new(FakeMikrotikConnections.CreateFullyPopulated(
            identity: "ACE01.POP001.001.core",
            version: "6.49.10",
            architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            new ThrowingMikrotikConnectionFactory(new IOException("still down")),
            timeProvider: time,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromSeconds(15));

        Task<UpgradeExecutionResult> task = executor.ExecuteAsync(
            original,
            CreateJob(),
            CreateDevice(),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCalled, TimeSpan.FromSeconds(5));
        for (int i = 0; i < 25 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(15));
            await Task.Yield();
        }

        UpgradeExecutionResult result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.True(result.RebootInitiated);
        Assert.Equal(0, original.DisposeCount);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => original.Dispose());
        Assert.Equal("dispose failed", ex.Message);
        Assert.Equal(1, original.DisposeCount);
        Assert.True(original.Threw);
    }

    [Fact]
    public async Task JobExecutor_AfterRebootInitiated_DisposesConn0_ExactlyOnce()
    {
        TrackingOps ops = new() { FetchReturnsMatchingSize = true };
        DisposeTrackingConnection original = CreateStrictOriginal();
        RecordingReconnectFactory factory = new(postVersion: "6.49.23");
        RouterOsV6UpgradeExecutor upgradeExecutor = CreateExecutor(
            ops,
            factory,
            timeProvider: TimeProvider.System,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromMilliseconds(5));

        FixedConnectionFactory jobFactory = new(original);
        CompleterStub completer = new();
        RouterJobExecutor jobExecutor = new(
            jobFactory,
            new DeviceInfoReaderStub(ops),
            new StaticCredentials(),
            new ReadyAssessor(),
            upgradeExecutor,
            Options.Create(new MikroTikOptions { Enabled = true, Port = 8728 }),
            new FixedRouterOsV7TargetProvider(null),
            Options.Create(new FirmwareUpgradeOptions
            {
                Enabled = true,
                AdvertisedBaseUrl = "http://198.18.0.1:8080/firmware_files",
            }),
            TimeProvider.System,
            NullLogger<RouterJobExecutor>.Instance);

        MikrotikScheduledJob job = CreateJob();
        await jobExecutor.ExecuteAsync(
            new RouterJobWorkItem(job, entryToken: new object()),
            completer,
            CancellationToken.None);

        Assert.True(ops.RebootCalled);
        Assert.Equal(1, original.DisposeCount);
        Assert.True(factory.Created.Count >= 1);
        Assert.All(factory.Created, c => Assert.Equal(1, c.DisposeCount));
        Assert.NotNull(completer.LastState);
    }

    [Fact]
    public async Task NoRebootPath_JobExecutor_StillDisposesConn0_ExactlyOnce()
    {
        TrackingOps ops = new();
        DisposeTrackingConnection original = CreateStrictOriginal();
        RouterOsV6UpgradeExecutor upgradeExecutor = CreateExecutor(
            ops,
            new ThrowingMikrotikConnectionFactory(new IOException("unused")),
            timeProvider: TimeProvider.System,
            rebootTimeout: TimeSpan.FromMinutes(5),
            reconnectInterval: TimeSpan.FromMilliseconds(5),
            enabled: false);

        FixedConnectionFactory jobFactory = new(original);
        CompleterStub completer = new();
        RouterJobExecutor jobExecutor = new(
            jobFactory,
            new DeviceInfoReaderStub(ops),
            new StaticCredentials(),
            new ReadyAssessor(),
            upgradeExecutor,
            Options.Create(new MikroTikOptions { Enabled = true, Port = 8728 }),
            new FixedRouterOsV7TargetProvider(null),
            Options.Create(new FirmwareUpgradeOptions
            {
                Enabled = false,
                AdvertisedBaseUrl = "http://198.18.0.1:8080/firmware_files",
            }),
            TimeProvider.System,
            NullLogger<RouterJobExecutor>.Instance);

        await jobExecutor.ExecuteAsync(
            new RouterJobWorkItem(CreateJob(), entryToken: new object()),
            completer,
            CancellationToken.None);

        Assert.False(ops.RebootCalled);
        Assert.Equal(1, original.DisposeCount);
        Assert.Equal(ScheduledJobTerminalState.Failed, completer.LastState);
    }

    private static DisposeTrackingConnection CreateStrictOriginal(string identity = "ACE01.POP001.001.core") =>
        new(FakeMikrotikConnections.CreateFullyPopulated(
            identity: identity,
            version: "6.49.10",
            architecture: "mipsbe"))
        {
            FailOnDoubleDispose = true,
        };

    /// <summary>Mirrors <see cref="RouterJobExecutor"/> Conn₀ disposal after upgrade returns.</summary>
    private static void SimulateJobExecutorDispose(
        DisposeTrackingConnection original,
        UpgradeExecutionResult result)
    {
        Assert.True(result.RebootInitiated || original.DisposeCount == 0);
        original.Dispose();
    }

    private static RouterOsV6UpgradeExecutor CreateExecutor(
        IMikrotikRouterOperations ops,
        IMikrotikConnectionFactory factory,
        TimeProvider timeProvider,
        TimeSpan rebootTimeout,
        TimeSpan reconnectInterval,
        string? firmwareRoot = null,
        string? v7Target = null,
        string? identity = null,
        bool enabled = true)
    {
        string root = firmwareRoot ?? Directory.CreateTempSubdirectory("upg-dispose-").FullName;
        if (firmwareRoot is null)
        {
            File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        }

        if (ops is TrackingOps tracking && identity is not null)
        {
            tracking.Identity = identity;
        }

        IOptions<FirmwareUpgradeOptions> upgradeOptions = Options.Create(new FirmwareUpgradeOptions
        {
            Enabled = enabled,
            AdvertisedBaseUrl = "http://198.18.0.1:8080/firmware_files",
            FetchTimeout = TimeSpan.FromSeconds(5),
            ReconnectInterval = reconnectInterval,
            RebootTimeout = rebootTimeout,
            PostUpgradeVerifyTimeout = rebootTimeout,
            BackupFileReadyTimeout = TimeSpan.FromMilliseconds(100),
            BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(20),
        });
        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = root });
        IOptions<MikroTikOptions> mikrotikOptions = Options.Create(new MikroTikOptions { Port = 8728 });

        return new RouterOsV6UpgradeExecutor(
            ops,
            factory,
            new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(root),
                firmwareFilesOptions),
            new SucceedingBackup(),
            new FixedRouterOsV7TargetProvider(v7Target),
            new LegacyFleetUpgradeAutomationCleanup(
                ops,
                NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance),
            new RouterboardFirmwareUpgradeService(
                ops,
                factory,
                upgradeOptions,
                mikrotikOptions,
                timeProvider,
                NullLogger<RouterboardFirmwareUpgradeService>.Instance),
            new PassingOspfMigrationService(),
            new PassingOspfStaticSummaryRedistributionService(),
            upgradeOptions,
            mikrotikOptions,
            timeProvider,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met before timeout.");
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

    private static MikrotikDeviceInfo CreateDevice(string identity = "ACE01.POP001.001.core") =>
        new()
        {
            Identity = identity,
            Version = "6.49.10",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

    private static DeviceCompatibilityAssessment CreateAssessment() =>
        new()
        {
            RouterIp = "10.0.0.50",
            OverallStatus = DeviceAssessmentStatus.ReadyForUpgrade,
            RouterOsVersionClassification = RouterOsVersionClassification.OlderThanTargetV6,
            TargetRouterOsVersion = "7.23.8",
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    private sealed class ThrowingDisposeConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        public bool Threw { get; private set; }

        public override void Dispose()
        {
            if (DisposeCount > 0)
            {
                throw new ObjectDisposedException(nameof(ThrowingDisposeConnection));
            }

            base.Dispose();
            Threw = true;
            throw new InvalidOperationException("dispose failed");
        }
    }

    private sealed class RecordingReconnectFactory(string postVersion) : IMikrotikConnectionFactory
    {
        public List<DisposeTrackingConnection> Created { get; } = [];

        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            DisposeTrackingConnection created = new(FakeMikrotikConnections.CreateFullyPopulated(
                identity: "ACE01.POP001.001.core",
                version: postVersion,
                architecture: "mipsbe"))
            {
                FailOnDoubleDispose = true,
            };
            Created.Add(created);
            return Task.FromResult<ITikConnection>(created);
        }
    }

    private sealed class TwoStageReconnectFactory : IMikrotikConnectionFactory
    {
        public List<DisposeTrackingConnection> Created { get; } = [];

        private int _successes;

        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            _successes++;
            string version = _successes == 1 ? "6.49.23" : "7.23.8";
            DisposeTrackingConnection created = new(FakeMikrotikConnections.CreateFullyPopulated(
                identity: "BR01.POP001.001.core",
                version: version,
                architecture: "mipsbe"))
            {
                FailOnDoubleDispose = true,
            };
            Created.Add(created);
            return Task.FromResult<ITikConnection>(created);
        }
    }

    private sealed class HangingReconnectFactory : IMikrotikConnectionFactory
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class FixedConnectionFactory(ITikConnection connection) : IMikrotikConnectionFactory
    {
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connectionOptions,
            CancellationToken cancellationToken) =>
            Task.FromResult(connection);
    }

    private sealed class DeviceInfoReaderStub(TrackingOps ops) : IMikrotikDeviceInfoReader
    {
        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            ops.ReadDeviceInfoAsync(connection, host, cancellationToken);
    }

    private sealed class StaticCredentials : IRouterCredentialResolver
    {
        public ValueTask<RouterCredentials?> ResolveAsync(
            MikrotikScheduledJob job,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<RouterCredentials?>(new RouterCredentials("admin", "secret"));
    }

    private sealed class ReadyAssessor : IDeviceCompatibilityAssessor
    {
        public DeviceCompatibilityAssessment Assess(
            string routerIp,
            MikrotikDeviceInfo deviceInfo,
            DateTimeOffset assessedAtUtc) =>
            new()
            {
                RouterIp = routerIp,
                OverallStatus = DeviceAssessmentStatus.ReadyForUpgrade,
                RouterOsVersionClassification = RouterOsVersionClassification.OlderThanTargetV6,
                TargetRouterOsVersion = "7.23.8",
                PackageCompatibility = PackageCompatibilityStatus.Compatible,
                UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
                RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
                AssessedAtUtc = assessedAtUtc,
            };
    }

    private sealed class CompleterStub : IRouterJobExecutionCompleter
    {
        public ScheduledJobTerminalState? LastState { get; private set; }

        public bool IsImportAbortedOrStopping(Guid importId) => false;

        public bool TryRequeueDispatchedJob(object entryToken) => false;

        public void CompleteDispatchedJob(
            object entryToken,
            ScheduledJobTerminalState state,
            TimeSpan? executionDuration = null,
            Exception? error = null) =>
            LastState = state;
    }

    private class TrackingOps : IMikrotikRouterOperations
    {
        public bool FetchCalled { get; private set; }

        public bool RebootCalled { get; private set; }

        public bool FetchReturnsMatchingSize { get; init; } = true;

        public string PostUpgradeVersion { get; set; } = "6.49.23";

        public string Identity { get; set; } = "ACE01.POP001.001.core";

        public virtual Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MikrotikDeviceInfo
            {
                Identity = Identity,
                Version = RebootCalled ? PostUpgradeVersion : "6.49.10",
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
                RouterboardCurrentFirmware = "7.23.8",
                RouterboardUpgradeFirmware = "7.23.8",
            });

        public virtual Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            string versionText = RebootCalled ? PostUpgradeVersion : "6.49.10";
            string numeric = RouterOsVersion.TryParse(versionText, out RouterOsVersion parsed)
                ? parsed.ToString()
                : "6.49.23";
            string main = RouterOsVersion.TryParse(versionText, out RouterOsVersion ver) && ver.IsV7OrLater
                ? "routeros"
                : "routeros-mipsbe";
            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([new(main, numeric, false)]);
        }

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
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, FetchReturnsMatchingSize ? 4 : 0));

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
            return Task.CompletedTask;
        }

        public virtual Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCalled = true;
            return Task.CompletedTask;
        }

        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task RemoveSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task RemoveSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpgradeRouterboardAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TwoStageTrackingOps : TrackingOps
    {
        private int _reboots;

        public override Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            _reboots++;
            PostUpgradeVersion = _reboots == 1 ? "6.49.23" : "7.23.8";
            return base.RebootAsync(connection, cancellationToken);
        }
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
            Task.FromResult(new DeviceBackupResult
            {
                Succeeded = true,
                RemoteCleanupSucceeded = true,
            });
    }
}
