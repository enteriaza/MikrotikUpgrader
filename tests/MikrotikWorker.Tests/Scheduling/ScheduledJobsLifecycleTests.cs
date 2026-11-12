using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Tests.Scheduling;
public sealed class ScheduledJobsLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly DateTime UtcNow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ValidCsv_MovesToAccepted_ThenCompleted_WhenJobsTerminal()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        FakeTimeProvider time = new(new DateTimeOffset(UtcNow));
        await using SchedulerHost host = await SchedulerHost.CreateAsync(firmwareRoot, jobsRoot, time);
        try
        {
            string incoming = Path.Combine(jobsRoot, "batch.csv");
            await File.WriteAllTextAsync(incoming, ValidCsv());

            await host.Processor.ProcessIncomingAsync(incoming, CancellationToken.None).WaitAsync(TestTimeout);

            Assert.False(File.Exists(incoming));
            Assert.True(Directory.EnumerateFiles(Path.Combine(jobsRoot, "Accepted"), "batch.csv").Any());
            Assert.False(Directory.EnumerateFiles(Path.Combine(jobsRoot, "Completed"), "*.csv").Any());

            time.SetUtcNow(new DateTimeOffset(2026, 12, 1, 6, 0, 0, TimeSpan.Zero));
            await WaitForAsync(
                () => Directory.EnumerateFiles(Path.Combine(jobsRoot, "Completed"), "*.csv").Any(),
                TestTimeout);

            Assert.False(Directory.EnumerateFiles(Path.Combine(jobsRoot, "Accepted"), "batch.csv").Any());
            Assert.Equal(0, host.Scheduler.PendingCount);
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public async Task ActiveWindowCsv_IsAccepted_AndDispatchedImmediately()
    {
        // Clock fixed at 2026-11-12 06:52Z (08:52 Africa/Johannesburg).
        // CSV window 08:32–18:00 local → already active; must import and dispatch without advancing time.
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 12, 6, 52, 0, TimeSpan.Zero));
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        RecordingLoggerProvider logs = new();
        await using SchedulerHost host = await SchedulerHost.CreateAsync(
            firmwareRoot,
            jobsRoot,
            time,
            configureLogging: logging =>
            {
                logging.AddProvider(logs);
                logging.SetMinimumLevel(LogLevel.Information);
            });
        try
        {
            string incoming = Path.Combine(jobsRoot, "active-window.csv");
            await File.WriteAllTextAsync(
                incoming,
                """
                datestart,datestop,routername,routerip
                12/11/2026 08:32:00,12/11/2026 18:00:00,CORE-01,192.168.1.1
                """);

            await host.Processor.ProcessIncomingAsync(incoming, CancellationToken.None).WaitAsync(TestTimeout);

            Assert.False(
                Directory.EnumerateFiles(Path.Combine(jobsRoot, "Rejected"), "*.csv").Any(),
                "Active-window CSV must not be rejected.");
            Assert.True(
                Directory.EnumerateFiles(Path.Combine(jobsRoot, "Accepted"), "active-window.csv").Any()
                || Directory.EnumerateFiles(Path.Combine(jobsRoot, "Completed"), "*.csv").Any(),
                "Active-window CSV should be Accepted (or already Completed).");

            // Do not advance the clock — eligibility must already hold at import time.
            await WaitForAsync(
                () => logs.Messages.Any(static m =>
                    m.Contains("dispatched to execution queue", StringComparison.Ordinal)
                    && m.Contains("192.168.1.1", StringComparison.Ordinal)),
                TestTimeout);
            await WaitForAsync(
                () => Directory.EnumerateFiles(Path.Combine(jobsRoot, "Completed"), "*.csv").Any(),
                TestTimeout);

            Assert.Equal(0, host.Scheduler.PendingCount);
            Assert.Contains(
                logs.Messages,
                static m => m.Contains("CSV accepted for scheduling", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public async Task InvalidCsv_MovesToRejected_WithErrorReport()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        await using SchedulerHost host = await SchedulerHost.CreateAsync(firmwareRoot, jobsRoot);
        try
        {
            string incoming = Path.Combine(jobsRoot, "bad.csv");
            await File.WriteAllTextAsync(
                incoming,
                """
                datestart,datestop,routername,routerip
                01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,not-an-ip
                """);

            await host.Processor.ProcessIncomingAsync(incoming, CancellationToken.None).WaitAsync(TestTimeout);

            Assert.True(File.Exists(Path.Combine(jobsRoot, "Rejected", "bad.csv")));
            Assert.True(File.Exists(Path.Combine(jobsRoot, "Rejected", "bad.error.txt")));
            string report = await File.ReadAllTextAsync(Path.Combine(jobsRoot, "Rejected", "bad.error.txt"));
            Assert.Contains("TotalErrors:", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public async Task ValidationErrorRetention_TruncatesDetails_ButCountsAll()
    {
        string path = Path.Combine(Path.GetTempPath(), $"jobs-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                datestart,datestop,routername,routerip
                01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,bad-1
                01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-02,bad-2
                01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-03,bad-3
                """);

            ScheduledJobCsvParser parser = new(
                Options.Create(new ScheduledJobsOptions { MaxValidationErrorsRetained = 1 }),
                new CsvDateTimeParser(Options.Create(new CsvOptions())));

            CsvParseResult result = await parser.ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Equal(3, result.TotalErrorCount);
            Assert.Single(result.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DuplicateEvents_DoNotProcessSameFileTwice()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        await using SchedulerHost host = await SchedulerHost.CreateAsync(firmwareRoot, jobsRoot);
        try
        {
            string incoming = Path.Combine(jobsRoot, "dup.csv");
            await File.WriteAllTextAsync(incoming, ValidCsv());

            Task first = host.Processor.ProcessIncomingAsync(incoming, CancellationToken.None);
            Task second = host.Processor.ProcessIncomingAsync(incoming, CancellationToken.None);
            await Task.WhenAll(first, second).WaitAsync(TestTimeout);

            Assert.Single(Directory.GetFiles(Path.Combine(jobsRoot, "Accepted"), "dup.csv"));
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public async Task StartupScan_ProcessesExistingIncomingCsv()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            string incoming = Path.Combine(jobsRoot, "startup.csv");
            await File.WriteAllTextAsync(incoming, ValidCsv());

            await using WebApplication app = CreateFullApp(firmwareRoot, jobsRoot);
            await app.StartAsync().WaitAsync(TestTimeout);
            try
            {
                await WaitForAsync(
                    () => Directory.EnumerateFiles(Path.Combine(jobsRoot, "Accepted"), "startup.csv").Any(),
                    TestTimeout);
            }
            finally
            {
                await app.StopAsync().WaitAsync(TestTimeout);
            }
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public async Task ProcessingLeftovers_AreNotAutoImported_AtStartup()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        RecordingLoggerProvider logs = new();
        try
        {
            string processing = Path.Combine(jobsRoot, "Processing", "interrupted.csv");
            await File.WriteAllTextAsync(processing, ValidCsv());

            await using WebApplication app = CreateFullApp(firmwareRoot, jobsRoot, configureLogging: b =>
            {
                b.AddProvider(logs);
                b.SetMinimumLevel(LogLevel.Information);
            });
            await app.StartAsync().WaitAsync(TestTimeout);
            try
            {
                await WaitForAsync(
                    () => logs.Messages.Any(static m => m.Contains("Interrupted Processing CSV", StringComparison.Ordinal)),
                    TestTimeout);
                Assert.True(File.Exists(processing));
                Assert.Empty(Directory.GetFiles(Path.Combine(jobsRoot, "Accepted")));
            }
            finally
            {
                await app.StopAsync().WaitAsync(TestTimeout);
            }
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public async Task AcceptedCsv_CanBeManuallyReImported()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        FakeTimeProvider time = new(new DateTimeOffset(UtcNow));
        await using SchedulerHost host = await SchedulerHost.CreateAsync(firmwareRoot, jobsRoot, time);
        try
        {
            string incoming = Path.Combine(jobsRoot, "reimport.csv");
            await File.WriteAllTextAsync(incoming, ValidCsv());
            await host.Processor.ProcessIncomingAsync(incoming, CancellationToken.None).WaitAsync(TestTimeout);

            string accepted = Path.Combine(jobsRoot, "Accepted", "reimport.csv");
            Assert.True(File.Exists(accepted));

            string again = Path.Combine(jobsRoot, "reimport.csv");
            File.Copy(accepted, again);
            await host.Processor.ProcessIncomingAsync(again, CancellationToken.None).WaitAsync(TestTimeout);

            Assert.True(Directory.EnumerateFiles(Path.Combine(jobsRoot, "Accepted"), "reimport*.csv").Count() >= 2);
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public async Task SimulatedRestart_AllowsManualReImportIntoFreshScheduler()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        try
        {
            string acceptedCopy;
            {
                FakeTimeProvider time = new(new DateTimeOffset(UtcNow));
                await using SchedulerHost host = await SchedulerHost.CreateAsync(firmwareRoot, jobsRoot, time);
                string incoming = Path.Combine(jobsRoot, "restart.csv");
                await File.WriteAllTextAsync(incoming, ValidCsv());
                await host.Processor.ProcessIncomingAsync(incoming, CancellationToken.None).WaitAsync(TestTimeout);
                acceptedCopy = Path.Combine(jobsRoot, "Accepted", "restart.csv");
                Assert.True(File.Exists(acceptedCopy));
            }

            {
                FakeTimeProvider time = new(new DateTimeOffset(UtcNow));
                await using SchedulerHost host = await SchedulerHost.CreateAsync(firmwareRoot, jobsRoot, time);
                Assert.Equal(0, host.Scheduler.PendingCount);
                string incoming = Path.Combine(jobsRoot, "restart.csv");
                File.Copy(acceptedCopy, incoming, overwrite: true);
                await host.Processor.ProcessIncomingAsync(incoming, CancellationToken.None).WaitAsync(TestTimeout);
                Assert.True(Directory.EnumerateFiles(Path.Combine(jobsRoot, "Accepted"), "restart*.csv").Any());
            }
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public async Task CancellationDuringIngestion_LeavesFileInProcessing()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        FakeTimeProvider time = new(new DateTimeOffset(UtcNow));
        await using SchedulerHost host = await SchedulerHost.CreateAsync(
            firmwareRoot,
            jobsRoot,
            time,
            additionalConfiguration: new Dictionary<string, string?>
            {
                ["ScheduledJobs:MaximumPendingJobs"] = "1",
            });
        try
        {
            Guid blocker = Guid.NewGuid();
            host.Scheduler.RegisterImport(blocker, 1);
            await host.Scheduler.ScheduleAsync(
                new MikrotikScheduledJob
                {
                    ImportId = blocker,
                    SourceFileName = "blocker.csv",
                    RowNumber = 2,
                    RouterName = "BLOCK",
                    RouterIp = IPAddress.Parse("10.0.0.1"),
                    DateStartUtc = UtcNow.AddDays(30),
                    DateStopUtc = UtcNow.AddDays(31),
                },
                CancellationToken.None);

            string incoming = Path.Combine(jobsRoot, "cancel.csv");
            await File.WriteAllTextAsync(incoming, ValidCsv());

            using CancellationTokenSource cts = new();
            Task process = host.Processor.ProcessIncomingAsync(incoming, cts.Token);
            await WaitForAsync(
                () => File.Exists(Path.Combine(jobsRoot, "Processing", "cancel.csv")),
                TestTimeout);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await process.WaitAsync(TestTimeout));
            Assert.True(File.Exists(Path.Combine(jobsRoot, "Processing", "cancel.csv")));
            Assert.False(Directory.EnumerateFiles(Path.Combine(jobsRoot, "Accepted"), "cancel*.csv").Any());
            Assert.False(Directory.EnumerateFiles(Path.Combine(jobsRoot, "Rejected"), "cancel*.csv").Any());
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public async Task NonCsvFiles_AreIgnored()
    {
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        await using SchedulerHost host = await SchedulerHost.CreateAsync(firmwareRoot, jobsRoot);
        try
        {
            string txt = Path.Combine(jobsRoot, "notes.txt");
            await File.WriteAllTextAsync(txt, "ignore");
            await host.Processor.ProcessIncomingAsync(txt, CancellationToken.None);

            Assert.True(File.Exists(txt));
            Assert.Empty(Directory.GetFiles(Path.Combine(jobsRoot, "Accepted")));
            Assert.Empty(Directory.GetFiles(Path.Combine(jobsRoot, "Rejected")));
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    [Fact]
    public void FileMove_UsesUniqueName_OnDestinationCollision()
    {
        string root = Directory.CreateTempSubdirectory("move-test-").FullName;
        try
        {
            string destDir = Path.Combine(root, "Accepted");
            Directory.CreateDirectory(destDir);
            string existing = Path.Combine(destDir, "batch.csv");
            File.WriteAllText(existing, "old");
            string source = Path.Combine(root, "batch.csv");
            File.WriteAllText(source, "new");

            string moved = ScheduledJobFileMover.MoveSafely(source, destDir, "batch.csv");
            Assert.NotEqual(existing, moved);
            Assert.True(File.Exists(existing));
            Assert.True(File.Exists(moved));
            Assert.Equal("old", File.ReadAllText(existing));
            Assert.Equal("new", File.ReadAllText(moved));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ScheduledJobsDirectory_IncludesAccepted()
    {
        string firmwareRoot = TestHostFactory.CreateFirmwareRoot();
        string jobsRoot = TestHostFactory.CreateScheduledJobsRoot();
        try
        {
            using WebApplication app = CreateFullApp(firmwareRoot, jobsRoot);
            ScheduledJobsPaths paths = app.Services.GetRequiredService<ScheduledJobsPaths>();
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.Root));
            Assert.True(Directory.Exists(paths.Processing));
            Assert.True(Directory.Exists(paths.Accepted));
            Assert.True(Directory.Exists(paths.Completed));
            Assert.True(Directory.Exists(paths.Rejected));
        }
        finally
        {
            Directory.Delete(jobsRoot, true);
            Directory.Delete(firmwareRoot, true);
        }
    }

    private static WebApplication CreateFullApp(
        string firmwareRoot,
        string jobsRoot,
        FakeTimeProvider? timeProvider = null,
        Action<ILoggingBuilder>? configureLogging = null,
        IDictionary<string, string?>? additionalConfiguration = null)
    {
        Dictionary<string, string?> config = new()
        {
            ["ScheduledJobs:ScanInterval"] = "01:00:00",
        };
        if (additionalConfiguration is not null)
        {
            foreach ((string key, string? value) in additionalConfiguration)
            {
                config[key] = value;
            }
        }

        WebApplicationBuilder builder = TestHostFactory.CreateBuilder(
            firmwareRoot,
            TestHostFactory.GetFreeTcpPort(),
            scheduledJobsRoot: jobsRoot,
            additionalConfiguration: config,
            configureLogging: configureLogging);

        builder.Services.AddSingleton<TimeProvider>(timeProvider ?? new FakeTimeProvider(new DateTimeOffset(UtcNow)));
        return MikrotikWorkerHost.BuildApplication(builder);
    }

    private static string ValidCsv() =>
        """
        datestart,datestop,routername,routerip
        01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1
        01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-02,192.168.1.2
        """;

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
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

        throw new TimeoutException("Condition was not met before timeout.");
    }

    /// <summary>
    /// Builds the DI graph and runs scheduler + router execution loops (no FileSystemWatcher).
    /// </summary>
    private sealed class SchedulerHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly CancellationTokenSource _cts = new();
        private readonly RouterJobExecutionService _execution;
        private readonly Task _schedulerTask;

        private SchedulerHost(
            WebApplication app,
            IInMemoryJobScheduler scheduler,
            ScheduledJobFileProcessor processor,
            RouterJobExecutionService execution)
        {
            _app = app;
            Scheduler = scheduler;
            Processor = processor;
            _execution = execution;
            _schedulerTask = scheduler.RunAsync(_cts.Token);
        }

        public IInMemoryJobScheduler Scheduler { get; }

        public ScheduledJobFileProcessor Processor { get; }

        public static async Task<SchedulerHost> CreateAsync(
            string firmwareRoot,
            string jobsRoot,
            FakeTimeProvider? timeProvider = null,
            IDictionary<string, string?>? additionalConfiguration = null,
            Action<ILoggingBuilder>? configureLogging = null)
        {
            WebApplication app = CreateFullApp(
                firmwareRoot,
                jobsRoot,
                timeProvider,
                configureLogging: configureLogging,
                additionalConfiguration: additionalConfiguration);
            IInMemoryJobScheduler scheduler = app.Services.GetRequiredService<IInMemoryJobScheduler>();
            ScheduledJobFileProcessor processor = app.Services.GetRequiredService<ScheduledJobFileProcessor>();
            RouterJobExecutionService execution = app.Services
                .GetServices<IHostedService>()
                .OfType<RouterJobExecutionService>()
                .Single();
            await execution.StartAsync(CancellationToken.None);
            return new SchedulerHost(app, scheduler, processor, execution);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _schedulerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }

            try
            {
                await _execution.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            _cts.Dispose();
            await _app.DisposeAsync();
        }
    }
}
