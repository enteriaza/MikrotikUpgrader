namespace MikrotikWorker.Tests.Hosting;
public sealed class WorkerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ProductionRegistration_ResolvesWorkerAsHostedService()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort());

            Worker? worker = app.Services.GetServices<IHostedService>().OfType<Worker>().SingleOrDefault();
            IOptions<WorkerOptions> options = app.Services.GetRequiredService<IOptions<WorkerOptions>>();

            Assert.NotNull(worker);
            Assert.Equal(TimeSpan.FromMilliseconds(50), options.Value.HeartbeatInterval);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    [Fact]
    public void InvalidHeartbeatInterval_PreventsHostStartup()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                heartbeatInterval: TimeSpan.Zero);

            OptionsValidationException exception = Assert.Throws<OptionsValidationException>(app.Start);
            Assert.Contains("HeartbeatInterval", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_EmitsHeartbeatSignals()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        RecordingLoggerProvider loggerProvider = new();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                heartbeatInterval: TimeSpan.FromMilliseconds(40),
                configureLogging: logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(loggerProvider);
                    logging.SetMinimumLevel(LogLevel.Information);
                });

            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                await loggerProvider.WaitForAsync(
                    static message => message.Contains("Worker heartbeat", StringComparison.Ordinal),
                    minimumCount: 2,
                    timeout: TestTimeout);
            }
            finally
            {
                await app.StopAsync().WaitAsync(TestTimeout);
            }

            Assert.Contains(
                loggerProvider.Messages,
                static message => message.Contains("MikrotikWorker started", StringComparison.Ordinal));
            Assert.Contains(
                loggerProvider.Messages,
                static message => message.Contains("MikrotikWorker stopped", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_InterruptsLongHeartbeatDelayPromptly()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        RecordingLoggerProvider loggerProvider = new();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                heartbeatInterval: TimeSpan.FromSeconds(30),
                configureLogging: logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(loggerProvider);
                    logging.SetMinimumLevel(LogLevel.Information);
                });

            await app.StartAsync().WaitAsync(TestTimeout);

            await loggerProvider.WaitForAsync(
                static message => message.Contains("Worker heartbeat", StringComparison.Ordinal),
                minimumCount: 1,
                timeout: TestTimeout);

            await app.StopAsync().WaitAsync(TestTimeout);

            Assert.Contains(
                loggerProvider.Messages,
                static message => message.Contains("MikrotikWorker stopped", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }
}
