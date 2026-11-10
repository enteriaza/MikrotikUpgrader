using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests;

public sealed class RouterPreflightTests
{
    private const string SharedPassword = "PreflightSecret!99";

    [Fact]
    public async Task SuccessfulDeviceInformationRetrieval_MarksJobSucceeded()
    {
        FakeTimeProvider time = CreateTime();
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            FakeMikrotikConnections.CreateFullyPopulated(),
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("192.168.10.1");

        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Contains(logs.Messages, static m => m.Contains("Connection attempt started", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("Connected successfully", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("RouterOS version:", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("Compatibility assessment started", StringComparison.Ordinal));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Assessment result:", StringComparison.Ordinal)
                && m.Contains("ReadyForUpgrade", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("Connection disconnect started", StringComparison.Ordinal));
        Assert.Contains(logs.Messages, static m => m.Contains("Connection disconnected successfully", StringComparison.Ordinal));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Final job outcome", StringComparison.Ordinal)
                && m.Contains("Succeeded", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            static m => m.Contains(SharedPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            static m => m.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingOptionalRouterboardFields_StillSucceed()
    {
        FakeTimeProvider time = CreateTime();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            FakeMikrotikConnections.CreateFullyPopulated(includeRouterboard: false));

        await harness.RunSingleJobAsync("10.0.0.8");
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
    }

    [Fact]
    public async Task MissingRequiredIdentity_FailsPreflight()
    {
        FakeTimeProvider time = CreateTime();
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            FakeMikrotikConnections.CreateMissingIdentity(),
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("10.0.0.9");

        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Pre-flight validation failed", StringComparison.Ordinal)
                && m.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingArchitecture_FailsPreflight()
    {
        FakeTimeProvider time = CreateTime();
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            FakeMikrotikConnections.CreateMissingArchitecture(),
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("10.0.0.10");

        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("architecture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConnectionFailure_FailsJob_WithoutDuplicateAttempts()
    {
        FakeTimeProvider time = CreateTime();
        CountingThrowingFactory failing = new(new IOException("tcp refused"));
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: failing,
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("10.0.0.11");
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Equal(1, failing.CreateCount);
        Assert.Contains(logs.Messages, static m => m.Contains("Connection attempt started", StringComparison.Ordinal));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Router connection failed", StringComparison.Ordinal)
                && m.Contains("Failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthenticationFailure_FailsJob_WithoutDuplicateAttempts()
    {
        FakeTimeProvider time = CreateTime();
        CountingThrowingFactory failing = new(new InvalidOperationException("cannot log in"));
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: failing,
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("10.0.0.16");
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Equal(1, failing.CreateCount);
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Router connection failed", StringComparison.Ordinal)
                && m.Contains("Failed", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, static m => m.Contains(SharedPassword, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InformationRetrievalFailure_DisposesConnection_AndFailsJob()
    {
        FakeTimeProvider time = CreateTime();
        DisposeTrackingConnection tracking = new(FakeMikrotikConnections.CreateInfoRetrievalFailure());
        ScriptedMikrotikConnectionFactory factory = new(_ => tracking);
        RecordingLoggerProvider logs = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory,
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        await harness.RunSingleJobAsync("10.0.0.12");

        Assert.Single(factory.Connections);
        Assert.Equal(1, tracking.DisposeCount);
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Device information retrieval failed", StringComparison.Ordinal)
                || m.Contains("Final job outcome", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisconnectionFailure_FailsJob()
    {
        FakeTimeProvider time = CreateTime();
        FailingDisposeConnection connection = new(FakeMikrotikConnections.CreateFullyPopulated());
        ScriptedMikrotikConnectionFactory factory = new(_ => connection);
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory);

        await harness.RunSingleJobAsync("10.0.0.13");
        Assert.False(harness.Scheduler.IsTracked(harness.LastImportId));
        Assert.True(connection.DisposeAttempted);
    }

    [Fact]
    public async Task CancellationDuringConnection_MarksCancelled()
    {
        FakeTimeProvider time = CreateTime();
        GateBeforeConnectFactory factory = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, time, "10.0.0.14"), CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => factory.Waiting, TimeSpan.FromSeconds(5));
        harness.CancelExecution();
        factory.Release();

        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task CancellationDuringInformationRetrieval_DisposesAndCancels()
    {
        FakeTimeProvider time = CreateTime();
        DisposeTrackingConnection tracking = new(FakeMikrotikConnections.CreateFullyPopulated());
        ScriptedMikrotikConnectionFactory factory = new(_ => tracking);
        GateDeviceInfoReader reader = new();
        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory,
            deviceInfoReader: reader);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 1);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, time, "10.0.0.15"), CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => reader.Waiting, TimeSpan.FromSeconds(5));
        harness.CancelExecution();
        reader.Release();

        await WaitForAsync(() => harness.Scheduler.PendingCount == 0, TimeSpan.FromSeconds(5));
        Assert.True(tracking.DisposeCount >= 1);
    }

    [Fact]
    public void PreflightResult_ConstructsFromDeviceInfo()
    {
        MikrotikDeviceInfo info = new()
        {
            Identity = "EDGE-01",
            Version = "6.49.23 (long-term)",
            ArchitectureName = "mipsbe",
            BoardName = "RB750Gr3",
            Platform = "MikroTik",
            Uptime = "5d",
            RouterboardModel = "RTR750Gr3",
            RouterboardFirmwareType = "mt7621L",
            RouterboardCurrentFirmware = "6.49.23",
            RouterboardUpgradeFirmware = "6.49.23",
            RouterboardFactoryFirmware = "3.41",
        };

        MikrotikPreflightResult result = MikrotikPreflightResult.FromDeviceInfo(
            "198.18.0.83",
            info,
            new DateTimeOffset(2026, 11, 10, 16, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(250));

        Assert.Equal(MikrotikPreflightStatus.Succeeded, result.Status);
        Assert.Equal(MikrotikConnectionStatus.Succeeded, result.ConnectionStatus);
        Assert.Equal(MikrotikInformationStatus.Succeeded, result.InformationStatus);
        Assert.Equal("198.18.0.83", result.RouterIp);
        Assert.Equal("EDGE-01", result.RouterIdentity);
        Assert.Equal("6.49.23 (long-term)", result.RouterOsVersion);
        Assert.Equal("mipsbe", result.Architecture);
        Assert.Equal("RB750Gr3", result.BoardName);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task FailedAndSuccessfulPreflight_CompleteImportBatch()
    {
        FakeTimeProvider time = CreateTime();
        int calls = 0;
        ScriptedMikrotikConnectionFactory factory = new(_ =>
        {
            int n = Interlocked.Increment(ref calls);
            return n == 1
                ? FakeMikrotikConnections.CreateInfoRetrievalFailure()
                : FakeMikrotikConnections.CreateFullyPopulated();
        });

        await using PreflightHarness harness = await PreflightHarness.CreateAsync(
            time,
            connectionFactory: factory);

        Guid importId = Guid.NewGuid();
        harness.Scheduler.RegisterImport(importId, 2);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, time, "10.0.0.21", row: 2), CancellationToken.None);
        await harness.Scheduler.ScheduleAsync(CreateJob(importId, time, "10.0.0.22", row: 3), CancellationToken.None);
        harness.Scheduler.SetAcceptedPath(importId, harness.AcceptedCsv);

        await WaitForAsync(() => !harness.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));
        await WaitForAsync(
            () => Directory.EnumerateFiles(harness.CompletedDir, "*.csv").Any(),
            TimeSpan.FromSeconds(5));
        Assert.Equal(2, factory.Connections.Count);
    }

    private static FakeTimeProvider CreateTime() =>
        new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));

    private static MikrotikScheduledJob CreateJob(
        Guid importId,
        FakeTimeProvider time,
        string ip,
        int row = 2) =>
        new()
        {
            ImportId = importId,
            SourceFileName = "batch.csv",
            RowNumber = row,
            RouterName = "CORE-01",
            RouterIp = IPAddress.Parse(ip),
            DateStartUtc = time.GetUtcNow().UtcDateTime.AddMinutes(-1),
            DateStopUtc = time.GetUtcNow().UtcDateTime.AddHours(1),
        };

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private sealed class CountingThrowingFactory(Exception exception) : IMikrotikConnectionFactory
    {
        public int CreateCount { get; private set; }

        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromException<ITikConnection>(exception);
        }
    }

    private sealed class GateBeforeConnectFactory : IMikrotikConnectionFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCount { get; private set; }

        public bool Waiting => _entered.Task.IsCompleted;

        public void Release() => _release.TrySetResult();

        public async Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return FakeMikrotikConnections.CreateFullyPopulated();
        }
    }

    private sealed class GateDeviceInfoReader : IMikrotikDeviceInfoReader
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Waiting => _entered.Task.IsCompleted;

        public void Release() => _release.TrySetResult();

        public async Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new MikrotikDeviceInfo
            {
                Identity = "late",
                Version = "1.0",
                ArchitectureName = "arm",
            };
        }
    }

    private sealed class FailingDisposeConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        public bool DisposeAttempted { get; private set; }

        public override void Dispose()
        {
            DisposeAttempted = true;
            throw new IOException("disconnect failed");
        }
    }

