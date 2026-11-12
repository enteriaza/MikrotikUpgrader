// -----------------------------------------------------------------------------
// File: SharedRouterCredentialTests.cs
//
// Description:
// Tests that AppSettingsRouterCredentialResolver returns shared Mikrotik options
// credentials for every job and never reads passwords from CSV fields.
//
// -----------------------------------------------------------------------------

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
/// <summary>
/// Validates shared appsettings credential resolution for scheduled jobs.
/// </summary>
public sealed class SharedRouterCredentialTests
{
    /// <summary>
    /// Shared API username configured in <see cref="MikroTikOptions"/> for all routers.
    /// </summary>
    private const string SharedUsername = "shared-api-user";

    /// <summary>
    /// Shared API password configured in <see cref="MikroTikOptions"/>; asserted never to appear in logs or error text.
    /// </summary>
    private const string SharedPassword = "SuperSecretShared!42";

    /// <summary>
    /// Resolver backed by shared username/password options.
    /// Expects ResolveAsync returns those credentials for a job.
    /// </summary>
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

    /// <summary>
    /// Two jobs with different RouterIp values.
    /// Expects identical username/password — credentials are not per-device from CSV.
    /// </summary>
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

    /// <summary>
    /// Enabled MikroTikOptions with blank username.
    /// Expects failure citing Username and must not echo the password in failure text.
    /// </summary>
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

    /// <summary>
    /// Enabled options with empty password.
    /// Expects failure citing Password without dumping credential assignment text.
    /// </summary>
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

    /// <summary>
    /// Enabled=false with empty username/password.
    /// Expects validation success.
    /// </summary>
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

    /// <summary>
    /// Scheduler execution host with MikroTik Enabled=false.
    /// Expects import completes with zero connection factory calls.
    /// </summary>
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

    /// <summary>
    /// Enabled host with shared credentials and options Port=8730.
    /// Expects CreateAsync receives job host + shared user/password; connection Port null
    /// (global port comes from options).
    /// </summary>
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

    /// <summary>
    /// Throwing factory with MaximumUnsuccessfulAttempts=0.
    /// Expects import untracked and Completed CSV present (terminal failure path).
    /// </summary>
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

