// -----------------------------------------------------------------------------
// File: SerilogLoggingTests.cs
//
// Description:
// Tests SerilogConfigurator binding, sink enablement, and device-detail exclusion from
// service sinks under SerilogLoggingCollection isolation.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MikrotikWorker.Tests.Logging;
/// <summary>
/// Covers SerilogConfigurator and sink filtering.
/// </summary>
[Collection("Serilog")]
public sealed class SerilogLoggingTests
{
    /// <summary>
    /// Scenario: <c>SerilogOptions</c> is configured with the relative <c>LogDirectory</c> value <c>Logs</c> and
    /// <c>GetAbsoluteLogDirectory()</c> is called.
    /// Expected: The result is a rooted path equal to <c>AppContext.BaseDirectory</c> combined with <c>Logs</c>, and
    /// it starts with the base directory.
    /// Invariant: Relative log directories resolve against the application base directory, not the working directory.
    /// </summary>
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

    /// <summary>
    /// Scenario: <c>SerilogConfigurator.ParseRollingInterval</c> is given <c>Day</c> and an unrecognised value.
    /// Expected: Both inputs yield <c>RollingInterval.Day</c>.
    /// Invariant: An invalid rolling interval falls back to daily rolling instead of throwing.
    /// </summary>
    [Fact]
    public void RollingInterval_DefaultsToDay()
    {
        Assert.Equal(RollingInterval.Day, SerilogConfigurator.ParseRollingInterval("Day"));
        Assert.Equal(RollingInterval.Day, SerilogConfigurator.ParseRollingInterval("not-a-real-interval"));
    }

    /// <summary>
    /// Scenario: An <c>ILogger</c> backed by a Serilog factory logs an error with an exception and the structured
    /// properties <c>ImportId</c> and <c>RouterName</c> (CORE-01) into a collecting sink.
    /// Expected: Exactly one Error event arrives with the original exception (<c>boom</c>), both properties present,
    /// and the rendered message contains <c>CORE-01</c>.
    /// Invariant: Microsoft logging calls keep their level, exception and structured properties when routed through Serilog.
    /// </summary>
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

    /// <summary>
    /// Scenario: A host is started with Serilog writing an application log file to a temporary directory. A client
    /// requests an existing firmware file (probe.npk) and a missing one, then the host is stopped and disposed, which
    /// flushes Serilog; the test polls briefly for the asynchronous file sink.
    /// Expected: The existing file returns 200 and the missing one returns 404, and the log file contains an HTTP
    /// entry that mentions /firmware_files/probe.npk.
    /// Invariant: Served requests are captured in the application log file once the host has flushed on shutdown.
    /// </summary>
    [Fact]
    public async Task Host_CreatesApplicationLogFile_AndCapturesRequestLogging()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot(("probe.npk", "ok"u8.ToArray()));
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
                using HttpResponseMessage ok = await client.GetAsync("/firmware_files/probe.npk");
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
                               && text.Contains("/firmware_files/probe.npk", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(5));
            Assert.Contains("HTTP", contents, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/firmware_files/probe.npk", contents, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Polls the application log files in the directory until the predicate accepts their combined contents, or
    /// throws a timeout exception showing what was observed.
    /// </summary>
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

    /// <summary>
    /// Scenario: <c>SerilogOptionsValidator</c> validates options with a blank log directory, a file name containing
    /// <c>|</c>, an unknown rolling interval and minimum level, and zero retention, size and buffer limits.
    /// Expected: Validation fails and reports at least one failure message.
    /// Invariant: Invalid Serilog settings are rejected at validation time rather than at logger start-up.
    /// </summary>
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

    /// <summary>
    /// Serilog sink that stores every emitted log event in a thread-safe bag for later assertions.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        /// <summary>Log events received by the sink, in no guaranteed order.</summary>
        public ConcurrentBag<LogEvent> Events { get; } = [];

        /// <summary>Stores <paramref name="logEvent"/> for later assertions.</summary>
        /// <param name="logEvent">Event emitted by Serilog.</param>
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
