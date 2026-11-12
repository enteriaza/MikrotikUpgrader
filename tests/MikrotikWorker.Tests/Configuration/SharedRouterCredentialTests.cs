using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Configuration;
public sealed class SharedRouterCredentialTests
{
    private const string SharedUsername = "shared-api-user";
    private const string SharedPassword = "SuperSecretShared!42";

    [Fact]
    public async Task AppSettingsResolver_ReturnsSharedCredentials()
    {
        AppSettingsRouterCredentialResolver resolver = CreateResolver(SharedUsername, SharedPassword);

        RouterCredentials? credentials = await resolver.ResolveAsync(
            CreateJob("192.168.1.1"),
            CancellationToken.None);

        Assert.NotNull(credentials);
        Assert.Equal(SharedUsername, credentials.Username);
        Assert.Equal(SharedPassword, credentials.Password);
    }

    [Fact]
    public async Task AppSettingsResolver_ReturnsIdenticalCredentials_ForDifferentRouterIps()
    {
        AppSettingsRouterCredentialResolver resolver = CreateResolver(SharedUsername, SharedPassword);

        RouterCredentials? first = await resolver.ResolveAsync(CreateJob("10.0.0.1"), CancellationToken.None);
        RouterCredentials? second = await resolver.ResolveAsync(CreateJob("10.0.0.2"), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Username, second.Username);
        Assert.Equal(first.Password, second.Password);
    }

