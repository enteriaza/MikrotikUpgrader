// -----------------------------------------------------------------------------
// File: TestHostFactory.cs
//
// Description:
// Factory helpers that build WebApplication / host test servers with production
// MikrotikWorkerHost composition and overridable options/fakes.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Configuration;

namespace MikrotikWorker.Tests.Infrastructure.Hosts;
/// <summary>
/// Builds test hosts with production composition and overrides.
/// </summary>
internal static class TestHostFactory
{
    /// <summary>Finds a currently unused loopback TCP port by briefly binding and releasing a listener.</summary>
    /// <returns>A port number that was free at the time of the call.</returns>
    public static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Creates a temp firmware directory populated with the supplied files.</summary>
    /// <param name="files">Relative paths and byte content to write beneath the new root.</param>
    /// <returns>Full path of the temp firmware root.</returns>
    public static string CreateFirmwareRoot(params (string RelativePath, byte[] Content)[] files)
    {
        string root = Directory.CreateTempSubdirectory("mikrotik-firmware-").FullName;

        foreach ((string relativePath, byte[] content) in files)
        {
            string fullPath = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, content);
        }

        return root;
    }

    /// <summary>Creates a temp scheduled-jobs root containing Processing, Accepted, Completed and Rejected folders.</summary>
    /// <returns>Full path of the temp jobs root.</returns>
    public static string CreateScheduledJobsRoot()
    {
        string root = Directory.CreateTempSubdirectory("mikrotik-jobs-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "Processing"));
        Directory.CreateDirectory(Path.Combine(root, "Accepted"));
        Directory.CreateDirectory(Path.Combine(root, "Completed"));
        Directory.CreateDirectory(Path.Combine(root, "Rejected"));
        return root;
    }

    /// <summary>
    /// Creates a production-composed <see cref="WebApplicationBuilder"/> with in-memory test configuration
    /// (MikroTik disabled, fast polling intervals, isolated log directory).
    /// </summary>
    /// <param name="firmwareRoot">Firmware directory to serve.</param>
    /// <param name="port">Listening port.</param>
    /// <param name="heartbeatInterval">Worker heartbeat interval; defaults to 50 ms.</param>
    /// <param name="ipAddress">Listening IP address.</param>
    /// <param name="configureLogging">
    /// Custom logging setup; when null, production Serilog configuration is applied instead.
    /// </param>
    /// <param name="additionalConfiguration">Configuration entries that override the defaults.</param>
    /// <param name="scheduledJobsRoot">Scheduled-jobs root; defaults to a new temp root.</param>
    /// <returns>The configured builder, not yet built.</returns>
    public static WebApplicationBuilder CreateBuilder(
        string firmwareRoot,
        int port,
        TimeSpan? heartbeatInterval = null,
        string ipAddress = "127.0.0.1",
        Action<ILoggingBuilder>? configureLogging = null,
        IDictionary<string, string?>? additionalConfiguration = null,
        string? scheduledJobsRoot = null)
    {
        WebApplicationBuilder builder = MikrotikWorkerHost.CreateApplicationBuilder();
        string jobsRoot = scheduledJobsRoot ?? CreateScheduledJobsRoot();

        string testLogDirectory = Path.Combine(
            Path.GetTempPath(),
            "mikrotik-test-logs",
            Guid.NewGuid().ToString("N"));

        Dictionary<string, string?> configuration = new()
        {
            [$"{ListeningOptions.SectionName}:IpAddress"] = ipAddress,
            [$"{ListeningOptions.SectionName}:Port"] = port.ToString(),
            [$"{FirmwareFilesOptions.SectionName}:Path"] = firmwareRoot,
            [$"{WorkerOptions.SectionName}:HeartbeatInterval"] =
                (heartbeatInterval ?? TimeSpan.FromMilliseconds(50)).ToString(),
            [$"{ScheduledJobsOptions.SectionName}:Path"] = jobsRoot,
            [$"{ScheduledJobsOptions.SectionName}:ScanInterval"] = "00:10:00",
            [$"{ScheduledJobsOptions.SectionName}:FileReadyCheckInterval"] = "00:00:00.050",
            [$"{ScheduledJobsOptions.SectionName}:FileReadyTimeout"] = "00:00:05",
            [$"{CsvOptions.SectionName}:Culture"] = "en-ZA",
            [$"{CsvOptions.SectionName}:TimeZoneId"] = "Africa/Johannesburg",
            [$"{MikroTikOptions.SectionName}:Enabled"] = "false",
            [$"{MikroTikOptions.SectionName}:Username"] = "your-router-username",
            [$"{MikroTikOptions.SectionName}:Password"] = "your-router-password",
            [$"{MikroTikOptions.SectionName}:Port"] = "8728",
            // Deployment-supplied firmware URL (not the removed lab default). Required when Enabled=true.
            ["FirmwareUpgrade:AdvertisedBaseUrl"] = "http://198.18.0.1:8080/firmware_files",
            ["Logging:Serilog:LogDirectory"] = testLogDirectory,
            ["Logging:Serilog:FileName"] = "application-.log",
            ["Logging:Serilog:MinimumLevel"] = "Information",
            ["Logging:Serilog:WriteToConsole"] = "false",
            ["Logging:Serilog:WriteToFile"] = "true",
        };

        if (additionalConfiguration is not null)
        {
            foreach ((string key, string? value) in additionalConfiguration)
            {
                configuration[key] = value;
            }
        }

        builder.Configuration.AddInMemoryCollection(configuration);

        // RecordingLogger tests replace MEL providers and must not call UseSerilog (it owns ILoggerFactory).
        // Production/Program and Serilog tests use ConfigureSerilog instead.
        if (configureLogging is not null)
        {
            configureLogging(builder.Logging);
        }
        else
        {
            MikrotikWorkerHost.ConfigureSerilog(builder);
        }

        return builder;
    }

    /// <summary>Builds the application from <see cref="CreateBuilder"/> using production host composition.</summary>
    /// <param name="firmwareRoot">Firmware directory to serve.</param>
    /// <param name="port">Listening port.</param>
    /// <param name="heartbeatInterval">Worker heartbeat interval; defaults to 50 ms.</param>
    /// <param name="ipAddress">Listening IP address.</param>
    /// <param name="configureLogging">Custom logging setup; when null, production Serilog is used.</param>
    /// <param name="additionalConfiguration">Configuration entries that override the defaults.</param>
    /// <param name="scheduledJobsRoot">Scheduled-jobs root; defaults to a new temp root.</param>
    /// <returns>The built, not yet started, application.</returns>
    public static WebApplication CreateApplication(
        string firmwareRoot,
        int port,
        TimeSpan? heartbeatInterval = null,
        string ipAddress = "127.0.0.1",
        Action<ILoggingBuilder>? configureLogging = null,
        IDictionary<string, string?>? additionalConfiguration = null,
        string? scheduledJobsRoot = null)
    {
        WebApplicationBuilder builder = CreateBuilder(
            firmwareRoot,
            port,
            heartbeatInterval,
            ipAddress,
            configureLogging,
            additionalConfiguration,
            scheduledJobsRoot);

        return MikrotikWorkerHost.BuildApplication(builder);
    }

    /// <summary>Reads the first address the started server is listening on.</summary>
    /// <param name="app">A started application.</param>
    /// <returns>The first registered listening address.</returns>
    /// <exception cref="InvalidOperationException">When the server addresses feature is missing or has no addresses.</exception>
    public static Uri GetListeningAddress(WebApplication app)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Server addresses feature is not available.");

        string address = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("No listening addresses were registered.");

        return new Uri(address);
    }
}
