// -----------------------------------------------------------------------------
// File: WorkerTests.cs
//
// Description:
// Tests Worker heartbeat behaviour and Mikrotik:Enabled logging without opening
// device connections.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Tests.Hosting;
/// <summary>
/// Covers Worker heartbeat and Mikrotik enabled logging.
/// </summary>
public sealed class WorkerTests
{
    /// <summary>Upper bound for each host start/stop and wait in these tests.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Default TestHostFactory application.
    /// Expects Worker registered as IHostedService and HeartbeatInterval from test host config (50ms).
    /// </summary>
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

    /// <summary>
    /// HeartbeatInterval=TimeSpan.Zero.
    /// Expects OptionsValidationException citing HeartbeatInterval on Start.
    /// </summary>
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

    /// <summary>
    /// 40ms heartbeat with recording logger.
    /// Expects ≥2 Worker heartbeat lines plus started/stopped lifecycle messages.
    /// </summary>
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

    /// <summary>
    /// 30s heartbeat interval; StopAsync after first heartbeat within TestTimeout.
    /// Expects MikrotikWorker stopped without waiting out the full delay.
    /// </summary>
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
