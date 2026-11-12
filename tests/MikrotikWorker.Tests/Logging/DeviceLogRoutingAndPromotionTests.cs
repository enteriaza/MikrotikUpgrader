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
public sealed class DeviceLogRoutingAndPromotionTests
{
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

    private static LogEventProperty Prop(string name, object value) =>
        new(name, new ScalarValue(value));

    private static LogEvent CreateEvent(string message, params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties: properties);

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class FakeEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "MikrotikWorker.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class DeviceLogHarness : IAsyncDisposable
    {
        private DeviceLogHarness(DeviceLogWriter writer) => Writer = writer;

        public DeviceLogWriter Writer { get; }

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
