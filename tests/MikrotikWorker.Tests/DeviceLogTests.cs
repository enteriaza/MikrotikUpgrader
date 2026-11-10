using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.Scheduling;
using Serilog.Events;
using Serilog.Parsing;

namespace MikrotikWorker.Tests;

public sealed class DeviceLogTests
{
    [Fact]
    public void DeviceLogNaming_IdentityStem_MatchesBackupSanitisation()
    {
        IPAddress ip = IPAddress.Parse("198.18.0.83");
        Assert.Equal("MikroTik_198.18.0.83.log", DeviceLogNaming.BuildDeviceLogFileName("MikroTik", ip));
        Assert.Equal("MikroTik_198.18.0.83", DeviceLogNaming.BuildDeviceStem("MikroTik", ip));
        Assert.Equal("MikroTik", DeviceLogNaming.Sanitize("MikroTik"));
    }

    [Fact]
    public void DeviceLogNaming_PendingUsesIpUntilIdentityKnown()
    {
        Assert.Equal("pending_198.18.0.83.log", DeviceLogNaming.BuildPendingLogFileName(IPAddress.Parse("198.18.0.83")));
    }

    [Fact]
    public void DeviceLogNaming_CollisionUsesIdentityPlusIp()
    {
        DeviceLogIdentityRegistry registry = new();
        string first = DeviceLogNaming.ResolveIdentityLogFileName(
            registry,
            "MikroTik",
            IPAddress.Parse("198.18.0.83"));
        string second = DeviceLogNaming.ResolveIdentityLogFileName(
            registry,
            "MikroTik",
            IPAddress.Parse("198.18.0.84"));

        Assert.Equal("MikroTik_198.18.0.83.log", first);
        Assert.Equal("MikroTik_198.18.0.84.log", second);
    }

    [Fact]
    public void DeviceLogNaming_SameIdentityAndIp_ReusesStem()
    {
        DeviceLogIdentityRegistry registry = new();
        string first = DeviceLogNaming.ResolveIdentityLogFileName(
            registry,
            "MikroTik",
            IPAddress.Parse("198.18.0.83"));
        string second = DeviceLogNaming.ResolveIdentityLogFileName(
            registry,
            "MikroTik",
            IPAddress.Parse("198.18.0.83"));
        Assert.Equal("MikroTik_198.18.0.83.log", first);
        Assert.Equal("MikroTik_198.18.0.83.log", second);
    }

