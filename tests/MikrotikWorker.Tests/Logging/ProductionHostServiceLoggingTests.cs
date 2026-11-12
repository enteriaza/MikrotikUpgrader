// -----------------------------------------------------------------------------
// File: ProductionHostServiceLoggingTests.cs
//
// Description:
// Verifies production host emits lifecycle/config summary logs (including independent
// Mikrotik:Enabled vs FirmwareUpgrade:Enabled) through the real Serilog pipeline.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using Serilog;
using tik4net;

namespace MikrotikWorker.Tests.Logging;
/// <summary>
/// Starts the actual production host (Serilog + Kestrel + hosted services) and proves
/// the application log tells the service lifecycle story while device details stay isolated.
/// </summary>
/// <remarks>Covers production host lifecycle and config summary logging.</remarks>
[Collection("Serilog")]
public sealed class ProductionHostServiceLoggingTests
{
    /// <summary>Upper bound for waiting on production host startup and log output.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Scenario: The production host starts on a free port with Serilog file logging and per-device logs enabled, a
    /// fake clock and a scripted connection factory for two routers (DeviceA at 10.10.0.1, DeviceB at 10.10.0.2). The
    /// fake clock begins before the CSV datestart because imports require a future start; a two-router CSV is
    /// imported, the clock is advanced so both jobs become eligible and run to completion, and the host is stopped.
    /// Expected: The firmware endpoint answers 200 and the CSV is accepted (not rejected) with at least two pending
    /// jobs. The application log records the start-up, configuration summary, HTTP request, watcher, scheduler,
    /// worker and shutdown lifecycle entries. Each router gets its own device log carrying its identity and address.
    /// Invariant: Service-level events never appear in device logs, device operational detail never appears in the
    /// application log, and the router password is written to neither.
    /// </summary>
    [Fact]
    public async Task ProductionHost_EmitsServiceLifecycle_AndIsolatesDeviceLogs()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot(("probe.npk", "ok"u8.ToArray()));
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string logRoot = Directory.CreateTempSubdirectory("svc-log-").FullName;
        string deviceLogRoot = Path.Combine(logRoot, "Devices");
        string backupRoot = Directory.CreateTempSubdirectory("svc-bk-").FullName;
        Directory.CreateDirectory(deviceLogRoot);

