using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade.Live;
public sealed class LiveFleetCompletionRaceTests
{
    [Fact]
    public async Task AllRoutersComplete_BeforeTimeout_IsSuccessful()
    {
        FakeCollector collector = new((_, _) => Task.FromResult(Success("x")));
        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(
            collector,
            totalTimeout: TimeSpan.FromMinutes(5));

        LiveFleetAuditReport report = await auditor.AuditAsync(Parse(
            """
            routerip
            10.0.0.1
            10.0.0.2
            """));

        Assert.True(report.Summary.Completed);
        Assert.False(report.Summary.Cancelled);
        Assert.Equal(2, report.Summary.SuccessfullyCollected);
        Assert.Null(report.Summary.Notes);
    }

    [Fact]
    public async Task DeadlineExpires_WhileRouterActive_IsIncomplete()
    {
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeCollector collector = new(async (ip, ct) =>
        {
            if (ip == "10.0.0.1")
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Success(ip);
        });

        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(
            collector,
            totalTimeout: TimeSpan.FromMilliseconds(100),
            maxConcurrent: 1);

        Task<LiveFleetAuditReport> auditTask = auditor.AuditAsync(Parse(
            """
            routerip
            10.0.0.1
            10.0.0.2
            """));

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        LiveFleetAuditReport report = await auditTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(report.Summary.Completed);
        Assert.True(report.Summary.Cancelled);
        Assert.Contains(report.Routers, static r =>
            r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Cancelled);
        Assert.Contains("timed out", report.Summary.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeadlineFires_AfterFinalRouterCompletes_DoesNotRetroactivelyCancel()
    {
        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(
            new FakeCollector((_, _) => Task.FromResult(Success("10.0.0.1"))),
            totalTimeout: TimeSpan.FromMinutes(1));

        int raceHits = 0;
        for (int i = 0; i < 50; i++)
        {
            auditor.OnBeforeFinalizeSummary = boundary =>
            {
                Assert.True(boundary.AllTargetsReachedNaturalTerminal);
                boundary.DeadlineCts.Cancel();
            };

            LiveFleetAuditReport report = await auditor.AuditAsync(Parse("routerip\n10.0.0.1\n"));
            Assert.True(report.Summary.Completed);
            Assert.False(report.Summary.Cancelled);
            Assert.Equal(1, report.Summary.SuccessfullyCollected);
            raceHits++;
        }

        Assert.Equal(50, raceHits);
    }

    [Fact]
    public async Task UserCancel_BeforeAnyRouterStarts_IsIncomplete()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        int calls = 0;
        FakeCollector collector = new((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Success("x"));
        });

        LiveFleetAuditReport report = await CreateAuditor(collector).AuditAsync(
            Parse(
                """
                routerip
                10.0.0.1
                10.0.0.2
                """),
            cts.Token);

        Assert.Equal(0, calls);
        Assert.False(report.Summary.Completed);
        Assert.True(report.Summary.Cancelled);
        Assert.All(
            report.Routers.Where(static r => r.Input.IsValid && !r.Input.IsDuplicate),
            static r => Assert.Equal(LiveInventoryCollectionStatus.Cancelled, r.Collection.CollectionStatus));
        Assert.Contains("cancelled", report.Summary.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UserCancel_WhileConnectionsActive_PreservesCompleted()
    {
        using CancellationTokenSource cts = new();
        FakeCollector collector = new(async (ip, ct) =>
        {
            if (ip == "10.0.0.1")
            {
                ReadOnlyInventoryCollectionResult result = Success(ip);
                await cts.CancelAsync();
                return result;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                return new ReadOnlyInventoryCollectionResult
                {
                    RouterIp = ip,
                    CollectionStatus = LiveInventoryCollectionStatus.Cancelled,
                    FailureDetail = "Audit cancelled.",
                };
            }

            return Success(ip);
        });

        LiveFleetAuditReport report = await CreateAuditor(collector, maxConcurrent: 1).AuditAsync(
            Parse(
                """
                routerip
                10.0.0.1
                10.0.0.2
                10.0.0.3
                """),
            cts.Token);

        Assert.False(report.Summary.Completed);
        Assert.True(report.Summary.Cancelled);
        Assert.Contains(report.Routers, static r =>
            r.Input.RouterIpText == "10.0.0.1"
            && r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Succeeded);
        Assert.Contains(report.Routers, static r =>
            r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Cancelled);
    }

    [Fact]
    public async Task UserCancel_AfterAllRoutersComplete_DoesNotRetroactivelyCancel()
    {
        using CancellationTokenSource cts = new();
        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(
            new FakeCollector((_, _) => Task.FromResult(Success("10.0.0.1"))));

        auditor.OnBeforeFinalizeSummary = _ => cts.Cancel();

        LiveFleetAuditReport report = await auditor.AuditAsync(
            Parse("routerip\n10.0.0.1\n"),
            cts.Token);

        Assert.True(report.Summary.Completed);
        Assert.False(report.Summary.Cancelled);
    }

    [Fact]
    public async Task EmptyInput_CompletesSuccessfully()
    {
        LiveFleetAuditReport report = await CreateAuditor(
                new FakeCollector((_, _) => throw new InvalidOperationException("should not connect")))
            .AuditAsync(Parse("routerip\r\n"));

        Assert.True(report.Summary.Completed);
        Assert.False(report.Summary.Cancelled);
        Assert.Equal(0, report.Summary.ValidUniqueAddresses);
    }

    [Fact]
    public async Task EntirelyInvalidInput_CompletesSuccessfully()
    {
        LiveFleetAuditReport report = await CreateAuditor(
                new FakeCollector((_, _) => throw new InvalidOperationException("should not connect")))
            .AuditAsync(Parse(
                """
                routerip
                not-an-ip
                hostname.local
                """));

        Assert.True(report.Summary.Completed);
        Assert.False(report.Summary.Cancelled);
        Assert.Equal(2, report.Summary.InvalidInputRows);
        Assert.Equal(0, report.Summary.ValidUniqueAddresses);
        Assert.All(report.Routers, static r =>
            Assert.Equal(LiveInventoryCollectionStatus.NotAttempted, r.Collection.CollectionStatus));
    }

    [Fact]
    public async Task DuplicateOnlyValidInput_ConnectsOnce_AndCompletes()
    {
        ConcurrentBag<string> connected = [];
        FakeCollector collector = new((ip, _) =>
        {
            connected.Add(ip);
            return Task.FromResult(Success(ip));
        });

        LiveFleetAuditReport report = await CreateAuditor(collector).AuditAsync(Parse(
            """
            routerip
            10.0.0.9
            10.0.0.9
            10.0.0.9
            """));

        Assert.True(report.Summary.Completed);
        Assert.False(report.Summary.Cancelled);
        Assert.Single(connected);
        Assert.Equal(1, report.Summary.ValidUniqueAddresses);
        Assert.Equal(2, report.Summary.DuplicateAddresses);
        Assert.Equal(1, report.Summary.SuccessfullyCollected);
    }

    [Fact]
    public async Task MixedCompletedFailedAndTimedOut_SummaryReconciles()
    {
        TaskCompletionSource startedHang = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeCollector collector = new(async (ip, ct) =>
        {
            if (ip == "10.0.0.1")
            {
                return Success(ip);
            }

            if (ip == "10.0.0.2")
            {
                return new ReadOnlyInventoryCollectionResult
                {
                    RouterIp = ip,
                    CollectionStatus = LiveInventoryCollectionStatus.ConnectionFailed,
                    FailureDetail = "Connection failed.",
                };
            }

            startedHang.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success(ip);
        });

        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(
            collector,
            totalTimeout: TimeSpan.FromMilliseconds(150),
            maxConcurrent: 2);

        Task<LiveFleetAuditReport> auditTask = auditor.AuditAsync(Parse(
            """
            routerip
            10.0.0.1
            10.0.0.2
            10.0.0.3
            """));

        await startedHang.Task.WaitAsync(TimeSpan.FromSeconds(2));
        LiveFleetAuditReport report = await auditTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(report.Summary.Completed);
        Assert.True(report.Summary.Cancelled);
        Assert.Equal(1, report.Summary.SuccessfullyCollected);
        Assert.Equal(1, report.Summary.ConnectionFailures);
        Assert.Contains(report.Routers, static r =>
            r.Input.RouterIpText == "10.0.0.1"
            && r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Succeeded);
        Assert.Contains(report.Routers, static r =>
            r.Input.RouterIpText == "10.0.0.2"
            && r.Collection.CollectionStatus == LiveInventoryCollectionStatus.ConnectionFailed);
        Assert.Contains(report.Routers, static r =>
            r.Input.RouterIpText == "10.0.0.3"
            && r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Cancelled);
        Assert.Contains("timed out", report.Summary.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    private static LiveAuditRouterCsvParser.ParseResult Parse(string csv)
    {
        using StringReader reader = new(csv);
        return LiveAuditRouterCsvParser.Parse(reader);
    }

    private static ReadOnlyInventoryCollectionResult Success(string ip) =>
        new()
        {
            RouterIp = ip,
            CollectionStatus = LiveInventoryCollectionStatus.Succeeded,
            IdentityStatus = LiveIdentityCollectionStatus.Succeeded,
            Identity = "AP01.POP001.001.access",
            RouterOsVersion = "6.49.10",
            Architecture = "mipsbe",
            Packages =
            [
                new InstalledPackageInfo("routeros-mipsbe", "6.49.10", false),
                new InstalledPackageInfo("user-manager", "6.49.10", false),
            ],
        };

    private static LivePackageCompatibilityFleetAuditor CreateAuditor(
        FakeCollector collector,
        TimeSpan? totalTimeout = null,
        int maxConcurrent = 5)
    {
        string root = Directory.CreateTempSubdirectory("live-race-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "user-manager-6.49.23-mipsbe.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "user-manager-7.23.8-mipsbe.npk"), [1]);
        DirectoryCatalog catalog = new(root);
        FixedRouterOsV7TargetProvider target = new("7.23.8");

        return new LivePackageCompatibilityFleetAuditor(
            collector,
            new PackageCompatibilityAuditor(
                new UpgradePackageResolver(catalog, Options.Create(new FirmwareFilesOptions { Path = root })),
                catalog,
                target),
            Options.Create(new LivePackageCompatibilityAuditOptions
            {
                Enabled = true,
                MaxConcurrentConnections = maxConcurrent,
                TotalAuditTimeout = totalTimeout,
            }),
            Options.Create(new MikroTikOptions
            {
                Username = "admin",
                Password = "secret",
                Port = 8728,
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            }),
            target,
            NullLogger<LivePackageCompatibilityFleetAuditor>.Instance);
    }

    private sealed class FakeCollector(
        Func<string, CancellationToken, Task<ReadOnlyInventoryCollectionResult>> factory)
        : IReadOnlyMikrotikInventoryCollector
    {
        public Task<ReadOnlyInventoryCollectionResult> CollectAsync(
            string routerIp,
            string username,
            string password,
            int port,
            TimeSpan connectionTimeout,
            string? routerName = null,
            CancellationToken cancellationToken = default) =>
            factory(routerIp, cancellationToken);
    }

    private sealed class DirectoryCatalog(string root) : IFirmwarePackageCatalog
    {
        public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
            GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

        public IReadOnlyList<FirmwarePackageInfo> GetPackages()
        {
            List<FirmwarePackageInfo> list = [];
            foreach (string path in Directory.EnumerateFiles(root, "*.npk", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(path);
                if (FirmwarePackageFileNameParser.TryParse(
                        fileName,
                        out string packageName,
                        out string? arch,
                        out RouterOsVersion version))
                {
                    list.Add(new FirmwarePackageInfo(
                        packageName,
                        fileName,
                        path,
                        arch,
                        version,
                        1,
                        "00"));
                }
            }

            return list;
        }
    }
}
