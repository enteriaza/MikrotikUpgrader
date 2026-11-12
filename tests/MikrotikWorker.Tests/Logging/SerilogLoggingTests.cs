using System.Collections.Concurrent;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MikrotikWorker.Tests.Logging;
[Collection("Serilog")]
public sealed class SerilogLoggingTests
{
    [Fact]
    public void RelativeLogDirectory_ResolvesFromAppContextBaseDirectory()
    {
        SerilogOptions options = new() { LogDirectory = "Logs", FileName = "application-.log" };
        string absolute = options.GetAbsoluteLogDirectory();

        Assert.True(Path.IsPathRooted(absolute));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Logs")),
            absolute);
        Assert.StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory),
            absolute,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RollingInterval_DefaultsToDay()
    {
        Assert.Equal(RollingInterval.Day, SerilogConfigurator.ParseRollingInterval("Day"));
        Assert.Equal(RollingInterval.Day, SerilogConfigurator.ParseRollingInterval("not-a-real-interval"));
    }

    [Fact]
    public void MicrosoftILogger_EventsReachSerilog_WithStructuredPropertiesAndExceptions()
    {
        CollectingSink sink = new();
        using Logger serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("Application", "MikrotikWorker.Tests")
            .WriteTo.Sink(sink)
            .CreateLogger();

        using Serilog.Extensions.Logging.SerilogLoggerFactory factory = new(serilogLogger);
        ILogger<SerilogLoggingTests> logger = factory.CreateLogger<SerilogLoggingTests>();

        InvalidOperationException ex = new("boom");
        logger.LogError(
            ex,
            "Failed import {ImportId} for router {RouterName}",
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            "CORE-01");

        LogEvent evt = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Error, evt.Level);
        Assert.NotNull(evt.Exception);
        Assert.Equal("boom", evt.Exception!.Message);
        Assert.True(evt.Properties.ContainsKey("ImportId"));
        Assert.True(evt.Properties.ContainsKey("RouterName"));
        Assert.Contains("CORE-01", evt.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_CreatesApplicationLogFile_AndCapturesRequestLogging()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot(("probe.txt", "ok"u8.ToArray()));
        string logDirectory = Path.Combine(Path.GetTempPath(), "mikrotik-serilog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);

        try
        {
            WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot,
                TestHostFactory.GetFreeTcpPort(),
                additionalConfiguration: new Dictionary<string, string?>
                {
                    ["Logging:Serilog:LogDirectory"] = logDirectory,
                    ["Logging:Serilog:FileName"] = "application-.log",
                    ["Logging:Serilog:WriteToConsole"] = "false",
                    ["Logging:Serilog:WriteToFile"] = "true",
                    ["Logging:Serilog:MinimumLevel"] = "Information",
                });

            await app.StartAsync();
            try
            {
                Uri baseAddress = TestHostFactory.GetListeningAddress(app);
                using HttpClient client = new() { BaseAddress = baseAddress };
                using HttpResponseMessage ok = await client.GetAsync("/firmware_files/probe.txt");
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
                using HttpResponseMessage missing = await client.GetAsync("/firmware_files/missing.bin");
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }

            // Host dispose flushes Serilog; poll briefly for the async file sink.
            string contents = await WaitForLogContentsAsync(
                logDirectory,
                static text => text.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
                               && text.Contains("/firmware_files/probe.txt", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(5));
            Assert.Contains("HTTP", contents, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/firmware_files/probe.txt", contents, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                Directory.Delete(firmwareRoot, true);
            }
            catch
            {
            }

            try
            {
                Directory.Delete(logDirectory, true);
            }
            catch
            {
            }
        }
    }

    private static async Task<string> WaitForLogContentsAsync(
        string logDirectory,
        Func<string, bool> predicate,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        string contents = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            string[] logFiles = Directory.Exists(logDirectory)
                ? Directory.GetFiles(logDirectory, "application-*.log")
                : [];
            contents = string.Join(Environment.NewLine, logFiles.Select(static path =>
            {
                try
                {
                    return File.ReadAllText(path);
                }
                catch (IOException)
                {
                    return string.Empty;
                }
            }));

            if (predicate(contents))
            {
                return contents;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Log contents were not ready before timeout. Observed:{Environment.NewLine}{contents}");
    }

    [Fact]
    public void SerilogOptionsValidator_RejectsInvalidSettings()
    {
        SerilogOptionsValidator validator = new();
        SerilogOptions options = new()
        {
            LogDirectory = " ",
            FileName = "bad|name.log",
            RollingInterval = "Weekly",
            MinimumLevel = "Loud",
            RetainedFileCountLimit = 0,
            FileSizeLimitBytes = 0,
            AsyncBufferCapacity = 0,
        };

        Microsoft.Extensions.Options.ValidateOptionsResult result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.NotEmpty(result.Failures!);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public ConcurrentBag<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