    [Fact]
    public void DeviceLogNaming_SanitizesUnsafePathCharacters()
    {
        string name = DeviceLogNaming.BuildDeviceLogFileName(@"CORE:01/..\evil", IPAddress.Parse("10.0.0.7"));
        Assert.Equal("CORE_01_.._evil_10.0.0.7.log", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public async Task DeviceLogWriter_AppendsWithoutRetainingHandles_AndSanitizesSecretsInContent()
    {
        string logRoot = Directory.CreateTempSubdirectory("devlogs-").FullName;
        SerilogOptions options = new()
        {
            LogDirectory = logRoot,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
            DeviceLogAsyncCapacity = 100,
            DeviceLogAsyncBlockWhenFull = true,
        };

        DeviceLogWriter writer = new(Options.Create(options));
        await writer.StartAsync(CancellationToken.None);

        string key = DeviceLogNaming.BuildDeviceLogFileName("CORE-01", IPAddress.Parse("10.1.2.3"));
        LogEvent evt = CreateEvent("Connection succeeded for router (no secrets)");
        writer.Emit(key, evt);

        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();

        string path = Path.Combine(writer.DeviceLogDirectory, key);
        Assert.True(File.Exists(path));
        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("Connection succeeded", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeviceLogScope_StartsPending_ThenPromotesToIdentity()
    {
        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.Parse("3e00880a-b421-4c69-997b-963f5c8a9f05"),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "LIVE-TEST",
            RouterIp = IPAddress.Parse("10.1.2.3"),
            DateStartUtc = DateTime.UtcNow,
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

        using DeviceLogScope scope = DeviceLogScope.Begin(job);
        Assert.Equal("pending_10.1.2.3.log", scope.DeviceLogKey);
        scope.SetRouterIdentity("MikroTik");
        Assert.Equal("MikroTik_10.1.2.3.log", scope.DeviceLogKey);
    }

    [Fact]
    public async Task DeviceLogScope_WritesSessionHeaderOnce_WithoutRepeatingCorrelationOnLines()
    {
        string logRoot = Directory.CreateTempSubdirectory("devlogs-header-").FullName;
        DeviceLogWriter writer = new(Options.Create(new SerilogOptions
        {
            LogDirectory = logRoot,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
        }));
        await writer.StartAsync(CancellationToken.None);

        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.Parse("3e00880a-b421-4c69-997b-963f5c8a9f05"),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "LIVE-TEST",
            RouterIp = IPAddress.Parse("198.18.0.83"),
            DateStartUtc = DateTime.UtcNow,
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 19, 13, 56, TimeSpan.Zero));
        using (DeviceLogScope scope = DeviceLogScope.Begin(job, writer, new DeviceLogIdentityRegistry(), time))
        {
            scope.SetRouterIdentity("MikroTik");
            writer.Emit(scope.DeviceLogKey, CreateEvent("Connected successfully"));
        }

        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();

        string content = await File.ReadAllTextAsync(Path.Combine(writer.DeviceLogDirectory, "MikroTik_198.18.0.83.log"));
        Assert.Contains("Device: MikroTik", content, StringComparison.Ordinal);
        Assert.Contains("Address: 198.18.0.83", content, StringComparison.Ordinal);
        Assert.Contains("CSV name: LIVE-TEST", content, StringComparison.Ordinal);
        Assert.Contains("Import ID: 3e00880a-b421-4c69-997b-963f5c8a9f05", content, StringComparison.Ordinal);
        Assert.Contains("Connected successfully", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ImportId=", content, StringComparison.Ordinal);
        Assert.DoesNotContain("JobId=", content, StringComparison.Ordinal);
        Assert.DoesNotContain("RouterIp=", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceLogWriter_PromoteKey_MovesPendingIntoIdentityFile()
    {
        string logRoot = Directory.CreateTempSubdirectory("devlogs-promote-").FullName;
        DeviceLogWriter writer = new(Options.Create(new SerilogOptions
        {
            LogDirectory = logRoot,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
        }));
        await writer.StartAsync(CancellationToken.None);

        writer.Emit("pending_10.0.0.1.log", CreateEvent("pre-identity"));
        writer.PromoteKey("pending_10.0.0.1.log", "MikroTik_10.0.0.1.log");
        writer.Emit("MikroTik_10.0.0.1.log", CreateEvent("post-identity"));

        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();

        Assert.False(File.Exists(Path.Combine(writer.DeviceLogDirectory, "pending_10.0.0.1.log")));
        string content = await File.ReadAllTextAsync(Path.Combine(writer.DeviceLogDirectory, "MikroTik_10.0.0.1.log"));
        Assert.Contains("pre-identity", content, StringComparison.Ordinal);
        Assert.Contains("post-identity", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceLogWriter_StopDrainsQueuedEvents()
    {
        string logRoot = Directory.CreateTempSubdirectory("devlogs-drain-").FullName;
        DeviceLogWriter writer = new(Options.Create(new SerilogOptions
        {
            LogDirectory = logRoot,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
            DeviceLogAsyncCapacity = 1000,
            DeviceLogAsyncBlockWhenFull = true,
        }));

        await writer.StartAsync(CancellationToken.None);
        string key = DeviceLogNaming.BuildDeviceLogFileName("DRAIN", IPAddress.Parse("10.0.0.1"));
        for (int i = 0; i < 50; i++)
        {
            writer.Emit(key, CreateEvent($"drain-line-{i}"));
        }

        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();

        string content = await File.ReadAllTextAsync(Path.Combine(writer.DeviceLogDirectory, key));
        Assert.Contains("drain-line-0", content, StringComparison.Ordinal);
        Assert.Contains("drain-line-49", content, StringComparison.Ordinal);
        Assert.Equal(0, writer.WriteFailureCount);
    }

    [Fact]
    public async Task DeviceLogWriter_Stop_IsIdempotent()
    {
        string logRoot = Directory.CreateTempSubdirectory("devlogs-idem-").FullName;
        DeviceLogWriter writer = new(Options.Create(new SerilogOptions
        {
            LogDirectory = logRoot,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
        }));

        await writer.StartAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();
        writer.Dispose();
    }

    [Fact]
    public async Task DeviceLogWriter_ConcurrentEmits_DoNotInterleaveLinesForSameDevice()
    {
        string logRoot = Directory.CreateTempSubdirectory("devlogs-conc-").FullName;
        DeviceLogWriter writer = new(Options.Create(new SerilogOptions
        {
            LogDirectory = logRoot,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
            DeviceLogAsyncCapacity = 5000,
            DeviceLogAsyncBlockWhenFull = true,
        }));

        await writer.StartAsync(CancellationToken.None);
        string key = DeviceLogNaming.BuildDeviceLogFileName("CONC", IPAddress.Parse("10.0.0.1"));
        ConcurrentBag<string> markers = [];
        await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            string marker = $"LINE_{i:D3}_END";
            markers.Add(marker);
            writer.Emit(key, CreateEvent(marker));
            await Task.Yield();
        }));

        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();

        string[] lines = (await File.ReadAllLinesAsync(Path.Combine(writer.DeviceLogDirectory, key)))
            .Where(static l => l.Contains("LINE_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(100, lines.Length);
        Assert.All(lines, line => Assert.Contains("_END", line, StringComparison.Ordinal));
        Assert.Equal(100, markers.Distinct().Count());
    }

    [Fact]
    public async Task DeviceLogWriter_DifferentDevices_UseDistinctFiles()
    {
        string logRoot = Directory.CreateTempSubdirectory("devlogs-split-").FullName;
        DeviceLogWriter writer = new(Options.Create(new SerilogOptions
        {
            LogDirectory = logRoot,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
        }));

        await writer.StartAsync(CancellationToken.None);
        DeviceLogIdentityRegistry registry = new();
        string keyA = DeviceLogNaming.ResolveIdentityLogFileName(registry, "SAME", IPAddress.Parse("10.0.0.1"));
        string keyB = DeviceLogNaming.ResolveIdentityLogFileName(registry, "SAME", IPAddress.Parse("10.0.0.2"));
        writer.Emit(keyA, CreateEvent("device-a-only"));
        writer.Emit(keyB, CreateEvent("device-b-only"));
        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();

        string contentA = await File.ReadAllTextAsync(Path.Combine(writer.DeviceLogDirectory, keyA));
        string contentB = await File.ReadAllTextAsync(Path.Combine(writer.DeviceLogDirectory, keyB));
        Assert.Contains("device-a-only", contentA, StringComparison.Ordinal);
        Assert.DoesNotContain("device-b-only", contentA, StringComparison.Ordinal);
        Assert.Contains("device-b-only", contentB, StringComparison.Ordinal);
        Assert.DoesNotContain("device-a-only", contentB, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceLogWriter_WriteFailure_IsCountedAndDoesNotThrow()
    {
        string logRoot = Directory.CreateTempSubdirectory("devlogs-fail-").FullName;
        DeviceLogWriter writer = new(Options.Create(new SerilogOptions
        {
            LogDirectory = logRoot,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
            DeviceLogAsyncCapacity = 64,
            DeviceLogAsyncBlockWhenFull = true,
        }));

        await writer.StartAsync(CancellationToken.None);
        string key = DeviceLogNaming.BuildDeviceLogFileName("FAIL", IPAddress.Parse("10.0.0.1"));
        Directory.CreateDirectory(Path.Combine(writer.DeviceLogDirectory, key));
        writer.Emit(key, CreateEvent("should-fail-to-append"));
        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();

        Assert.True(writer.WriteFailureCount >= 1);
    }

    [Fact]
    public async Task DeviceLogWriter_WhenQueueFullWithoutBlocking_FallsBackToSyncWrite()
    {
        string logRoot = Directory.CreateTempSubdirectory("devlogs-sync-").FullName;
        DeviceLogWriter writer = new(Options.Create(new SerilogOptions
        {
            LogDirectory = logRoot,
            DeviceLogEnabled = true,
            DeviceLogDirectory = "Devices",
            DeviceLogAsyncCapacity = 64,
            DeviceLogAsyncBlockWhenFull = false,
        }));

        await writer.StartAsync(CancellationToken.None);
        string key = DeviceLogNaming.BuildDeviceLogFileName("SYNC", IPAddress.Parse("10.0.0.1"));
        for (int i = 0; i < 200; i++)
        {
            writer.Emit(key, CreateEvent($"sync-fallback-{i}"));
        }

        await writer.StopAsync(CancellationToken.None);
        writer.Dispose();

        string content = await File.ReadAllTextAsync(Path.Combine(writer.DeviceLogDirectory, key));
        Assert.Contains("sync-fallback-0", content, StringComparison.Ordinal);
        Assert.Contains("sync-fallback-199", content, StringComparison.Ordinal);
        Assert.Equal(0, writer.WriteFailureCount);
    }

    [Fact]
    public void DeviceLogFilters_ExcludeDeviceDetail_ButKeepWarningsAndSummaries()
    {
        LogEvent detail = CreateEvent(
            "Connected successfully",
            new LogEventProperty("DeviceLogKey", new ScalarValue("MikroTik.log")));

        LogEvent summary = CreateEvent(
            "Router job started",
            new LogEventProperty("DeviceLogKey", new ScalarValue("MikroTik.log")),
            new LogEventProperty("ServiceSummary", new ScalarValue(true)));

        LogEvent warning = new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Warning,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse("something went wrong"),
            properties: [new LogEventProperty("DeviceLogKey", new ScalarValue("MikroTik.log"))]);

        Assert.True(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog(detail));
        Assert.False(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog(summary));
        Assert.False(DeviceLogFilters.IsDeviceDetailExcludedFromServiceLog(warning));
    }

    private static LogEvent CreateEvent(string message, params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties: properties);
}
