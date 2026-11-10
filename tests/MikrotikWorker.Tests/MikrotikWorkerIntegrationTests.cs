using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Tests;

public sealed class MikrotikWorkerIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task MikroTikIntegrationDisabled_DoesNotAttemptConnections()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        RecordingMikrotikApiClient recordingClient = new();

        try
        {
            WebApplicationBuilder builder = TestHostFactory.CreateBuilder(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                heartbeatInterval: TimeSpan.FromMilliseconds(40),
                additionalConfiguration: new Dictionary<string, string?>
                {
                    [$"{MikroTikOptions.SectionName}:Enabled"] = "false",
                });

            builder.Services.AddSingleton<IMikrotikApiClient>(recordingClient);

            await using WebApplication app = MikrotikWorkerHost.BuildApplication(builder);
            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                await Task.Delay(120, CancellationToken.None);
                Assert.Equal(0, recordingClient.CallCount);
            }
            finally
            {
                await app.StopAsync().WaitAsync(TestTimeout);
            }
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MikroTikIntegrationEnabledWithoutWorkload_RemainsIdle()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        RecordingMikrotikApiClient recordingClient = new();
        RecordingLoggerProvider loggerProvider = new();

        try
        {
            WebApplicationBuilder builder = TestHostFactory.CreateBuilder(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                heartbeatInterval: TimeSpan.FromMilliseconds(40),
                additionalConfiguration: new Dictionary<string, string?>
                {
                    [$"{MikroTikOptions.SectionName}:Enabled"] = "true",
                    [$"{MikroTikOptions.SectionName}:Username"] = "test-user",
                    [$"{MikroTikOptions.SectionName}:Password"] = "test-pass",
                },
                configureLogging: logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(loggerProvider);
                    logging.SetMinimumLevel(LogLevel.Information);
                });

            builder.Services.AddSingleton<IMikrotikApiClient>(recordingClient);

            await using WebApplication app = MikrotikWorkerHost.BuildApplication(builder);
            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                await loggerProvider.WaitForAsync(
                    static message => message.Contains("no device workload is configured yet", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("Remaining idle", StringComparison.OrdinalIgnoreCase),
                    minimumCount: 1,
                    timeout: TestTimeout);

                Assert.Equal(0, recordingClient.CallCount);
            }
            finally
            {
                await app.StopAsync().WaitAsync(TestTimeout);
            }
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    [Fact]
    public void ProductionRegistration_ResolvesMikroTikServicesWithSharedCredentialPlaceholders()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort());

            IMikrotikApiClient client = app.Services.GetRequiredService<IMikrotikApiClient>();
            IOptions<MikroTikOptions> options = app.Services.GetRequiredService<IOptions<MikroTikOptions>>();
            IRouterCredentialResolver credentials = app.Services.GetRequiredService<IRouterCredentialResolver>();
            Worker worker = app.Services.GetServices<IHostedService>().OfType<Worker>().Single();

            Assert.NotNull(client);
            Assert.False(options.Value.Enabled);
            Assert.Equal(8728, options.Value.Port);
            Assert.False(string.IsNullOrWhiteSpace(options.Value.Username));
            Assert.False(string.IsNullOrEmpty(options.Value.Password));
            Assert.IsType<AppSettingsRouterCredentialResolver>(credentials);
            Assert.Same(client, worker.ApiClient);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    [Fact]
    public void InvalidGlobalMikroTikOptions_PreventStartup()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                additionalConfiguration: new Dictionary<string, string?>
                {
                    [$"{MikroTikOptions.SectionName}:ConnectionTimeout"] = "00:00:00",
                });

            OptionsValidationException exception = Assert.Throws<OptionsValidationException>(app.Start);
            Assert.Contains("ConnectionTimeout", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }
}
