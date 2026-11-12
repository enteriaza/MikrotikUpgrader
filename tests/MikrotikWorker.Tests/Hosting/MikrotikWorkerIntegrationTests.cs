// -----------------------------------------------------------------------------
// File: MikrotikWorkerIntegrationTests.cs
//
// Description:
// Broader host integration tests covering DI composition and end-to-end worker host
// startup with TestHostFactory.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Tests.Hosting;
/// <summary>
/// Covers host integration and DI composition.
/// </summary>
public sealed class MikrotikWorkerIntegrationTests
{
    /// <summary>Upper bound for each host start/stop and wait in these tests.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Enabled=false with RecordingMikrotikApiClient.
    /// Expects CallCount remains 0 after a short idle period.
    /// </summary>
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

    /// <summary>
    /// Enabled=true with credentials but no scheduled jobs.
    /// Expects idle/no-workload log and zero API client calls.
    /// </summary>
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

    /// <summary>
    /// Default production DI graph.
    /// Expects API client, AppSettingsRouterCredentialResolver, placeholder credentials,
    /// port 8728, Enabled=false, and Worker.ApiClient same instance.
    /// </summary>
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

    /// <summary>
    /// ConnectionTimeout=00:00:00.
    /// Expects OptionsValidationException citing ConnectionTimeout.
    /// </summary>
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
