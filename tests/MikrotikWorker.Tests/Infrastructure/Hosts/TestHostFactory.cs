using MikrotikWorker.Configuration;

namespace MikrotikWorker.Tests.Infrastructure.Hosts;
internal static class TestHostFactory
{
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

    public static string CreateScheduledJobsRoot()
    {
        string root = Directory.CreateTempSubdirectory("mikrotik-jobs-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "Processing"));
        Directory.CreateDirectory(Path.Combine(root, "Accepted"));
        Directory.CreateDirectory(Path.Combine(root, "Completed"));
        Directory.CreateDirectory(Path.Combine(root, "Rejected"));
        return root;
    }

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
