// -----------------------------------------------------------------------------
// File: DeviceLogRoutingAndPromotionTests.cs
//
// Description:
// Device log routing, promotion, and sink behaviour tests.
//
// -----------------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.Scheduling;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace MikrotikWorker.Tests.Logging;
/// <summary>
/// Tests per-device logging: <c>DeviceLogWriter</c> append and key promotion (pending IP-based
/// file to identity-based file), <c>DeviceLogScope</c> lifecycle, identity-safe file naming, and
/// the Serilog filters that decide whether an event goes to a device file, the service log, or
/// both.
/// The recurring invariants are that no line is lost, duplicated or reordered during promotion,
/// existing content is never truncated, and one router's events never reach another router's
/// file.
/// </summary>
public sealed class DeviceLogRoutingAndPromotionTests
{
    /// <summary>
    /// Lines written before, during and after promotion of a pending key all reach the identity file.
    /// Preconditions: A, B go to "pending_10.0.0.1.log"; the key is promoted to "MikroTik.log"; C is
    /// written to the identity key; "D-via-stale-key" is then written to the old pending key.
    /// Expected: The pending file is gone and the identity file holds A, B, C and D exactly once
    /// each, in that order.
    /// Invariant: Promotion preserves order, does not duplicate or drop events, and redirects late
    /// writes to a stale key.
    /// </summary>
    [Fact]
    public async Task Promote_PreservesOrder_WithoutDuplication_OrLoss()
    {
        await using DeviceLogHarness harness = await DeviceLogHarness.CreateAsync();
        string pending = "pending_10.0.0.1.log";
        string identity = "MikroTik.log";

        harness.Writer.Emit(pending, CreateEvent("A"));
        harness.Writer.Emit(pending, CreateEvent("B"));
        harness.Writer.PromoteKey(pending, identity);
        harness.Writer.Emit(identity, CreateEvent("C"));
        harness.Writer.Emit(pending, CreateEvent("D-via-stale-key")); // must redirect after promote

        await harness.Writer.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(harness.Writer.DeviceLogDirectory, pending)));
        string content = await File.ReadAllTextAsync(Path.Combine(harness.Writer.DeviceLogDirectory, identity));
        Assert.Contains("A", content, StringComparison.Ordinal);
        Assert.Contains("B", content, StringComparison.Ordinal);
        Assert.Contains("C", content, StringComparison.Ordinal);
        Assert.Contains("D-via-stale-key", content, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(content, "A"));
        Assert.Equal(1, CountOccurrences(content, "B"));
        Assert.Equal(1, CountOccurrences(content, "C"));
        Assert.Equal(1, CountOccurrences(content, "D-via-stale-key"));

        int indexA = content.IndexOf('A', StringComparison.Ordinal);
        int indexB = content.IndexOf('B', StringComparison.Ordinal);
        int indexC = content.IndexOf('C', StringComparison.Ordinal);
        int indexD = content.IndexOf("D-via-stale-key", StringComparison.Ordinal);
        Assert.True(indexA < indexB && indexB < indexC && indexC < indexD);
    }

    /// <summary>
    /// A scope learns the router identity after it already logged lines under its pending key.
    /// Preconditions: A scope for 198.18.0.83 with a fake clock; "pre-identity-line" is emitted, then
    /// <c>SetRouterIdentity("MikroTik")</c>, then "post-identity-line".
    /// Expected: "MikroTik_198.18.0.83.log" contains the "Device: MikroTik" header, then the
    /// pre-identity line, then the post-identity line.
    /// Invariant: The session header always precedes the promoted pending lines.
    /// </summary>
    [Fact]
    public async Task ScopePromotion_WritesHeaderBeforePendingLines()
    {
        await using DeviceLogHarness harness = await DeviceLogHarness.CreateAsync();
        MikrotikScheduledJob job = CreateJob("198.18.0.83", "LIVE-TEST");
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 19, 0, 0, TimeSpan.Zero));

        using (DeviceLogScope scope = DeviceLogScope.Begin(job, harness.Writer, new DeviceLogIdentityRegistry(), time))
        {
            harness.Writer.Emit(scope.DeviceLogKey, CreateEvent("pre-identity-line"));
            scope.SetRouterIdentity("MikroTik");
            harness.Writer.Emit(scope.DeviceLogKey, CreateEvent("post-identity-line"));
        }

        await harness.Writer.StopAsync(CancellationToken.None);
        string content = await File.ReadAllTextAsync(Path.Combine(harness.Writer.DeviceLogDirectory, "MikroTik_198.18.0.83.log"));
        int header = content.IndexOf("Device: MikroTik", StringComparison.Ordinal);
        int pending = content.IndexOf("pre-identity-line", StringComparison.Ordinal);
        int after = content.IndexOf("post-identity-line", StringComparison.Ordinal);
        Assert.True(header >= 0 && pending >= 0 && after >= 0);
        Assert.True(header < pending, "Session header must precede promoted pending lines");
        Assert.True(pending < after);
    }

    /// <summary>
    /// 40 writers log to a pending key while a promotion is issued, and 40 more then alternate
    /// between the old and new keys.
    /// Preconditions: A tiny queue capacity of 8 with blocking disabled, so writers can contend with
    /// the consumer.
    /// Expected: The identity file contains all 80 "pre-NN" and "post-NN" messages, and any residual
    /// pending file is empty.
    /// Invariant: Concurrent promotion never loses events and leaves no content stranded in the
    /// pending file.
    /// </summary>
    [Fact]
    public async Task Promote_ConcurrentWritesDuringPromotion_AreRedirectedSafely()
    {
        await using DeviceLogHarness harness = await DeviceLogHarness.CreateAsync(capacity: 8, blockWhenFull: false);
        string pending = "pending_10.0.0.2.log";
        string identity = "CORE-01.log";

        Task writers = Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            harness.Writer.Emit(pending, CreateEvent($"pre-{i:D2}"));
            await Task.Yield();
        }));

        await Task.Yield();
        harness.Writer.PromoteKey(pending, identity);

        Task post = Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            harness.Writer.Emit(i % 2 == 0 ? pending : identity, CreateEvent($"post-{i:D2}"));
            await Task.Yield();
        }));

        await writers;
        await post;
        await harness.Writer.StopAsync(CancellationToken.None);

        string identityPath = Path.Combine(harness.Writer.DeviceLogDirectory, identity);
        Assert.True(File.Exists(identityPath));
        string content = await File.ReadAllTextAsync(identityPath);
        for (int i = 0; i < 40; i++)
        {
            Assert.Contains($"pre-{i:D2}", content, StringComparison.Ordinal);
            Assert.Contains($"post-{i:D2}", content, StringComparison.Ordinal);
        }

        string pendingPath = Path.Combine(harness.Writer.DeviceLogDirectory, pending);
        if (File.Exists(pendingPath))
        {
            // Any residual pending file must be empty; content belongs in the identity log.
            Assert.True(string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(pendingPath)));
        }
    }

    /// <summary>
    /// The identity log already exists from an earlier session and a new pending log is promoted onto
    /// it.
    /// Preconditions: "historical-line" is written to the identity key and "session-line" to a
    /// pending key before promotion.
    /// Expected: The identity file contains both lines with the historical line first.
    /// Invariant: Promotion appends and never truncates an existing device log.
    /// </summary>
    [Fact]
    public async Task Promote_AppendsToExistingIdentityFile_NeverTruncates()
    {
        await using DeviceLogHarness harness = await DeviceLogHarness.CreateAsync();
        string identity = "MikroTik.log";
        string pending = "pending_198.18.0.83.log";

        harness.Writer.Emit(identity, CreateEvent("historical-line"));
        harness.Writer.Emit(pending, CreateEvent("session-line"));
        harness.Writer.PromoteKey(pending, identity);
        await harness.Writer.StopAsync(CancellationToken.None);

        string content = await File.ReadAllTextAsync(Path.Combine(harness.Writer.DeviceLogDirectory, identity));
        Assert.Contains("historical-line", content, StringComparison.Ordinal);
        Assert.Contains("session-line", content, StringComparison.Ordinal);
        Assert.True(
            content.IndexOf("historical-line", StringComparison.Ordinal)
            < content.IndexOf("session-line", StringComparison.Ordinal));
    }

    /// <summary>
    /// A scope ends without ever discovering the router identity.
    /// Preconditions: A scope for 10.9.9.9 (CSV name "CSV-NAME") emits one line and is disposed with
    /// no <c>SetRouterIdentity</c> call.
    /// Expected: "pending_10.9.9.9.log" exists with the line, and no "CSV-NAME.log" is created.
    /// Invariant: Evidence from failed connections stays in the IP-based pending file and is not
    /// renamed by guesswork.
    /// </summary>
    [Fact]
    public async Task IdentityDiscoveryFailure_LeavesPendingFileIntact()
    {
        await using DeviceLogHarness harness = await DeviceLogHarness.CreateAsync();
        MikrotikScheduledJob job = CreateJob("10.9.9.9", "CSV-NAME");
        using (DeviceLogScope scope = DeviceLogScope.Begin(job, harness.Writer, new DeviceLogIdentityRegistry()))
        {
            harness.Writer.Emit(scope.DeviceLogKey, CreateEvent("connected-before-identity"));
            // Identity never discovered — no PromoteKey / SetRouterIdentity.
        }

        await harness.Writer.StopAsync(CancellationToken.None);
        string pendingPath = Path.Combine(harness.Writer.DeviceLogDirectory, "pending_10.9.9.9.log");
        Assert.True(File.Exists(pendingPath));
        string content = await File.ReadAllTextAsync(pendingPath);
        Assert.Contains("connected-before-identity", content, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(harness.Writer.DeviceLogDirectory, "CSV-NAME.log")));
    }

    /// <summary>
    /// The writer is stopped while appends and a promotion are still queued.
    /// Preconditions: 25 lines go to a pending key, the key is promoted to "DRAIN.log", and one more
    /// line is written before <c>StopAsync</c>.
    /// Expected: The identity file contains the first line, the last of the 25 lines and the
    /// post-promotion line; <c>WriteFailureCount</c> is 0.
    /// Invariant: Shutdown drains the queue completely before returning.
    /// </summary>
    [Fact]
    public async Task ShutdownDrainsQueuedPromoteAndAppends()
    {
        await using DeviceLogHarness harness = await DeviceLogHarness.CreateAsync();
        string pending = "pending_10.0.0.8.log";
        string identity = "DRAIN.log";
        for (int i = 0; i < 25; i++)
        {
            harness.Writer.Emit(pending, CreateEvent($"line-{i}"));
        }

        harness.Writer.PromoteKey(pending, identity);
        harness.Writer.Emit(identity, CreateEvent("after-promote"));
        await harness.Writer.StopAsync(CancellationToken.None);

        string content = await File.ReadAllTextAsync(Path.Combine(harness.Writer.DeviceLogDirectory, identity));
        Assert.Contains("line-0", content, StringComparison.Ordinal);
        Assert.Contains("line-24", content, StringComparison.Ordinal);
        Assert.Contains("after-promote", content, StringComparison.Ordinal);
        Assert.Equal(0, harness.Writer.WriteFailureCount);
    }

    /// <summary>
    /// Promotion cannot append because the destination path is a directory.
    /// Preconditions: A pending line exists and a directory named "FAILPROMOTE.log" is created in the
    /// device folder.
    /// Expected: <c>WriteFailureCount</c> is at least 1, the pending file still exists with its
    /// content, and the directory is untouched.
    /// Invariant: A failed promotion keeps the source evidence and reports through the main service
    /// log, never the device log.
    /// </summary>
    [Fact]
    public async Task PromoteFailure_IsCounted_LeavesPendingSource_AndUsesServiceLogPath()
    {
        await using DeviceLogHarness harness = await DeviceLogHarness.CreateAsync();
        string pending = "pending_10.0.0.7.log";
        string identity = "FAILPROMOTE.log";
        harness.Writer.Emit(pending, CreateEvent("before-promote"));
        // Occupy the destination path with a directory so append fails.
        string identityPath = Path.Combine(harness.Writer.DeviceLogDirectory, identity);
        Directory.CreateDirectory(identityPath);
        harness.Writer.PromoteKey(pending, identity);
        await harness.Writer.StopAsync(CancellationToken.None);

        // Failure is counted for service-log visibility (RecordWriteFailure → Log.Warning, no DeviceLogKey).
        Assert.True(harness.Writer.WriteFailureCount >= 1);
        // Append failed before delete, so pending evidence remains.
        Assert.True(File.Exists(Path.Combine(harness.Writer.DeviceLogDirectory, pending)));
        Assert.Contains(
            "before-promote",
            await File.ReadAllTextAsync(Path.Combine(harness.Writer.DeviceLogDirectory, pending)),
            StringComparison.Ordinal);
        Assert.True(Directory.Exists(identityPath));
    }

    /// <summary>
    /// Two routers report the same identity from different addresses.
    /// Preconditions: Identity "MikroTik" at 198.18.0.83 and 198.18.0.84, resolved through one
    /// identity registry.
    /// Expected: The file names are "MikroTik_198.18.0.83.log" and "MikroTik_198.18.0.84.log".
    /// Invariant: Duplicate identities never share a log file.
    /// </summary>
    [Fact]
    public void CollisionSafeIdentity_DoesNotShareLogBetweenDifferentIps()
    {
        DeviceLogIdentityRegistry registry = new();
        string a = DeviceLogNaming.ResolveIdentityLogFileName(registry, "MikroTik", IPAddress.Parse("198.18.0.83"));
        string b = DeviceLogNaming.ResolveIdentityLogFileName(registry, "MikroTik", IPAddress.Parse("198.18.0.84"));
        Assert.Equal("MikroTik_198.18.0.83.log", a);
        Assert.Equal("MikroTik_198.18.0.84.log", b);
        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Classifies five events against <c>DeviceLogFilters</c>.
    /// Preconditions: A plain device-detail info event, a service-only event, a service-summary
    /// event, a device warning, and an event with no device key.
    /// Expected: Only the device-detail info event is excluded from the service log; the others are
    /// kept; only the service-only event is reported by <c>IsServiceOnlyEvent</c>.
    /// Invariant: Warnings, summaries and service-only events always remain visible in the service
    /// log.
    /// </summary>
    [Fact]
    public void DeviceDetail_IsExcludedFromServiceFilter_ServiceOnlyAndSummaryAreNot()
    {
        LogEvent detail = CreateEvent(
            "Connected successfully",
            Prop("DeviceLogKey", "MikroTik.log"));
        LogEvent serviceOnly = CreateEvent(
            "Router job started",
            Prop("DeviceLogKey", "MikroTik.log"),
            Prop("ServiceOnly", true));
        LogEvent summary = CreateEvent(
            "Final upgrade outcome",
            Prop("DeviceLogKey", "MikroTik.log"),
            Prop("ServiceSummary", true));
        LogEvent warning = new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Warning,
            null,
            new MessageTemplateParser().Parse("device warning"),
            [Prop("DeviceLogKey", "MikroTik.log")]);
        LogEvent plainService = CreateEvent("CSV detected");

        Assert.True(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog(detail));
        Assert.False(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog(serviceOnly));
        Assert.False(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog(summary));
        Assert.False(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog(warning));
        Assert.False(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog(plainService));
        Assert.True(DeviceLogFilters.IsServiceOnlyEvent(serviceOnly));
        Assert.False(DeviceLogFilters.IsServiceOnlyEvent(detail));
    }

    /// <summary>
    /// A real Serilog pipeline with a device file sink and a collecting service sink routes four
    /// kinds of event.
    /// Preconditions: Markers are logged inside a <c>DeviceLogScope</c>: service-only, device detail,
    /// a service summary, and a warning; the identity is set between them.
    /// Expected: The device file has the detail, summary and warning but not the service-only marker;
    /// the service sink has the service-only, summary and warning markers but not the detail marker.
    /// Invariant: Detail lines live only in the device log, service-only lines only in the service
    /// log.
    /// </summary>
    [Fact]
    public async Task EndToEndRouting_DeviceDetailDoesNotEnterServiceLog_ServiceOnlyDoesNotEnterDeviceLog()
    {
        string logRoot = Directory.CreateTempSubdirectory("route-e2e-").FullName;
        SerilogOptions options = new()
        {
            LogDirectory = logRoot,
            FileName = "application-.log",
            WriteToConsole = false,
            WriteToFile = false,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
            DeviceLogAsyncCapacity = 1000,
            DeviceLogAsyncBlockWhenFull = true,
            MinimumLevel = "Information",
        };

        DeviceLogWriter writer = new(Options.Create(options));
        await writer.StartAsync(CancellationToken.None);
        CollectingSink serviceSink = new();

        LoggerConfiguration configuration = new();
        SerilogConfigurator.Configure(
            configuration,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new FakeEnv(),
            options,
            writer);
        configuration.WriteTo.Logger(lc => lc
            .Filter.ByExcluding(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog)
            .WriteTo.Sink(serviceSink));
        using Logger logger = configuration.CreateLogger();

        MikrotikScheduledJob job = CreateJob("198.18.0.83", "LIVE-TEST");
        using (DeviceLogScope scope = DeviceLogScope.Begin(job, writer, new DeviceLogIdentityRegistry()))
        {
            using (DeviceLogging.BeginServiceOnly())
            {
                logger.Information("SERVICE_ONLY_MARKER ImportId={ImportId}", job.ImportId);
            }

            logger.Information("DEVICE_DETAIL_MARKER Connected successfully");
            scope.SetRouterIdentity("MikroTik");

            using (DeviceLogging.BeginServiceSummary())
            {
                logger.Information("SERVICE_SUMMARY_MARKER Final upgrade outcome: UpgradeSucceeded");
            }

            logger.Warning("DEVICE_WARNING_MARKER something odd");
        }

        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();
        await logger.DisposeAsync();

        string deviceLog = await File.ReadAllTextAsync(Path.Combine(writer.DeviceLogDirectory, "MikroTik_198.18.0.83.log"));
        string[] serviceMessages = serviceSink.Events.Select(static e => e.RenderMessage()).ToArray();

        Assert.Contains("DEVICE_DETAIL_MARKER", deviceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("SERVICE_ONLY_MARKER", deviceLog, StringComparison.Ordinal);
        Assert.Contains("SERVICE_SUMMARY_MARKER", deviceLog, StringComparison.Ordinal);
        Assert.Contains("DEVICE_WARNING_MARKER", deviceLog, StringComparison.Ordinal);

        Assert.DoesNotContain(serviceMessages, static m => m.Contains("DEVICE_DETAIL_MARKER", StringComparison.Ordinal));
        Assert.Contains(serviceMessages, static m => m.Contains("SERVICE_ONLY_MARKER", StringComparison.Ordinal));
        Assert.Contains(serviceMessages, static m => m.Contains("SERVICE_SUMMARY_MARKER", StringComparison.Ordinal));
        Assert.Contains(serviceMessages, static m => m.Contains("DEVICE_WARNING_MARKER", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two devices with the same identity but different IPs log to their own files.
    /// Preconditions: Keys come from <c>ResolveIdentityLogFileName</c> for "SAME" at 10.0.0.1 and
    /// 10.0.0.2; "ONLY_A" and "ONLY_B" are emitted to the respective keys.
    /// Expected: Each file contains only its own marker.
    /// Invariant: Device logs are isolated from each other.
    /// </summary>
    [Fact]
    public async Task DeviceA_EventsNeverAppearInDeviceB_Log()
    {
        await using DeviceLogHarness harness = await DeviceLogHarness.CreateAsync();
        DeviceLogIdentityRegistry registry = new();
        string keyA = DeviceLogNaming.ResolveIdentityLogFileName(registry, "SAME", IPAddress.Parse("10.0.0.1"));
        string keyB = DeviceLogNaming.ResolveIdentityLogFileName(registry, "SAME", IPAddress.Parse("10.0.0.2"));
        harness.Writer.Emit(keyA, CreateEvent("ONLY_A"));
        harness.Writer.Emit(keyB, CreateEvent("ONLY_B"));
        await harness.Writer.StopAsync(CancellationToken.None);

        string a = await File.ReadAllTextAsync(Path.Combine(harness.Writer.DeviceLogDirectory, keyA));
        string b = await File.ReadAllTextAsync(Path.Combine(harness.Writer.DeviceLogDirectory, keyB));
        Assert.Contains("ONLY_A", a, StringComparison.Ordinal);
        Assert.DoesNotContain("ONLY_B", a, StringComparison.Ordinal);
        Assert.Contains("ONLY_B", b, StringComparison.Ordinal);
        Assert.DoesNotContain("ONLY_A", b, StringComparison.Ordinal);
    }

    /// <summary>
    /// The device routing property exists only while a scope is open.
    /// Preconditions: A logger enriched from <c>LogContext</c> writes one event inside
    /// <c>DeviceLogScope.Begin</c> and one after it is disposed.
    /// Expected: The inside event carries the device-log-key property and the outside event does not.
    /// Invariant: Events outside a scope are never routed to a device file.
    /// </summary>
    [Fact]
    public void ScopeDispose_ClearsDeviceRoutingContext()
    {
        MikrotikScheduledJob job = CreateJob("10.0.0.5", "X");
        CollectingSink sink = new();
        using Logger logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using (DeviceLogScope.Begin(job))
        {
            logger.Information("inside-scope");
        }

        logger.Information("outside-scope");

        LogEvent inside = Assert.Single(sink.Events, static e => e.RenderMessage().Contains("inside-scope"));
        LogEvent outside = Assert.Single(sink.Events, static e => e.RenderMessage().Contains("outside-scope"));
        Assert.True(inside.Properties.ContainsKey(DeviceLogNaming.DeviceLogKeyPropertyName));
        Assert.False(outside.Properties.ContainsKey(DeviceLogNaming.DeviceLogKeyPropertyName));
    }

    /// <summary>
    /// Creates a scheduled job whose schedule window is open now.
    /// </summary>
    /// <param name="ip">Router IP address text.</param>
    /// <param name="name">Router name from the CSV row.</param>
    /// <returns>A job for "a.csv" row 2.</returns>
    private static MikrotikScheduledJob CreateJob(string ip, string name) =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = name,
            RouterIp = IPAddress.Parse(ip),
            DateStartUtc = DateTime.UtcNow,
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    /// <summary>
    /// Counts non-overlapping ordinal occurrences of a substring.
    /// </summary>
    /// <param name="haystack">Text to search.</param>
    /// <param name="needle">Substring to count.</param>
    /// <returns>Number of occurrences.</returns>
    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// Builds a scalar Serilog property for test events.
    /// </summary>
    /// <param name="name">Property name.</param>
    /// <param name="value">Scalar value.</param>
    /// <returns>The property.</returns>
    private static LogEventProperty Prop(string name, object value) =>
        new(name, new ScalarValue(value));

    /// <summary>
    /// Creates an Information-level event timestamped now, with the text parsed as its message
    /// template.
    /// </summary>
    /// <param name="message">Message template text.</param>
    /// <param name="properties">Properties to attach, such as the device log key or routing
    /// markers.</param>
    /// <returns>The event.</returns>
    private static LogEvent CreateEvent(string message, params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties: properties);

    /// <summary>
    /// Serilog sink that keeps every event in memory so tests can inspect what reached it.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        /// <summary>
        /// Events received, in arrival order.
        /// </summary>
        public List<LogEvent> Events { get; } = [];

        /// <summary>
        /// Stores the event.
        /// </summary>
        /// <param name="logEvent">Event delivered by Serilog.</param>
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    /// <summary>
    /// Minimal development <c>IHostEnvironment</c> that satisfies
    /// <c>SerilogConfigurator.Configure</c> without a real host.
    /// </summary>
    private sealed class FakeEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        /// <summary>
        /// Environment name; "Development".
        /// </summary>
        public string EnvironmentName { get; set; } = "Development";

        /// <summary>
        /// Application name; "MikrotikWorker.Tests".
        /// </summary>
        public string ApplicationName { get; set; } = "MikrotikWorker.Tests";

        /// <summary>
        /// Content root; the test assembly's base directory.
        /// </summary>
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        /// <summary>
        /// Empty file provider, since no configuration files are read from the content root.
        /// </summary>
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>
    /// Owns a started <c>DeviceLogWriter</c> rooted in a temporary folder and cleans it up
    /// afterwards.
    /// </summary>
    private sealed class DeviceLogHarness : IAsyncDisposable
    {
        /// <summary>
        /// Wraps an already started writer.
        /// </summary>
        /// <param name="writer">Started device log writer.</param>
        private DeviceLogHarness(DeviceLogWriter writer) => Writer = writer;

        /// <summary>
        /// Writer under test; its <c>DeviceLogDirectory</c> is where tests read the resulting files.
        /// </summary>
        public DeviceLogWriter Writer { get; }

        /// <summary>
        /// Creates and starts a writer with device logging enabled in a new temp folder.
        /// </summary>
        /// <param name="capacity">Async queue capacity.</param>
        /// <param name="blockWhenFull">Whether writers block (true) or overflow (false) when the queue is
        /// full.</param>
        /// <returns>A running harness.</returns>
        public static async Task<DeviceLogHarness> CreateAsync(int capacity = 1000, bool blockWhenFull = true)
        {
            string logRoot = Directory.CreateTempSubdirectory("devlog-audit-").FullName;
            DeviceLogWriter writer = new(Options.Create(new SerilogOptions
            {
                LogDirectory = logRoot,
                DeviceLogEnabled = true,
                DeviceLogDirectory = "Devices",
                DeviceLogAsyncCapacity = capacity,
                DeviceLogAsyncBlockWhenFull = blockWhenFull,
            }));
            await writer.StartAsync(CancellationToken.None);
            return new DeviceLogHarness(writer);
        }

        /// <summary>
        /// Stops and disposes the writer, then deletes the temporary log root, ignoring cleanup failures.
        /// </summary>
        /// <returns>A task that completes once cleanup has finished.</returns>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Writer.StopAsync(CancellationToken.None);
            }
            catch
            {
            }

            Writer.Dispose();
            try
            {
                Directory.Delete(Path.GetDirectoryName(Writer.DeviceLogDirectory)!, recursive: true);
            }
            catch
            {
            }
        }
    }
}
