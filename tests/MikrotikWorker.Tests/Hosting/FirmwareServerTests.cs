// -----------------------------------------------------------------------------
// File: FirmwareServerTests.cs
//
// Description:
// HTTP tests that /firmware_files serves .npk only and rejects non-.npk content via
// the static-file middleware configured in MikrotikWorkerHost.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Tests.Hosting;
/// <summary>
/// Covers .npk-only firmware static file serving.
/// </summary>
public sealed class FirmwareServerTests
{
    /// <summary>Upper bound for each host start/stop and HTTP wait in these tests.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Sample firmware payload served and compared byte-for-byte.</summary>
    private static readonly byte[] SampleFirmware = "mikrotik-firmware-package"u8.ToArray();

    /// <summary>
    /// Listening IP 127.0.0.1 and free port.
    /// Expects options surface that IP and a port in 1–65535.
    /// </summary>
    [Fact]
    public void ValidListeningConfiguration_IsAccepted()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                ipAddress: "127.0.0.1");

            IOptions<ListeningOptions> options = app.Services.GetRequiredService<IOptions<ListeningOptions>>();

            Assert.Equal("127.0.0.1", options.Value.IpAddress);
            Assert.InRange(options.Value.Port, 1, 65535);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    /// <summary>
    /// IpAddress=not-a-valid-ip at CreateApplication.
    /// Expects OptionsValidationException citing IpAddress.
    /// </summary>
    [Fact]
    public void InvalidIpAddress_IsRejectedAtStartup()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
                () => TestHostFactory.CreateApplication(
                    firmwareRoot,
                    port: 8080,
                    ipAddress: "not-a-valid-ip"));

            Assert.Contains("IpAddress", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    /// <summary>
    /// Port=70000 via configuration.
    /// Expects OptionsValidationException citing Port at BuildApplication.
    /// </summary>
    [Fact]
    public void InvalidPort_IsRejectedAtStartup()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            WebApplicationBuilder builder = TestHostFactory.CreateBuilder(
                firmwareRoot,
                port: 8080,
                additionalConfiguration: new Dictionary<string, string?>
                {
                    [$"{ListeningOptions.SectionName}:Port"] = "70000",
                });

            OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
                () => MikrotikWorkerHost.BuildApplication(builder));

            Assert.Contains("Port", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    /// <summary>
    /// Start on a free port.
    /// Expects listening URI matches port/http scheme; GET / returns 404 (no site root).
    /// </summary>
    [Fact]
    public async Task HttpServer_StartsOnConfiguredEndpoint()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        int port = TestHostFactory.GetFreeTcpPort();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(firmwareRoot, port);
            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                Uri baseAddress = TestHostFactory.GetListeningAddress(app);
                Assert.Equal(port, baseAddress.Port);
                Assert.Equal(Uri.UriSchemeHttp, baseAddress.Scheme);

                using HttpClient client = new() { BaseAddress = baseAddress, Timeout = TestTimeout };
                using HttpResponseMessage response = await client.GetAsync("/");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
    /// Seeded routeros-tile npk under firmware root.
    /// Expects GET /firmware_files/... returns exact sample bytes.
    /// </summary>
    [Fact]
    public async Task ExistingNpkFile_CanBeRetrieved()
    {
        string relativePath = Path.Combine("mikrotik", "routeros-tile-6.49.23.npk");
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot((relativePath, SampleFirmware));
        int port = TestHostFactory.GetFreeTcpPort();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(firmwareRoot, port);
            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                using HttpClient client = CreateClient(app);
                byte[] downloaded = await client.GetByteArrayAsync(
                    "/firmware_files/mikrotik/routeros-tile-6.49.23.npk");

                Assert.Equal(SampleFirmware, downloaded);
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
    /// Empty firmware root.
    /// Expects 404 for a missing .npk path.
    /// </summary>
    [Fact]
    public async Task MissingFirmwareFile_ReturnsNotFound()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        int port = TestHostFactory.GetFreeTcpPort();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(firmwareRoot, port);
            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                using HttpClient client = CreateClient(app);
                using HttpResponseMessage response = await client.GetAsync(
                    "/firmware_files/mikrotik/does-not-exist.npk");

                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
    /// GET /firmware_files/ and /firmware_files/mikrotik/.
    /// Expects 404 — directory listing must not be enabled.
    /// </summary>
    [Fact]
    public async Task DirectoryBrowsing_IsNotEnabled()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot(
            (Path.Combine("mikrotik", "routeros-tile-6.49.23.npk"), SampleFirmware));
        int port = TestHostFactory.GetFreeTcpPort();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(firmwareRoot, port);
            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                using HttpClient client = CreateClient(app);

                using HttpResponseMessage rootResponse = await client.GetAsync("/firmware_files/");
                using HttpResponseMessage folderResponse = await client.GetAsync("/firmware_files/mikrotik/");

                Assert.Equal(HttpStatusCode.NotFound, rootResponse.StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, folderResponse.StatusCode);
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
    /// Successful .npk GET.
    /// Expects Content-Type application/octet-stream.
    /// </summary>
    [Fact]
    public async Task NpkFiles_AreServedAsOctetStream()
    {
        string relativePath = Path.Combine("mikrotik", "sample.npk");
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot((relativePath, SampleFirmware));
        int port = TestHostFactory.GetFreeTcpPort();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(firmwareRoot, port);
            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                using HttpClient client = CreateClient(app);
                using HttpResponseMessage response = await client.GetAsync("/firmware_files/mikrotik/sample.npk");

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(
                    MediaTypeHeaderValue.Parse("application/octet-stream"),
                    response.Content.Headers.ContentType);
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
    /// Host with Worker heartbeat and an .npk file.
    /// Expects heartbeat log and concurrent successful firmware HTTP GET.
    /// </summary>
    [Fact]
    public async Task WorkerAndKestrel_CoexistInSameHost()
    {
        string relativePath = Path.Combine("mikrotik", "sample.npk");
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot((relativePath, SampleFirmware));
        RecordingLoggerProvider loggerProvider = new();
        int port = TestHostFactory.GetFreeTcpPort();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                port,
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
                Assert.Contains(app.Services.GetServices<IHostedService>(), static service => service is Worker);

                await loggerProvider.WaitForAsync(
                    static message => message.Contains("Worker heartbeat", StringComparison.Ordinal),
                    minimumCount: 1,
                    timeout: TestTimeout);

                using HttpClient client = CreateClient(app);
                using HttpResponseMessage response = await client.GetAsync("/firmware_files/mikrotik/sample.npk");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
    /// Firmware root also contains txt/backup/json/log beside an .npk.
    /// Expects only the .npk returns 200; others 404.
    /// </summary>
    [Fact]
    public async Task NonNpkFiles_AreNotServed()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot(
            (Path.Combine("mikrotik", "sample.npk"), SampleFirmware),
            ("readme.txt", "secret"u8.ToArray()),
            ("backup.backup", "backup"u8.ToArray()),
            ("plan.json", "{}"u8.ToArray()),
            ("appsettings.json", "{}"u8.ToArray()),
            ("device.log", "log"u8.ToArray()));
        int port = TestHostFactory.GetFreeTcpPort();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(firmwareRoot, port);
            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                using HttpClient client = CreateClient(app);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/firmware_files/mikrotik/sample.npk")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/firmware_files/readme.txt")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/firmware_files/backup.backup")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/firmware_files/plan.json")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/firmware_files/appsettings.json")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/firmware_files/device.log")).StatusCode);
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
    /// Traversal URLs attempting to reach appsettings outside the firmware mount.
    /// Expects 404 or 400.
    /// </summary>
    [Theory]
    [InlineData("/firmware_files/../appsettings.json")]
    [InlineData("/firmware_files/%2e%2e/appsettings.json")]
    [InlineData("/firmware_files/mikrotik/../../appsettings.json")]
    public async Task PathTraversal_IsRejected(string requestPath)
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot(
            (Path.Combine("mikrotik", "sample.npk"), SampleFirmware));
        int port = TestHostFactory.GetFreeTcpPort();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(firmwareRoot, port);
            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                using HttpClient client = CreateClient(app);
                using HttpResponseMessage response = await client.GetAsync(requestPath);
                Assert.True(
                    response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                    $"Unexpected status {response.StatusCode} for {requestPath}");
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
    /// StopAsync after start.
    /// Expects stopped log and TCP connect to the former port fails.
    /// </summary>
    [Fact]
    public async Task HostShutdown_StopsWorkerAndKestrel()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot(
            (Path.Combine("mikrotik", "sample.npk"), SampleFirmware));
        RecordingLoggerProvider loggerProvider = new();
        int port = TestHostFactory.GetFreeTcpPort();

        try
        {
            await using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                port,
                heartbeatInterval: TimeSpan.FromMilliseconds(40),
                configureLogging: logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(loggerProvider);
                    logging.SetMinimumLevel(LogLevel.Information);
                });

            await app.StartAsync().WaitAsync(TestTimeout);
            Uri baseAddress = TestHostFactory.GetListeningAddress(app);

            await app.StopAsync().WaitAsync(TestTimeout);

            Assert.Contains(
                loggerProvider.Messages,
                static message => message.Contains("MikrotikWorker stopped", StringComparison.Ordinal));

            using TcpClient tcpClient = new();
            await Assert.ThrowsAnyAsync<SocketException>(
                async () => await tcpClient.ConnectAsync(baseAddress.Host, baseAddress.Port));
        }
        finally
        {
            Directory.Delete(firmwareRoot, recursive: true);
        }
    }

    /// <summary>
    /// HttpClient aimed at the app's listening address with TestTimeout.
    /// </summary>
    private static HttpClient CreateClient(WebApplication app) =>
        new()
        {
            BaseAddress = TestHostFactory.GetListeningAddress(app),
            Timeout = TestTimeout,
        };
}
