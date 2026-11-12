// -----------------------------------------------------------------------------
// File: LiveFleetCompletionRaceTests.cs
//
// Description:
// Live/integration race coverage for fleet job completion behaviour
// (category-gated).
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade.Live;
    /// <summary>
    /// Scenario: fleet audit of one successful router; at finalize the test cancels the deadline CTS after all targets
    /// already reached a natural terminal state. Repeated 50 times to stress the race.
    /// Expected: every report is Completed, not Cancelled, with one successful collection.
    /// Invariant: a deadline that fires only after natural completion must not rewrite the summary as cancelled.
    /// </summary>
public sealed class LiveFleetCompletionRaceTests
{
    /// <summary>
    /// Two routers that complete well within a 5 minute total timeout give a <c>Completed</c> summary that is not
    /// <c>Cancelled</c>, with 2 collected and no notes.
    /// </summary>
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

    /// <summary>
    /// A 100 ms deadline that expires while the first router is still hanging gives a summary that is not
    /// <c>Completed</c> but is <c>Cancelled</c>. At least one router row is <c>Cancelled</c> and the notes say the
    /// audit 'timed out'.
    /// </summary>
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

    /// <summary>
    /// Scenario: one-router fleet audit; at finalize the test cancels the deadline CTS after natural terminal.
    /// Expected: every report is Completed (not Cancelled) across 50 race iterations.
    /// Invariant: a late deadline must not rewrite an already-complete summary as cancelled.
    /// </summary>
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

    /// <summary>
    /// A token cancelled before the audit starts means the collector is never called. The summary is
    /// <c>Cancelled</c> and not <c>Completed</c>, every valid unique router row is <c>Cancelled</c>, and the notes
    /// mention 'cancelled'.
    /// </summary>
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

    /// <summary>
    /// The first router completes and then cancels the audit while later routers wait. The summary is
    /// <c>Cancelled</c> and not <c>Completed</c>, <c>10.0.0.1</c> keeps <c>Succeeded</c>, and at least one other
    /// router is <c>Cancelled</c>.
    /// </summary>
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

    /// <summary>
    /// If the token is cancelled in the hook just before the summary is finalised, after the only router has
    /// completed, the summary is still <c>Completed</c> and not <c>Cancelled</c>.
    /// </summary>
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

    /// <summary>
    /// A CSV with only a header completes without calling the collector. The summary is <c>Completed</c>, not
    /// <c>Cancelled</c>, with 0 valid unique addresses.
    /// </summary>
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

    /// <summary>
    /// Two invalid rows (<c>not-an-ip</c> and <c>hostname.local</c>) complete without a connection. The summary is
    /// <c>Completed</c>, not <c>Cancelled</c>, with 2 invalid rows, 0 valid unique addresses and every router row
    /// <c>NotAttempted</c>.
    /// </summary>
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

    /// <summary>
    /// The same address listed three times is connected to once. The summary is <c>Completed</c>, not
    /// <c>Cancelled</c>, with 1 valid unique address, 2 duplicates and 1 successful collection.
    /// </summary>
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

    /// <summary>
    /// With one router succeeding, one failing to connect and one hanging until the deadline, the summary is
    /// <c>Cancelled</c> and not <c>Completed</c> with 1 collected and 1 connection failure. Rows for
    /// <c>10.0.0.1</c>, <c>10.0.0.2</c> and <c>10.0.0.3</c> are present, and the notes say the audit 'timed out'.
    /// </summary>
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

    /// <summary>
    /// Wraps the CSV text in a <c>StringReader</c> and runs it through <c>LiveAuditRouterCsvParser.Parse</c>.
    /// </summary>
    private static LiveAuditRouterCsvParser.ParseResult Parse(string csv)
    {
        using StringReader reader = new(csv);
        return LiveAuditRouterCsvParser.Parse(reader);
    }

    /// <summary>Creates a successful inventory result for <paramref name="ip"/> with a fixed identity and packages.</summary>
    /// <param name="ip">Router IP address recorded on the result.</param>
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

    /// <summary>
    /// Builds a <c>LivePackageCompatibilityFleetAuditor</c> over the fake collector, a temp firmware directory
    /// seeded with v6 and v7 <c>routeros</c> and <c>user-manager</c> packages, a fixed v7 target of 7.23.8, and the
    /// supplied concurrency limit and total audit timeout.
    /// </summary>
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

    /// <summary>
    /// Inventory collector fake that ignores credentials and timeouts and delegates to a scripted async factory
    /// keyed by router IP and cancellation token, so tests can control completion order and delays.
    /// </summary>
    private sealed class FakeCollector(
        Func<string, CancellationToken, Task<ReadOnlyInventoryCollectionResult>> factory)
        : IReadOnlyMikrotikInventoryCollector
    {
        /// <summary>Delegates to the scripted factory using only the router IP and cancellation token.</summary>
        /// <param name="routerIp">Router IP address.</param>
        /// <param name="username">User name (ignored).</param>
        /// <param name="password">Password (ignored).</param>
        /// <param name="port">API port (ignored).</param>
        /// <param name="connectionTimeout">Connect timeout (ignored).</param>
        /// <param name="routerName">Router name (ignored).</param>
        /// <param name="cancellationToken">Token passed to the scripted factory.</param>
        /// <returns>The scripted collection result.</returns>
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

    /// <summary>
    /// Firmware package catalogue fake that enumerates <c>*.npk</c> files under a temp root and parses each file
    /// name into a package entry.
    /// </summary>
    private sealed class DirectoryCatalog(string root) : IFirmwarePackageCatalog
    {
        /// <summary>Gets only the main <c>routeros</c> packages from <see cref="GetPackages"/>.</summary>
        public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
            GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

        /// <summary>Enumerates <c>*.npk</c> files under the fixture root and parses each name into a firmware package.</summary>
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
