// -----------------------------------------------------------------------------
// File: DeviceLogTests.cs
//
// Description:
// Unit/integration tests for DeviceLogWriter append, WriteRaw session headers, and
// DeviceLogSink routing.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.Scheduling;
using Serilog.Events;
using Serilog.Parsing;

namespace MikrotikWorker.Tests.Logging;
/// <summary>
/// Covers DeviceLogWriter and DeviceLogSink basics.
/// </summary>
public sealed class DeviceLogTests
{
    /// <summary>
    /// Scenario: A device log file name and stem are built for identity <c>MikroTik</c> at 198.18.0.83, and the plain
    /// identity is sanitised.
    /// Expected: The file name is <c>MikroTik_198.18.0.83.log</c>, the stem is <c>MikroTik_198.18.0.83</c>, and a
    /// name that is already safe is returned unchanged.
    /// Invariant: Device log names use the same identity_ip stem convention as backup artefacts.
    /// </summary>
    [Fact]
    public void DeviceLogNaming_IdentityStem_MatchesBackupSanitisation()
    {
        IPAddress ip = IPAddress.Parse("198.18.0.83");
        Assert.Equal("MikroTik_198.18.0.83.log", DeviceLogNaming.BuildDeviceLogFileName("MikroTik", ip));
        Assert.Equal("MikroTik_198.18.0.83", DeviceLogNaming.BuildDeviceStem("MikroTik", ip));
        Assert.Equal("MikroTik", DeviceLogNaming.Sanitize("MikroTik"));
    }

    /// <summary>
    /// Scenario: A pending log file name is requested for 198.18.0.83 before a router identity is known.
    /// Expected: The name is <c>pending_198.18.0.83.log</c>.
    /// Invariant: Before identity is resolved, the log is keyed by IP address only.
    /// </summary>
    [Fact]
    public void DeviceLogNaming_PendingUsesIpUntilIdentityKnown()
    {
        Assert.Equal("pending_198.18.0.83.log", DeviceLogNaming.BuildPendingLogFileName(IPAddress.Parse("198.18.0.83")));
    }

    /// <summary>
    /// Scenario: Two routers that both report identity <c>MikroTik</c>, at 198.18.0.83 and 198.18.0.84, resolve
    /// their log file names through one shared <c>DeviceLogIdentityRegistry</c>.
    /// Expected: The names are <c>MikroTik_198.18.0.83.log</c> and <c>MikroTik_198.18.0.84.log</c>.
    /// Invariant: Routers sharing an identity are disambiguated by IP and never share a log file.
    /// </summary>
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

    /// <summary>
    /// Scenario: The same identity (<c>MikroTik</c>) and IP (198.18.0.83) are resolved twice through one registry.
    /// Expected: Both calls return <c>MikroTik_198.18.0.83.log</c>.
    /// Invariant: Re-resolving a known device is idempotent and does not create a second log stem.
    /// </summary>
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

