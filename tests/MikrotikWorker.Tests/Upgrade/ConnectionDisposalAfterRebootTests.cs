// -----------------------------------------------------------------------------
// File: ConnectionDisposalAfterRebootTests.cs
//
// Description:
// Verifies post-reboot connection ownership and disposal rules in the upgrade
// executor.
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
    /// <summary>
    /// After a reboot and a successful reconnect to 6.49.23, <c>RebootInitiated</c> is true and every connection
    /// the factory created has been disposed exactly once. The caller's original connection is untouched
    /// (<c>DisposeCount</c> 0) until the job executor's dispose is simulated, which brings it to exactly 1.
    /// </summary>
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

    /// <summary>
    /// With reconnects failing and a fake clock advanced in 15 s steps past the 5 minute reboot timeout, the
    /// outcome is <c>UpgradeTimedOut</c> with <c>RebootInitiated</c> true. The original connection is still
    /// undisposed until the caller disposes it, which counts exactly once.
    /// </summary>
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

    /// <summary>
    /// Cancelling while a reconnect attempt is hanging must give <c>UpgradeInterrupted</c> with
    /// <c>RebootInitiated</c> true. The original connection stays undisposed until the caller disposes it, which
    /// counts exactly once.
    /// </summary>
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

    /// <summary>
    /// A BR core router upgraded through 6.49.23 and then 7.23.8 reboots twice and creates at least two replacement
    /// connections. Each of them is disposed exactly once, the outcome is not <c>NotAttempted</c>, and the original
    /// connection is disposed once, and only by the caller.
    /// </summary>
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

    /// <summary>
    /// The executor still reports <c>UpgradeTimedOut</c> with <c>RebootInitiated</c> true when the original
    /// connection's <c>Dispose</c> throws. The test then calls <c>Dispose</c> itself and expects an
    /// <c>InvalidOperationException</c> ('dispose failed') with <c>DisposeCount</c> 1 and <c>Threw</c> true.
    /// </summary>
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

    /// <summary>
    /// Driven through <c>RouterJobExecutor</c> with a ready assessment, the reboot is requested, every replacement
    /// connection is disposed once, the original connection is disposed exactly once by the job executor, and the
    /// completer receives a terminal state.
    /// </summary>
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

    /// <summary>
    /// With firmware upgrades disabled, no reboot happens (<c>RebootCalled</c> false) and the job completes as
    /// <c>ScheduledJobTerminalState.Failed</c>. The original connection is still disposed exactly once.
    /// </summary>
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

    /// <summary>
    /// Creates the pre-reboot "original" connection wrapper (RouterOS 6.49.10, mipsbe) that throws if disposed more
    /// than once.
    /// </summary>
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

    /// <summary>
    /// Builds a <see cref="RouterOsV6UpgradeExecutor"/> over the supplied fakes with a temporary firmware directory
    /// (stub 6.49.23 package unless one is given), optional identity override and RouterOS 7 target.
    /// </summary>
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

    /// <summary>
    /// Polls a condition until timeout or success.
    /// </summary>
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

    /// <summary>Builds a minimal scheduled job (router <c>CORE-01</c>, 10.0.0.50) for executor calls.</summary>
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

    /// <summary>
    /// Builds RouterOS 6.49.10 <see cref="MikrotikDeviceInfo"/> (mipsbe, hEX PoE) with the given identity.
    /// </summary>
    private static MikrotikDeviceInfo CreateDevice(string identity = "ACE01.POP001.001.core") =>
        new()
        {
            Identity = identity,
            Version = "6.49.10",
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
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Connection wrapper whose first dispose succeeds internally and then throws, simulating a faulty dispose of the
    /// original connection.
    /// </summary>
    private sealed class ThrowingDisposeConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        /// <summary>True once the simulated dispose failure was thrown.</summary>
        public bool Threw { get; private set; }

        /// <summary>
        /// Disposes the wrapped connection, then throws <see cref="InvalidOperationException"/>; a second dispose
        /// throws <see cref="ObjectDisposedException"/>.
        /// </summary>
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

    /// <summary>
    /// Connection factory that returns strict dispose-tracking connections at a fixed post-upgrade version, recording
    /// each one so tests can assert they are disposed exactly once.
    /// </summary>
    private sealed class RecordingReconnectFactory(string postVersion) : IMikrotikConnectionFactory
    {
        /// <summary>Every connection handed out, in creation order.</summary>
        public List<DisposeTrackingConnection> Created { get; } = [];

        /// <summary>
        /// Creates and records a dispose-tracking connection reporting the configured post-upgrade version.
        /// </summary>
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

    /// <summary>
    /// Connection factory for the two-stage flow: the first reconnect reports RouterOS 6.49.23 and later ones
    /// RouterOS 7.23.8.
    /// </summary>
    private sealed class TwoStageReconnectFactory : IMikrotikConnectionFactory
    {
        /// <summary>Every connection handed out, in creation order.</summary>
        public List<DisposeTrackingConnection> Created { get; } = [];

        /// <summary>Number of connections created so far; selects the reported version.</summary>
        private int _successes;

        /// <summary>
        /// Creates and records a strict dispose-tracking connection whose version depends on how many reconnects have
        /// happened.
        /// </summary>
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

    /// <summary>
    /// Connection factory that blocks until cancelled, used to verify disposal behaviour when the reconnect wait is
    /// cancelled.
    /// </summary>
    private sealed class HangingReconnectFactory : IMikrotikConnectionFactory
    {
        /// <summary>Completed when the first reconnect attempt begins.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signals <see cref="Started"/> and waits indefinitely until cancelled.</summary>
        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Connection factory that always returns the same pre-built connection.</summary>
    private sealed class FixedConnectionFactory(ITikConnection connection) : IMikrotikConnectionFactory
    {
        /// <summary>Returns the fixed connection regardless of the requested options.</summary>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connectionOptions,
            CancellationToken cancellationToken) =>
            Task.FromResult(connection);
    }

    /// <summary>Device-info reader that forwards to <see cref="TrackingOps"/>.</summary>
    private sealed class DeviceInfoReaderStub(TrackingOps ops) : IMikrotikDeviceInfoReader
    {
        /// <summary>Forwards the read to the wrapped <see cref="TrackingOps"/>.</summary>
        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            ops.ReadDeviceInfoAsync(connection, host, cancellationToken);
    }

    /// <summary>
    /// Credential resolver that returns fixed <c>admin</c>/<c>secret</c> credentials for every job.
    /// </summary>
    private sealed class StaticCredentials : IRouterCredentialResolver
    {
        /// <summary>Returns the fixed test credentials.</summary>
        public ValueTask<RouterCredentials?> ResolveAsync(
            MikrotikScheduledJob job,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<RouterCredentials?>(new RouterCredentials("admin", "secret"));
    }

    /// <summary>Compatibility assessor that always reports ready-for-upgrade against RouterOS 7.23.8.</summary>
    private sealed class ReadyAssessor : IDeviceCompatibilityAssessor
    {
        /// <summary>Returns a compatible, ready-for-upgrade assessment for the given router IP and time.</summary>
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

    /// <summary>Job execution completer that records the terminal state and never requeues or retries.</summary>
    private sealed class CompleterStub : IRouterJobExecutionCompleter
    {
        /// <summary>Terminal state passed to the most recent completion; null if none.</summary>
        public ScheduledJobTerminalState? LastState { get; private set; }

        /// <summary>Always false: imports are never aborted in these tests.</summary>
        public bool IsImportAbortedOrStopping(Guid importId) => false;

        /// <summary>Always false: requeueing is not supported by the stub.</summary>
        public bool TryRequeueDispatchedJob(object entryToken) => false;

        /// <summary>Always false: transient retries are not scheduled by the stub.</summary>
        public bool TryScheduleTransientRetry(
            object entryToken,
            JobRetryFailureCategory category,
            string reason,
            Exception? error = null) =>
            false;

        /// <summary>Stores the terminal state in <see cref="LastState"/>.</summary>
        public void CompleteDispatchedJob(
            object entryToken,
            ScheduledJobTerminalState state,
            TimeSpan? executionDuration = null,
            Exception? error = null) =>
            LastState = state;
    }

    /// <summary>
    /// Router operations fake that reports version 6.49.10 until rebooted and the post-upgrade version afterwards,
    /// recording fetch and reboot calls; scripts and schedulers are always empty.
    /// </summary>
    private class TrackingOps : IMikrotikRouterOperations
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

        /// <summary>
        /// When true (default), the fetched file is reported as 4 bytes (matching the stub package); otherwise 0
        /// bytes, simulating a failed transfer.
        /// </summary>
        public bool FetchReturnsMatchingSize { get; init; } = true;

        /// <summary>RouterOS version reported after a reboot.</summary>
        public string PostUpgradeVersion { get; set; } = "6.49.23";

        /// <summary>System identity reported by the fake router.</summary>
        public string Identity { get; set; } = "ACE01.POP001.001.core";

        /// <summary>
        /// Returns device info that reports 6.49.10 before reboot and <see cref="PostUpgradeVersion"/> afterwards.
        /// </summary>
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

        /// <summary>
        /// Returns a single main package (<c>routeros-mipsbe</c> or <c>routeros</c>) at the version implied by the
        /// reboot state.
        /// </summary>
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

        /// <summary>
        /// Reports the file with a size of 4 or 0 bytes according to <see cref="FetchReturnsMatchingSize"/>.
        /// </summary>
        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, FetchReturnsMatchingSize ? 4 : 0));

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

        /// <summary>
        /// Marks the reboot as requested; overridable so subclasses can change post-reboot behaviour.
        /// </summary>
        public virtual Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
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

        /// <summary>No-op: pretends the RouterBOARD firmware upgrade command was accepted.</summary>
        public Task UpgradeRouterboardAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Tracking fake for the two-stage flow: the first reboot reports RouterOS 6.49.23 and later reboots report
    /// 7.23.8.
    /// </summary>
    private sealed class TwoStageTrackingOps : TrackingOps
    {
        /// <summary>Number of reboots requested so far.</summary>
        private int _reboots;

        /// <summary>
        /// Counts the reboot and sets the post-upgrade version for that stage before delegating to the base
        /// implementation.
        /// </summary>
        public override Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            _reboots++;
            PostUpgradeVersion = _reboots == 1 ? "6.49.23" : "7.23.8";
            return base.RebootAsync(connection, cancellationToken);
        }
    }

    /// <summary>Backup fake that always succeeds with remote cleanup completed.</summary>
    private sealed class SucceedingBackup : IDeviceBackupService
    {
        /// <summary>Returns success with remote cleanup completed.</summary>
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
