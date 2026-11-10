using System.Reflection;
using System.Text;
using Microsoft.Extensions.Hosting;
using MikrotikWorker.Configuration;
using Serilog;
using Serilog.Events;

namespace MikrotikWorker.Logging;

/// <summary>
/// Builds the central application Serilog pipeline (console + rolling file + optional device sink).
/// </summary>
internal static class SerilogConfigurator
{
    internal const string ConsoleOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    internal const string FileOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}";

    public static SerilogOptions BindOptions(IConfiguration configuration)
    {
        SerilogOptions options = new();
        configuration.GetSection(SerilogOptions.SectionName).Bind(options);
        return options;
    }

    public static void Configure(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        IHostEnvironment environment,
        SerilogOptions? options = null,
        DeviceLogWriter? deviceLogWriter = null)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        options ??= BindOptions(configuration);
        LogEventLevel minimumLevel = ParseLevel(options.MinimumLevel);

        string applicationName = configuration["ApplicationName"]
            ?? environment.ApplicationName
            ?? "MikrotikWorker";
        string? version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        loggerConfiguration
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifecycle", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", applicationName)
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            .Enrich.WithProperty("MachineName", Environment.MachineName)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId);

        if (!string.IsNullOrWhiteSpace(version))
        {
            loggerConfiguration.Enrich.WithProperty("ApplicationVersion", version);
        }

        if (options.WriteToConsole)
        {
            loggerConfiguration.WriteTo.Console(
                outputTemplate: ConsoleOutputTemplate,
                standardErrorFromLevel: LogEventLevel.Error);
        }

        if (options.WriteToFile)
        {
            string logDirectory = options.GetAbsoluteLogDirectory();
            Directory.CreateDirectory(logDirectory);
            string logFilePath = Path.Combine(logDirectory, options.FileName);

            // Async wrapper keeps scheduler/HTTP threads off synchronous disk I/O.
            // Bounded buffer; blockWhenFull avoids silent loss under burst load.
            // CloseAndFlush on shutdown drains the buffer (brief delay possible).
            loggerConfiguration.WriteTo.Async(
                configure => configure.File(
                    path: logFilePath,
                    rollingInterval: ParseRollingInterval(options.RollingInterval),
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: options.RollOnFileSizeLimit,
                    shared: true,
                    encoding: Encoding.UTF8,
                    outputTemplate: FileOutputTemplate),
                bufferSize: options.AsyncBufferCapacity,
                blockWhenFull: options.AsyncBlockWhenFull);
        }

        if (options.DeviceLogEnabled && deviceLogWriter is not null)
        {
            loggerConfiguration.WriteTo.Sink(new DeviceLogSink(deviceLogWriter));
        }
    }

    public static Serilog.ILogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
            .CreateBootstrapLogger();

    internal static LogEventLevel ParseLevel(string level) =>
        Enum.TryParse(level, ignoreCase: true, out LogEventLevel parsed)
            ? parsed
            : LogEventLevel.Information;

    internal static RollingInterval ParseRollingInterval(string interval) =>
        Enum.TryParse(interval, ignoreCase: true, out RollingInterval parsed)
            ? parsed
            : RollingInterval.Day;
}