    /// <summary>
    /// ResolveAsync with an already-cancelled token.
    /// Expects OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task CredentialResolver_PropagatesCancellation()
    {
        AppSettingsRouterCredentialResolver resolver = CreateResolver(SharedUsername, SharedPassword);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(CreateJob("10.0.0.1"), cts.Token).AsTask());
    }

    /// <summary>
    /// Recording logger on RouterJobExecutor during a successful connect path.
    /// Expects success messages without the shared password or Password= fragments.
    /// </summary>
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

    /// <summary>
    /// Resolver with empty username and password.
    /// Expects null credentials (caller must treat as unresolved).
    /// </summary>
    [Fact]
    public async Task AppSettingsResolver_ReturnsNull_WhenCredentialsBlank()
    {
        AppSettingsRouterCredentialResolver resolver = CreateResolver(string.Empty, string.Empty);
        RouterCredentials? credentials = await resolver.ResolveAsync(
            CreateJob("10.0.0.1"),
            CancellationToken.None);
        Assert.Null(credentials);
    }

    /// <summary>
    /// Host build with Enabled=true and missing username.
    /// Expects startup/options validation failure.
    /// </summary>
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

    /// <summary>
    /// Host build with Enabled=true and missing password.
    /// Expects startup/options validation failure.
    /// </summary>
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

    /// <summary>
    /// Production DI registration.
    /// Expects IRouterCredentialResolver resolves to AppSettingsRouterCredentialResolver
    /// with placeholder username/password and Enabled=false.
    /// </summary>
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

    /// <summary>
    /// AppSettingsRouterCredentialResolver over Enabled MikroTikOptions with the given user/password.
    /// </summary>
    private static AppSettingsRouterCredentialResolver CreateResolver(string username, string password) =>
        new(Options.Create(new MikroTikOptions
        {
            Enabled = true,
            Username = username,
            Password = password,
        }));

    /// <summary>
    /// Minimal MikrotikScheduledJob for resolver unit tests (due window, given IP).
    /// </summary>
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

    /// <summary>
    /// Due job bound to an import id and FakeTimeProvider clock for execution-host tests.
    /// </summary>
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

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    /// <summary>
    /// Records CreateAsync connection options and returns a populated fake connection.
    /// </summary>
    private sealed class RecordingConnectionFactory : IMikrotikConnectionFactory
    {
        /// <summary>
        /// Connection options passed to each connect attempt, collected in a thread-safe bag because execution
        /// workers call the factory concurrently. Tests inspect it to confirm which host, port and shared credentials
        /// every attempt used.
        /// </summary>
        public ConcurrentBag<MikrotikConnectionOptions> Connections { get; } = [];

        /// <summary>
        /// Records the connection options and returns a fully populated fake connection.
        /// </summary>
        /// <param name="connection">Connection options for the attempted router.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <returns>A completed task containing the fake connection.</returns>
        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Connections.Add(connection);
            return Task.FromResult<ITikConnection>(FakeMikrotikConnections.CreateFullyPopulated());
        }
    }

    /// <summary>
    /// Scheduler + execution workers with injectable MikroTik options/credentials and connection factory.
    /// </summary>
    private sealed class ExecutionHost : IAsyncDisposable
    {
        /// <summary>
        /// Cancels the scheduler run loop during disposal.
        /// </summary>
        private readonly CancellationTokenSource _schedulerCts = new();

        /// <summary>
        /// Cancels the router execution workers during disposal.
        /// </summary>
        private readonly CancellationTokenSource _executionCts;

        /// <summary>
        /// Task running the scheduler loop.
        /// </summary>
        private readonly Task _schedulerTask;

        /// <summary>
        /// Task running the router execution workers.
        /// </summary>
        private readonly Task _executionTask;

        /// <summary>
        /// Temporary ScheduledJobs root deleted on disposal.
        /// </summary>
        private readonly string _root;

        /// <summary>
        /// Initializes the host state and starts the scheduler run loop.
        /// </summary>
        /// <param name="scheduler">Scheduler under test.</param>
        /// <param name="mikroTik">MikroTik options used by the executor.</param>
        /// <param name="acceptedCsv">Path of the placeholder Accepted CSV.</param>
        /// <param name="completedDir">Completed folder path.</param>
        /// <param name="root">Temporary ScheduledJobs root to delete on disposal.</param>
        /// <param name="executionTask">Task running the execution workers.</param>
        /// <param name="executionCts">Cancellation source that stops the execution workers.</param>
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

        /// <summary>
        /// In-memory scheduler under test; jobs are enqueued through it and executed by the workers this host starts.
        /// </summary>
        public InMemoryJobScheduler Scheduler { get; }

        /// <summary>
        /// MikroTik options (shared username and password, API port and enabled flag) supplied to the executor, so
        /// tests can compare the configured values with the options each connection attempt received.
        /// </summary>
        public MikroTikOptions MikroTik { get; }

        /// <summary>
        /// Path of the placeholder Accepted CSV that the scheduled jobs are registered against.
        /// </summary>
        public string AcceptedCsv { get; }

        /// <summary>
        /// Completed folder inside the temporary ScheduledJobs tree; finished CSVs are archived here, so tests can
        /// tell whether an import finished.
        /// </summary>
        public string CompletedDir { get; }

        /// <summary>
        /// Creates the temp ScheduledJobs tree and starts the scheduler loop plus execution workers with the given MikroTik settings.
        /// </summary>
        /// <param name="time">Fake clock driving scheduling.</param>
        /// <param name="connectionFactory">Connection factory used by the router job executor.</param>
        /// <param name="enabled">Whether MikroTik integration is enabled.</param>
        /// <param name="username">Shared router username.</param>
        /// <param name="password">Shared router password.</param>
        /// <param name="port">MikroTik API port set in the global options.</param>
        /// <param name="executorLogger">Optional logger for <see cref="RouterJobExecutor"/>; a no-op logger is used when <see langword="null"/>.</param>
        /// <param name="jobRetryOptions">Optional retry delays and limits.</param>
        /// <returns>A started host; dispose it to stop loops and clean up.</returns>
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

        /// <summary>
        /// Stops the scheduler and execution workers and deletes the temporary directory.
        /// </summary>
        /// <returns>A task that completes when disposal has finished.</returns>
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

        /// <summary>
        /// Adapts a non-generic <see cref="ILogger"/> to <c>ILogger&lt;RouterJobExecutor&gt;</c> for credential-log assertions.
        /// </summary>
        /// <param name="inner">Logger that receives all forwarded calls.</param>
        private sealed class LoggerAdapter(ILogger inner) : ILogger<RouterJobExecutor>
        {
            /// <summary>
            /// Begins a logical scope on the inner logger.
            /// </summary>
            /// <typeparam name="TState">Type of the scope state.</typeparam>
            /// <param name="state">Scope state to push.</param>
            /// <returns>A disposable that ends the scope, or <see langword="null"/> if the inner logger returns none.</returns>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                inner.BeginScope(state);

            /// <summary>
            /// Checks whether the inner logger is enabled for a level.
            /// </summary>
            /// <param name="logLevel">Level to check.</param>
            /// <returns><see langword="true"/> when the inner logger would emit at that level.</returns>
            public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

            /// <summary>
            /// Forwards a log entry to the inner logger.
            /// </summary>
            /// <typeparam name="TState">Type of the log state.</typeparam>
            /// <param name="logLevel">Entry severity.</param>
            /// <param name="eventId">Event identifier.</param>
            /// <param name="state">Log state object.</param>
            /// <param name="exception">Associated exception, if any.</param>
            /// <param name="formatter">Formats the state and exception into message text.</param>
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
