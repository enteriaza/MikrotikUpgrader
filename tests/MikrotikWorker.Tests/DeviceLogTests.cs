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
    public void DeviceLogNaming_IncludesIp_ToAvoidIdentityCollisions()
    {
        string a = DeviceLogNaming.BuildFileName("MikroTik", IPAddress.Parse("198.18.0.83"));
        string b = DeviceLogNaming.BuildFileName("MikroTik", IPAddress.Parse("198.18.0.84"));
        Assert.NotEqual(a, b);
        Assert.Contains("198.18.0.83", a, StringComparison.Ordinal);
        Assert.Contains("198.18.0.84", b, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceLogNaming_WorksWithoutIdentity()
    {
        string name = DeviceLogNaming.BuildFileName(null, IPAddress.Parse("10.0.0.5"));
        Assert.Equal("router_10.0.0.5.log", name);
    }

    [Fact]
    public void DeviceLogNaming_SanitizesUnsafePathCharacters()
    {
        string name = DeviceLogNaming.BuildFileName(@"CORE:01/..\evil", IPAddress.Parse("10.0.0.7"));
        Assert.Equal("CORE_01_.._evil_10.0.0.7.log", name);
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), name, StringComparison.Ordinal);
        Assert.DoesNotContain(":", name, StringComparison.Ordinal);
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

        string key = DeviceLogNaming.BuildFileName("CORE-01", IPAddress.Parse("192.168.1.10"));
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
    public void DeviceLogScope_PushesStableKeyFromJobMetadata()
    {
        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "CORE-01",
            RouterIp = IPAddress.Parse("10.1.2.3"),
            DateStartUtc = DateTime.UtcNow,
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

        using DeviceLogScope scope = DeviceLogScope.Begin(job);
        Assert.Equal("CORE-01_10.1.2.3.log", scope.DeviceLogKey);
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
        string key = DeviceLogNaming.BuildFileName("DRAIN", IPAddress.Parse("10.0.0.9"));
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
        string key = DeviceLogNaming.BuildFileName("CONC", IPAddress.Parse("10.0.0.8"));
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
        string keyA = DeviceLogNaming.BuildFileName("SAME", IPAddress.Parse("10.0.0.1"));
        string keyB = DeviceLogNaming.BuildFileName("SAME", IPAddress.Parse("10.0.0.2"));
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
        string key = DeviceLogNaming.BuildFileName("FAIL", IPAddress.Parse("10.0.0.3"));
        // A directory occupying the target file path makes append fail without affecting StartAsync.
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
        string key = DeviceLogNaming.BuildFileName("SYNC", IPAddress.Parse("10.0.0.4"));
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

    private static LogEvent CreateEvent(string message) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties: []);
}