    private sealed class PreflightHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _schedulerCts = new();
        private readonly CancellationTokenSource _executionCts;
        private readonly FakeTimeProvider _time;
        private readonly Task _schedulerTask;
        private readonly Task _executionTask;
        private readonly string _root;

        private PreflightHarness(
            InMemoryJobScheduler scheduler,
            FakeTimeProvider time,
            string acceptedCsv,
            string completedDir,
            string root,
            Task executionTask,
            CancellationTokenSource executionCts)
        {
            Scheduler = scheduler;
            _time = time;
            AcceptedCsv = acceptedCsv;
            CompletedDir = completedDir;
            _root = root;
            _executionTask = executionTask;
            _executionCts = executionCts;
            _schedulerTask = scheduler.RunAsync(_schedulerCts.Token);
        }

        public InMemoryJobScheduler Scheduler { get; }

        public string AcceptedCsv { get; }

        public string CompletedDir { get; }

        public Guid LastImportId { get; private set; }

        public void CancelExecution() => _executionCts.Cancel();

        public static Task<PreflightHarness> CreateAsync(
            FakeTimeProvider time,
            ITikConnection? connection = null,
            IMikrotikConnectionFactory? connectionFactory = null,
            IMikrotikDeviceInfoReader? deviceInfoReader = null,
            ILogger? executorLogger = null)
        {
            string root = TestHostFactory.CreateScheduledJobsRoot();
            ScheduledJobsOptions options = new()
            {
                Path = root,
                MaximumPendingJobs = 100,
                MaxConcurrentRouterJobs = 12,
            };
            ScheduledJobsPaths paths = new(Options.Create(options));
            paths.EnsureCreated();
            string acceptedCsv = Path.Combine(paths.Accepted, "batch.csv");
            File.WriteAllText(acceptedCsv, "placeholder");

            MikroTikOptions mikroTik = new()
            {
                Enabled = true,
                Username = "admin",
                Password = SharedPassword,
                Port = 8728,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                ReconnectInterval = TimeSpan.FromSeconds(30),
            };

            IMikrotikConnectionFactory factory = connectionFactory
                ?? new ScriptedMikrotikConnectionFactory(_ => connection ?? FakeMikrotikConnections.CreateFullyPopulated());

            ILogger<RouterJobExecutor> logger = executorLogger as ILogger<RouterJobExecutor>
                ?? (executorLogger is null
                    ? NullLogger<RouterJobExecutor>.Instance
                    : new LoggerAdapter(executorLogger));

            (InMemoryJobScheduler scheduler, _, Task executionTask, CancellationTokenSource hostCts) =
                SchedulerExecutionTestHost.Create(
                    options,
                    paths,
                    new FileSystemScheduledJobFileMover(),
                    time,
                    connectionFactory: factory,
                    deviceInfoReader: deviceInfoReader,
                    mikroTikEnabled: true,
                    mikroTikOptions: mikroTik,
                    executorLogger: logger);

            return Task.FromResult(new PreflightHarness(
                scheduler,
                time,
                acceptedCsv,
                paths.Completed,
                root,
                executionTask,
                hostCts));
        }

        public async Task RunSingleJobAsync(string ip)
        {
            Guid importId = Guid.NewGuid();
            LastImportId = importId;
            Scheduler.RegisterImport(importId, 1);
            await Scheduler.ScheduleAsync(CreateJob(importId, _time, ip), CancellationToken.None);
            Scheduler.SetAcceptedPath(importId, AcceptedCsv);
            await WaitForAsync(() => !Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            _schedulerCts.Cancel();
            _executionCts.Cancel();
            try
            {
                await _schedulerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            try
            {
                await _executionTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            await Scheduler.DisposeAsync();
            _schedulerCts.Dispose();
            _executionCts.Dispose();
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        private sealed class LoggerAdapter(ILogger inner) : ILogger<RouterJobExecutor>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