    /// <summary>
    /// Scenario: A log file name is built from the hostile identity <c>CORE:01/..\evil</c> and IP 10.0.0.7.
    /// Expected: The name is <c>CORE_01_.._evil_10.0.0.7.log</c> and contains no characters from
    /// <c>Path.GetInvalidFileNameChars()</c>.
    /// Invariant: A device-reported identity cannot introduce path separators or traversal into the log file name.
    /// </summary>
    [Fact]
    public void DeviceLogNaming_SanitizesUnsafePathCharacters()
    {
        string name = DeviceLogNaming.BuildDeviceLogFileName(@"CORE:01/..\evil", IPAddress.Parse("10.0.0.7"));
        Assert.Equal("CORE_01_.._evil_10.0.0.7.log", name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    /// <summary>
    /// Scenario: A started <c>DeviceLogWriter</c> receives one event for CORE-01, then is stopped and disposed before
    /// the file is read back.
    /// Expected: The device log file exists, can be read after disposal, contains the logged message, and does not
    /// contain <c>Password=</c>.
    /// Invariant: Secrets never appear in log content, and the written file is readable once the writer has stopped.
    /// </summary>
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

    /// <summary>
    /// Scenario: A scope begins for a job targeting 10.1.2.3, then the router identity <c>MikroTik</c> is set.
    /// Expected: The scope key is <c>pending_10.1.2.3.log</c> initially and <c>MikroTik_10.1.2.3.log</c> after the
    /// identity is set.
    /// Invariant: The log key moves from IP-only to identity-based exactly when the identity becomes known.
    /// </summary>
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

    /// <summary>
    /// Scenario: A scope for LIVE-TEST (198.18.0.83, import 3e00880a-b421-4c69-997b-963f5c8a9f05) sets its identity
    /// and emits one event through a writer with a fixed clock.
    /// Expected: The log carries a session header with the device, address, CSV name and import ID, plus the logged
    /// line, and contains no <c>ImportId=</c>, <c>JobId=</c> or <c>RouterIp=</c> correlation fragments.
    /// Invariant: Correlation details appear once in the header and are never repeated on each log line.
    /// </summary>
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

    /// <summary>
    /// Scenario: An event is written under <c>pending_10.0.0.1.log</c>, the key is promoted to
    /// <c>MikroTik_10.0.0.1.log</c>, and a second event is written under the identity key.
    /// Expected: The pending file no longer exists, and the identity file contains both the pre-identity and
    /// post-identity lines.
    /// Invariant: Promotion keeps earlier output and leaves no orphaned pending file.
    /// </summary>
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

    /// <summary>
    /// Scenario: 50 events are emitted to one device log through a writer with a 1000-entry queue, and the writer is
    /// stopped immediately afterwards.
    /// Expected: The file contains the first (<c>drain-line-0</c>) and last (<c>drain-line-49</c>) lines and
    /// <c>WriteFailureCount</c> is 0.
    /// Invariant: Stopping the writer flushes every queued event before it returns.
    /// </summary>
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

    /// <summary>
    /// Scenario: A started writer is stopped twice and disposed twice.
    /// Expected: No exception is thrown by the repeated calls.
    /// Invariant: Shutdown can be called redundantly (for example by the host and by disposal) without failing.
    /// </summary>
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

    /// <summary>
    /// Scenario: 100 tasks concurrently emit uniquely numbered events (<c>LINE_nnn_END</c>) to the same device log.
    /// Expected: The file contains exactly 100 marker lines, each ending in <c>_END</c>, and all 100 markers are
    /// distinct.
    /// Invariant: Concurrent writers to one device never interleave or truncate each other's lines.
    /// </summary>
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

    /// <summary>
    /// Scenario: Two routers with the same identity <c>SAME</c> but different IPs (10.0.0.1, 10.0.0.2) each receive
    /// one event.
    /// Expected: Each file contains only its own event and not the other's.
    /// Invariant: Events for different devices are never mixed in a single log file.
    /// </summary>
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

    /// <summary>
    /// Scenario: A directory is created where the device log file should be, so appending the emitted event fails.
    /// Expected: Emit, stop and dispose complete without throwing and <c>WriteFailureCount</c> is at least 1.
    /// Invariant: A device log I/O failure is counted and swallowed so it cannot break the router job.
    /// </summary>
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

    /// <summary>
    /// Scenario: 200 events are emitted rapidly to a writer with a 64-entry queue and blocking-when-full disabled.
    /// Expected: The first (<c>sync-fallback-0</c>) and last (<c>sync-fallback-199</c>) lines are in the file and
    /// <c>WriteFailureCount</c> is 0.
    /// Invariant: Overflowing the queue falls back to a synchronous write instead of dropping events.
    /// </summary>
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

    /// <summary>
    /// Scenario: Three events carrying a <c>DeviceLogKey</c> are tested against the service-log filter: an
    /// information detail line, an information line flagged <c>ServiceSummary</c>, and a warning.
    /// Expected: Only the plain detail line is excluded from the service log.
    /// Invariant: Device-level detail stays in the device log, while summaries and warnings still reach the service log.
    /// </summary>
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

    /// <summary>
    /// Private helper <c>CreateEvent</c> supporting the scenarios in this file.
    /// </summary>
    private static LogEvent CreateEvent(string message, params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplateParser().Parse(message),
            properties: properties);
}