    [Fact]
    public void Validator_RejectsMissingUsername_WhenEnabled()
    {
        ValidateOptionsResult result = new MikroTikOptionsValidator().Validate(
            null,
            new MikroTikOptions
            {
                Enabled = true,
                Username = " ",
                Password = SharedPassword,
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static f => f.Contains("Username", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Failures, static f => f.Contains(SharedPassword, StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsMissingPassword_WhenEnabled()
    {
        ValidateOptionsResult result = new MikroTikOptionsValidator().Validate(
            null,
            new MikroTikOptions
            {
                Enabled = true,
                Username = SharedUsername,
                Password = string.Empty,
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static f => f.Contains("Password", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Failures,
            static f => f.Contains(SharedUsername, StringComparison.Ordinal) && f.Contains('='));
    }

    [Fact]
    public void Validator_AllowsMissingCredentials_WhenDisabled()
    {
        ValidateOptionsResult result = new MikroTikOptionsValidator().Validate(
            null,
            new MikroTikOptions
            {
                Enabled = false,
                Username = string.Empty,
                Password = string.Empty,
                Port = 8728,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                ReconnectInterval = TimeSpan.FromSeconds(30),
            });

        Assert.False(result.Failed);
    }

    [Fact]
    public async Task DisabledIntegration_PreventsConnectionAttempts()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        RecordingConnectionFactory factory = new();
        await using ExecutionHost host = await ExecutionHost.CreateAsync(
            time,
            factory,
            enabled: false,
            username: SharedUsername,
            password: SharedPassword);

        Guid importId = Guid.NewGuid();
        host.Scheduler.RegisterImport(importId, 1);
        await host.Scheduler.ScheduleAsync(
            CreateScheduledJob(importId, time, "192.168.88.1"),
            CancellationToken.None);
        host.Scheduler.SetAcceptedPath(importId, host.AcceptedCsv);

        // PendingCount hits 0 when the job is dispatched; IsTracked clears only after completion.
        await WaitForAsync(() => !host.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));
        Assert.Empty(factory.Connections);
        Assert.Equal(0, host.Scheduler.PendingCount);
    }

    [Fact]
    public async Task Executor_PassesSharedHostPortUsernamePassword()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        RecordingConnectionFactory factory = new();
        await using ExecutionHost host = await ExecutionHost.CreateAsync(
            time,
            factory,
            enabled: true,
            username: SharedUsername,
            password: SharedPassword,
            port: 8730);

        Guid importId = Guid.NewGuid();
        host.Scheduler.RegisterImport(importId, 1);
        await host.Scheduler.ScheduleAsync(
            CreateScheduledJob(importId, time, "192.168.55.10"),
            CancellationToken.None);
        host.Scheduler.SetAcceptedPath(importId, host.AcceptedCsv);

        await WaitForAsync(() => factory.Connections.Count >= 1, TimeSpan.FromSeconds(5));
        MikrotikConnectionOptions connection = factory.Connections.First();
        Assert.Equal("192.168.55.10", connection.Host);
        Assert.Equal(SharedUsername, connection.Username);
        Assert.Equal(SharedPassword, connection.Password);
        Assert.Null(connection.Port);
        Assert.Equal(8730, host.MikroTik.Port);
    }

    [Fact]
    public async Task ConnectionFailure_ProducesFailedTerminalOutcome_AndCompletesImport()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        ThrowingMikrotikConnectionFactory failing = new(new IOException("connect refused"));
        // Retry disabled (max 0): first unsuccessful attempt is terminal — preserves credential-path coverage.
        await using ExecutionHost host = await ExecutionHost.CreateAsync(
            time,
            failing,
            enabled: true,
            username: SharedUsername,
            password: SharedPassword,
            jobRetryOptions: new JobRetryOptions { MaximumUnsuccessfulAttempts = 0 });

        Guid importId = Guid.NewGuid();
        host.Scheduler.RegisterImport(importId, 1);
        await host.Scheduler.ScheduleAsync(
            CreateScheduledJob(importId, time, "10.1.1.1"),
            CancellationToken.None);
        host.Scheduler.SetAcceptedPath(importId, host.AcceptedCsv);

        await WaitForAsync(() => !host.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));
        await WaitForAsync(
            () => Directory.EnumerateFiles(host.CompletedDir, "*.csv").Any(),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CredentialResolver_PropagatesCancellation()
    {
        AppSettingsRouterCredentialResolver resolver = CreateResolver(SharedUsername, SharedPassword);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(CreateJob("10.0.0.1"), cts.Token).AsTask());
    }

    [Fact]
    public async Task Executor_NeverLogsCredentials()
    {
        RecordingLoggerProvider logs = new();
        FakeTimeProvider time = new(new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero));
        RecordingConnectionFactory factory = new();
        await using ExecutionHost host = await ExecutionHost.CreateAsync(
            time,
            factory,
            enabled: true,
            username: SharedUsername,
            password: SharedPassword,
            executorLogger: logs.CreateLogger("RouterJobExecutor"));

        Guid importId = Guid.NewGuid();
        host.Scheduler.RegisterImport(importId, 1);
        await host.Scheduler.ScheduleAsync(
            CreateScheduledJob(importId, time, "192.168.9.9"),
            CancellationToken.None);
        host.Scheduler.SetAcceptedPath(importId, host.AcceptedCsv);

        await WaitForAsync(() => factory.Connections.Count >= 1, TimeSpan.FromSeconds(5));
        await WaitForAsync(() => !host.Scheduler.IsTracked(importId), TimeSpan.FromSeconds(5));

        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Credential resolution successful", StringComparison.Ordinal));
        Assert.Contains(
            logs.Messages,
            static m => m.Contains("Connected successfully", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            static m => m.Contains(SharedPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            static m => m.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AppSettingsResolver_ReturnsNull_WhenCredentialsBlank()
    {
        AppSettingsRouterCredentialResolver resolver = CreateResolver(string.Empty, string.Empty);
        RouterCredentials? credentials = await resolver.ResolveAsync(
            CreateJob("10.0.0.1"),
            CancellationToken.None);
        Assert.Null(credentials);
    }

    [Fact]
    public void Startup_RejectsEnabledWithoutUsername()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                additionalConfiguration: new Dictionary<string, string?>
                {
                    [$"{MikroTikOptions.SectionName}:Enabled"] = "true",
                    [$"{MikroTikOptions.SectionName}:Username"] = "",
                    [$"{MikroTikOptions.SectionName}:Password"] = SharedPassword,
                });

            OptionsValidationException exception = Assert.Throws<OptionsValidationException>(app.Start);
            Assert.Contains("Username", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SharedPassword, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    [Fact]
    public void Startup_RejectsEnabledWithoutPassword()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                additionalConfiguration: new Dictionary<string, string?>
                {
                    [$"{MikroTikOptions.SectionName}:Enabled"] = "true",
                    [$"{MikroTikOptions.SectionName}:Username"] = SharedUsername,
                    [$"{MikroTikOptions.SectionName}:Password"] = "",
                });

            OptionsValidationException exception = Assert.Throws<OptionsValidationException>(app.Start);
            Assert.Contains("Password", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(SharedUsername, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    [Fact]
    public void ProductionRegistration_ResolvesAppSettingsCredentialResolver()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort());

            IRouterCredentialResolver resolver = app.Services.GetRequiredService<IRouterCredentialResolver>();
            IOptions<MikroTikOptions> options = app.Services.GetRequiredService<IOptions<MikroTikOptions>>();

            Assert.IsType<AppSettingsRouterCredentialResolver>(resolver);
            Assert.False(options.Value.Enabled);
            Assert.Equal("your-router-username", options.Value.Username);
            Assert.Equal("your-router-password", options.Value.Password);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    private static AppSettingsRouterCredentialResolver CreateResolver(string username, string password) =>
        new(Options.Create(new MikroTikOptions
        {
            Enabled = true,
            Username = username,
            Password = password,
        }));

    private static MikrotikScheduledJob CreateJob(string ip) =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "batch.csv",
            RowNumber = 2,
            RouterName = "CORE-01",
            RouterIp = IPAddress.Parse(ip),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    private static MikrotikScheduledJob CreateScheduledJob(Guid importId, FakeTimeProvider time, string ip) =>
        new()
        {
            ImportId = importId,
            SourceFileName = "batch.csv",
            RowNumber = 2,
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

    private sealed class RecordingConnectionFactory : IMikrotikConnectionFactory
    {
        public ConcurrentBag<MikrotikConnectionOptions> Connections { get; } = [];

        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Connections.Add(connection);
            return Task.FromResult<ITikConnection>(FakeMikrotikConnections.CreateFullyPopulated());
        }
    }

    private sealed class ExecutionHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource _schedulerCts = new();
        private readonly CancellationTokenSource _executionCts;
        private readonly Task _schedulerTask;
        private readonly Task _executionTask;
        private readonly string _root;

        private ExecutionHost(
            InMemoryJobScheduler scheduler,
            MikroTikOptions mikroTik,
            string acceptedCsv,
            string completedDir,
            string root,
            Task executionTask,
            CancellationTokenSource executionCts)
        {
            Scheduler = scheduler;
            MikroTik = mikroTik;
            AcceptedCsv = acceptedCsv;
            CompletedDir = completedDir;
            _root = root;
            _executionTask = executionTask;
            _executionCts = executionCts;
            _schedulerTask = scheduler.RunAsync(_schedulerCts.Token);
        }

        public InMemoryJobScheduler Scheduler { get; }

        public MikroTikOptions MikroTik { get; }

        public string AcceptedCsv { get; }

        public string CompletedDir { get; }

        public static Task<ExecutionHost> CreateAsync(
            FakeTimeProvider time,
            IMikrotikConnectionFactory connectionFactory,
            bool enabled,
            string username,
            string password,
            int port = 8728,
            ILogger? executorLogger = null,
            JobRetryOptions? jobRetryOptions = null)
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
                Enabled = enabled,
                Username = username,
                Password = password,
                Port = port,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                ReconnectInterval = TimeSpan.FromSeconds(30),
            };

            ILogger<RouterJobExecutor> logger = executorLogger as ILogger<RouterJobExecutor>
                ?? (executorLogger is null
                    ? NullLogger<RouterJobExecutor>.Instance
                    : new LoggerAdapter(executorLogger));

            (InMemoryJobScheduler scheduler, _, Task executionTask, CancellationTokenSource executionCts) =
                SchedulerExecutionTestHost.Create(
                    options,
                    paths,
                    new FileSystemScheduledJobFileMover(),
                    time,
                    connectionFactory: connectionFactory,
                    mikroTikEnabled: enabled,
                    mikroTikOptions: mikroTik,
                    executorLogger: logger,
                    jobRetryOptions: jobRetryOptions);

            return Task.FromResult(new ExecutionHost(
                scheduler,
                mikroTik,
                acceptedCsv,
                paths.Completed,
                root,
                executionTask,
                executionCts));
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