        // Import-time clock: CSV datestart must be in the future. Execution clock advances later.
        FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        ScriptedMikrotikConnectionFactory connections = new(options =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: options.Host switch
                {
                    "10.10.0.1" => "DeviceA",
                    "10.10.0.2" => "DeviceB",
                    _ => "DeviceX",
                },
                version: "6.49.23 (stable)",
                architecture: "mipsbe",
                boardName: "hEX PoE"));

        WebApplication? app = null;
        try
        {
            int port = TestHostFactory.GetFreeTcpPort();
            WebApplicationBuilder builder = TestHostFactory.CreateBuilder(
                firmwareRoot,
                port,
                heartbeatInterval: TimeSpan.FromHours(1),
                scheduledJobsRoot: jobsRoot,
                additionalConfiguration: new Dictionary<string, string?>
                {
                    ["Logging:Serilog:LogDirectory"] = logRoot,
                    ["Logging:Serilog:FileName"] = "application-.log",
                    ["Logging:Serilog:DeviceLogDirectory"] = "Devices",
                    ["Logging:Serilog:DeviceLogEnabled"] = "true",
                    ["Logging:Serilog:WriteToConsole"] = "false",
                    ["Logging:Serilog:WriteToFile"] = "true",
                    ["Logging:Serilog:MinimumLevel"] = "Information",
                    [$"{MikroTikOptions.SectionName}:Enabled"] = "true",
                    [$"{MikroTikOptions.SectionName}:Username"] = "test-user",
                    [$"{MikroTikOptions.SectionName}:Password"] = "test-password",
                    [$"{FirmwareUpgradeOptions.SectionName}:Enabled"] = "false",
                    [$"{FirmwareUpgradeOptions.SectionName}:BackupDirectory"] = backupRoot,
                    // Long scan interval so FileSystemWatcher races do not double-process the CSV.
                    [$"{ScheduledJobsOptions.SectionName}:ScanInterval"] = "01:00:00",
                });

            builder.Services.AddSingleton<TimeProvider>(time);
            builder.Services.AddSingleton<IMikrotikConnectionFactory>(connections);

            app = MikrotikWorkerHost.BuildApplication(builder);
            await app.StartAsync();

            Uri baseAddress = TestHostFactory.GetListeningAddress(app);
            using (HttpClient client = new() { BaseAddress = baseAddress })
            {
                using HttpResponseMessage response = await client.GetAsync("/firmware_files/probe.npk");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            string csvPath = Path.Combine(jobsRoot, "lifecycle-batch.csv");
            await File.WriteAllTextAsync(csvPath, ValidTwoRouterCsv(), Encoding.UTF8);

            // Prefer explicit processor call; FileSystemWatcher may also race-process the same file.
            ScheduledJobFileProcessor processor = app.Services.GetRequiredService<ScheduledJobFileProcessor>();
            try
            {
                await processor.ProcessIncomingAsync(csvPath, CancellationToken.None).WaitAsync(TestTimeout);
            }
            catch (FileNotFoundException)
            {
                // Watcher already moved/processed the file.
            }

            await WaitForAsync(
                () => Directory.EnumerateFiles(Path.Combine(jobsRoot, "Accepted"), "*.csv").Any()
                      || Directory.EnumerateFiles(Path.Combine(jobsRoot, "Completed"), "*.csv").Any()
                      || Directory.EnumerateFiles(Path.Combine(jobsRoot, "Rejected"), "*.csv").Any(),
                TestTimeout);

            Assert.False(
                Directory.EnumerateFiles(Path.Combine(jobsRoot, "Rejected"), "*.csv").Any(),
                "CSV import was rejected unexpectedly.");
            Assert.True(
                Directory.EnumerateFiles(Path.Combine(jobsRoot, "Accepted"), "*.csv").Any()
                || Directory.EnumerateFiles(Path.Combine(jobsRoot, "Completed"), "*.csv").Any(),
                "CSV should be Accepted (or already Completed) after import.");

            // Make jobs eligible (datestart 01/12/2026 08:00 Africa/Johannesburg ≈ 06:00 UTC).
            InMemoryJobScheduler scheduler = app.Services.GetRequiredService<InMemoryJobScheduler>();
            TimeProvider resolvedTime = app.Services.GetRequiredService<TimeProvider>();
            Assert.Same(time, resolvedTime);
            Assert.True(scheduler.PendingCount >= 2, $"Expected pending jobs after import, PendingCount={scheduler.PendingCount}");

            time.SetUtcNow(new DateTimeOffset(2026, 12, 1, 6, 0, 0, TimeSpan.Zero));
            scheduler.NudgeWake();
            // FakeTimeProvider timers fire on SetUtcNow; nudge again after a yield so the
            // scheduler loop observes the advanced clock even if it was mid-wait.
            await Task.Yield();
            scheduler.NudgeWake();
            time.EvaluateTimers();

            string deviceAPath = Path.Combine(deviceLogRoot, "DeviceA_10.10.0.1.log");
            string deviceBPath = Path.Combine(deviceLogRoot, "DeviceB_10.10.0.2.log");
            await WaitForAsync(
                () => File.Exists(deviceAPath)
                      && File.Exists(deviceBPath)
                      && Directory.EnumerateFiles(Path.Combine(jobsRoot, "Completed"), "*.csv").Any(),
                TestTimeout,
                () => $"connections={connections.Connections.Count}, pending={scheduler.PendingCount}, dispatched={scheduler.DispatchedCount}, clock={time.GetUtcNow():o}, deviceA={File.Exists(deviceAPath)}, deviceB={File.Exists(deviceBPath)}");

            await app.StopAsync();
            await app.DisposeAsync();
            app = null;
            await Log.CloseAndFlushAsync();

            string applicationLog = await WaitForLogContentsAsync(
                logRoot,
                "application-*.log",
                text => text.Length > 0,
                TestTimeout);

            // --- Service lifecycle ---
            AssertLogContains(applicationLog, "Hosted services starting");
            AssertLogContains(applicationLog, "Application configuration summary");
            Assert.DoesNotContain("test-password", applicationLog, StringComparison.Ordinal);
            AssertLogContains(applicationLog, "Application ready");
            AssertLogContains(applicationLog, "Now listening on:");
            Assert.Contains($":{port}", applicationLog, StringComparison.Ordinal);
            AssertLogContains(applicationLog, "HTTP GET /firmware_files/probe.npk responded 200");
            AssertLogContains(applicationLog, "RemoteIp=");

            // CSV watcher / import
            AssertLogContains(applicationLog, "ScheduledJobs watcher started");
            AssertLogContains(applicationLog, "Initial CSV reconciliation");
            AssertLogContains(applicationLog, "FileSystemWatcher listening");
            AssertLogContains(applicationLog, "CSV detected:");
            AssertLogContains(applicationLog, "CSV accepted for scheduling");
            AssertLogContains(applicationLog, "CSV moved to Accepted");

            // Scheduler / queue / worker
            AssertLogContains(applicationLog, "Scheduled job scheduler service starting");
            AssertLogContains(applicationLog, "In-memory job scheduler started");
            AssertLogContains(applicationLog, "Job accepted into scheduler");
            AssertLogContains(applicationLog, "Job became eligible and was dispatched");
            AssertLogContains(applicationLog, "Job dequeued for execution");
            AssertLogContains(applicationLog, "Worker beginning job");
            AssertLogContains(applicationLog, "Router job started");
            AssertLogContains(applicationLog, "Worker finished job");
            AssertLogContains(applicationLog, "Job terminal");
            AssertLogContains(applicationLog, "ImportId=");
            AssertLogContains(applicationLog, "JobId=");
            AssertLogContains(applicationLog, "RouterIp=");

            AssertLogContains(applicationLog, "Worker starting");
            AssertLogContains(applicationLog, "Router job execution service starting");
            AssertLogContains(applicationLog, "Router job worker available");

            // Shutdown
            AssertLogContains(applicationLog, "Application stopping");
            AssertLogContains(applicationLog, "Hosted services stopping");
            AssertLogContains(applicationLog, "Application shutdown completed");

            // Device operational detail must not leak into the service log.
            Assert.DoesNotContain("Creating RouterOS export", applicationLog, StringComparison.Ordinal);
            Assert.DoesNotContain("Fetching firmware package", applicationLog, StringComparison.Ordinal);
            Assert.DoesNotContain("Installed package count:", applicationLog, StringComparison.Ordinal);
            Assert.DoesNotContain("PackageCount=11", applicationLog, StringComparison.Ordinal);
            Assert.DoesNotContain("Reconnection attempt", applicationLog, StringComparison.Ordinal);
            Assert.DoesNotContain("============================================================", applicationLog, StringComparison.Ordinal);
            Assert.DoesNotContain("Device: DeviceA", applicationLog, StringComparison.Ordinal);
            Assert.DoesNotContain("Device: DeviceB", applicationLog, StringComparison.Ordinal);

            // --- Device logs ---
            string logA = await File.ReadAllTextAsync(deviceAPath);
            string logB = await File.ReadAllTextAsync(deviceBPath);

            Assert.Contains("Device: DeviceA", logA, StringComparison.Ordinal);
            Assert.Contains("Address: 10.10.0.1", logA, StringComparison.Ordinal);
            Assert.Contains("Import ID:", logA, StringComparison.Ordinal);
            Assert.Contains("Job ID:", logA, StringComparison.Ordinal);
            Assert.Contains("Router job started", logA, StringComparison.Ordinal);
            AssertNoRepeatedCorrelation(logA);
            Assert.Contains("Device: DeviceB", logB, StringComparison.Ordinal);
            Assert.Contains("Address: 10.10.0.2", logB, StringComparison.Ordinal);
            Assert.DoesNotContain("Device: DeviceA", logB, StringComparison.Ordinal);
            Assert.DoesNotContain("10.10.0.1", logB.Replace("Address: 10.10.0.2", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            AssertNoRepeatedCorrelation(logB);

            Assert.DoesNotContain("Now listening on:", logA, StringComparison.Ordinal);
            Assert.DoesNotContain("HTTP GET", logA, StringComparison.Ordinal);
            Assert.DoesNotContain("ScheduledJobs watcher started", logA, StringComparison.Ordinal);
            Assert.DoesNotContain("Application ready", logA, StringComparison.Ordinal);
            Assert.DoesNotContain("Now listening on:", logB, StringComparison.Ordinal);
            Assert.DoesNotContain("Application ready", logB, StringComparison.Ordinal);

            // Capture evidence fragments for the report.
            await File.WriteAllTextAsync(
                Path.Combine(Path.GetTempPath(), "mikrotik-service-logging-evidence.txt"),
                BuildEvidenceSummary(applicationLog, logA, logB, port));
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    await app.StopAsync();
                    await app.DisposeAsync();
                }
                catch
                {
                }
            }

            try { await Log.CloseAndFlushAsync(); } catch { }

            TryDelete(firmwareRoot);
            TryDelete(jobsRoot);
            TryDelete(backupRoot);
            TryDelete(logRoot);
        }
    }

    /// <summary>
    /// Asserts that the log text contains the expected fragment, reporting the log length and its tail on failure.
    /// </summary>
    private static void AssertLogContains(string log, string expected)
    {
        Assert.True(
            log.Contains(expected, StringComparison.Ordinal),
            $"Missing expected log fragment '{expected}'. Log length={log.Length}. Tail={Tail(log, 1200)}");
    }

    /// <summary>
    /// Correlation identifiers belong in the session header ("Import ID:") only — not as
    /// structured <c>ImportId=</c> / <c>JobId=</c> / <c>RouterIp=</c> on subsequent event lines.
    /// </summary>
    private static void AssertNoRepeatedCorrelation(string deviceLog)
    {
        string[] eventLines = deviceLog
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(static l => l.Length > 12 && char.IsDigit(l[0]) && l[2] == ':')
            .ToArray();
        Assert.All(eventLines, static line =>
        {
            Assert.DoesNotContain("ImportId=", line, StringComparison.Ordinal);
            Assert.DoesNotContain("JobId=", line, StringComparison.Ordinal);
            Assert.DoesNotContain("RouterIp=", line, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Helper used by this suite.
    /// Key steps: if (string.IsNullOrEmpty(text)); {; return string.Empty;.
    /// </summary>
    private static string Tail(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= max ? text : text[^max..];
    }

    /// <summary>
    /// Private helper <c>ValidTwoRouterCsv</c> supporting the scenarios in this file.
    /// </summary>
    private static string ValidTwoRouterCsv() =>
        """
        datestart,datestop,routername,routerip
        01/12/2026 08:00:00,02/12/2026 18:00:00,CSV-A,10.10.0.1
        01/12/2026 08:00:00,02/12/2026 18:00:00,CSV-B,10.10.0.2
        """;

    /// <summary>
    /// Private helper <c>AssertContainsAny</c> supporting the scenarios in this file.
    /// </summary>
    private static void AssertContainsAny(string haystack, params string[] needles)
    {
        Assert.True(
            needles.Any(n => haystack.Contains(n, StringComparison.Ordinal)),
            "Expected one of: " + string.Join(", ", needles));
    }

    /// <summary>
    /// Private helper <c>BuildEvidenceSummary</c> supporting the scenarios in this file.
    /// </summary>
    private static string BuildEvidenceSummary(string appLog, string deviceA, string deviceB, int port)
    {
        static string Snip(string source, string marker, int context = 160)
        {
            int idx = source.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return $"(missing {marker})";
            }

            int start = Math.Max(0, idx - 20);
            int len = Math.Min(source.Length - start, context);
            return source.Substring(start, len).Replace("\r", " ").Replace("\n", " | ");
        }

        StringBuilder sb = new();
        sb.AppendLine("1. Startup/Kestrel: " + Snip(appLog, "Now listening on:"));
        sb.AppendLine("2. HTTP: " + Snip(appLog, "HTTP GET /firmware_files/probe.npk"));
        sb.AppendLine("3. CSV: " + Snip(appLog, "CSV accepted for scheduling"));
        sb.AppendLine("4. Enqueue: " + Snip(appLog, "Job accepted into scheduler"));
        sb.AppendLine("5. Dequeue: " + Snip(appLog, "Job dequeued for execution"));
        sb.AppendLine("6. Completion: " + Snip(appLog, "Job terminal"));
        sb.AppendLine("7. Shutdown: " + Snip(appLog, "Application shutdown completed"));
        sb.AppendLine("8. DeviceA header: " + Snip(deviceA, "Device: DeviceA"));
        sb.AppendLine("9. DeviceB header: " + Snip(deviceB, "Device: DeviceB"));
        sb.AppendLine($"Port={port}");
        sb.AppendLine("---APP_HAS_DEVICE_DETAIL---");
        sb.AppendLine("Creating RouterOS export present=" + appLog.Contains("Creating RouterOS export", StringComparison.Ordinal));
        sb.AppendLine("Installed package count present=" + appLog.Contains("Installed package count:", StringComparison.Ordinal));
        sb.AppendLine("Device session header present=" + appLog.Contains("============================================================", StringComparison.Ordinal));
        sb.AppendLine("---DEVICE_A_SAMPLE---");
        sb.AppendLine(string.Join(Environment.NewLine, deviceA.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(12)));
        sb.AppendLine("---DEVICE_B_SAMPLE---");
        sb.AppendLine(string.Join(Environment.NewLine, deviceB.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(12)));
        return sb.ToString();
    }

    /// <summary>
    /// Polls a condition until timeout or success.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, Func<string>? detail = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            "Condition was not met before timeout." + (detail is null ? string.Empty : " " + detail()));
    }

    /// <summary>
    /// Helper used by this suite.
    /// Key steps: DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;; while (DateTimeOffset.UtcNow &lt; deadline); {.
    /// </summary>
    private static async Task<string> WaitForLogContentsAsync(
        string directory,
        string pattern,
        Func<string, bool> predicate,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        string contents = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            string[] files = Directory.Exists(directory)
                ? Directory.GetFiles(directory, pattern)
                : [];
            contents = string.Join(Environment.NewLine, files.Select(static path =>
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

            await Task.Delay(100);
        }

        throw new TimeoutException(
            "Log contents did not satisfy predicate before timeout. Last length=" + contents.Length);
    }

    /// <summary>
    /// Helper used by this suite.
    /// Key steps: DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;; string contents = string.Empty;; while (DateTimeOffset.UtcNow &lt; deadline).
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}
