using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests;

public sealed class LivePackageCompatibilityFleetAuditorTests
{
    [Fact]
    public async Task ConcurrencyLimit_IsRespected()
    {
        ConcurrentBag<int> observed = [];
        int current = 0;
        object gate = new();

        FakeCollector collector = new(async (ip, ct) =>
        {
            lock (gate)
            {
                current++;
                observed.Add(current);
            }

            try
            {
                await Task.Delay(80, ct);
                return Success(ip, "AP01.POP001.001.access");
            }
            finally
            {
                lock (gate)
                {
                    current--;
                }
            }
        });

        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(collector, maxConcurrent: 2);
        using StringReader reader = new(
            """
            routerip
            10.0.0.1
            10.0.0.2
            10.0.0.3
            10.0.0.4
            """);

        LiveFleetAuditReport report = await auditor.AuditAsync(LiveAuditRouterCsvParser.Parse(reader));

        Assert.True(report.Summary.Completed);
        Assert.Equal(4, report.Summary.SuccessfullyCollected);
        Assert.True(observed.Max() <= 2);
    }

    [Fact]
    public async Task OneFailedRouter_DoesNotStopOthers()
    {
        FakeCollector collector = new((ip, _) =>
        {
            if (ip == "10.0.0.2")
            {
                return Task.FromResult(new ReadOnlyInventoryCollectionResult
                {
                    RouterIp = ip,
                    CollectionStatus = LiveInventoryCollectionStatus.ConnectionFailed,
                    FailureDetail = "Connection failed.",
                });
            }

            return Task.FromResult(Success(ip, "AP01.POP001.001.access"));
        });

        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(collector);
        using StringReader reader = new(
            """
            routerip
            10.0.0.1
            10.0.0.2
            10.0.0.3
            """);

        LiveFleetAuditReport report = await auditor.AuditAsync(LiveAuditRouterCsvParser.Parse(reader));

        Assert.Equal(2, report.Summary.SuccessfullyCollected);
        Assert.Equal(1, report.Summary.ConnectionFailures);
        Assert.True(report.Summary.Completed);
        Assert.DoesNotContain(
            report.Routers.Where(static r =>
                r.Collection.CollectionStatus == LiveInventoryCollectionStatus.ConnectionFailed),
            static r => r.Compatibility is not null
                && r.Compatibility.Stages.Any(static s => s.Status == CatalogueCompatibilityStatus.Compatible)
                && r.Collection.CollectionStatus != LiveInventoryCollectionStatus.Succeeded);
    }

    [Fact]
    public async Task DuplicateAddresses_AreNotConnectedRepeatedly()
    {
        ConcurrentBag<string> connected = [];
        FakeCollector collector = new((ip, _) =>
        {
            connected.Add(ip);
            return Task.FromResult(Success(ip, "AP01.POP001.001.access"));
        });

        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(collector);
        using StringReader reader = new(
            """
            routerip,routername
            10.0.0.5,A
            10.0.0.5,B
            10.0.0.6,C
            """);

        LiveFleetAuditReport report = await auditor.AuditAsync(LiveAuditRouterCsvParser.Parse(reader));

        Assert.Equal(2, connected.Count);
        Assert.Equal(1, report.Summary.DuplicateAddresses);
        Assert.Equal(2, report.Summary.ValidUniqueAddresses);
    }

    [Fact]
    public async Task Cancellation_ProducesIncompleteSummary_AndPreservesCompleted()
    {
        using CancellationTokenSource cts = new();
        FakeCollector collector = new(async (ip, ct) =>
        {
            if (ip == "10.0.0.1")
            {
                ReadOnlyInventoryCollectionResult result = Success(ip, "AP01.POP001.001.access");
                await cts.CancelAsync();
                return result;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success(ip, "AP01.POP001.001.access");
        });

        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(collector, maxConcurrent: 1);
        using StringReader reader = new(
            """
            routerip
            10.0.0.1
            10.0.0.2
            10.0.0.3
            """);

        LiveFleetAuditReport report = await auditor.AuditAsync(
            LiveAuditRouterCsvParser.Parse(reader),
            cts.Token);

        Assert.True(report.Summary.Cancelled);
        Assert.False(report.Summary.Completed);
        Assert.Contains(report.Routers, static r =>
            r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Succeeded);
        Assert.Contains(report.Routers, static r =>
            r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Cancelled);
    }

    [Fact]
    public async Task Compatibility_EvaluatesV6V7_AndIdentityPolicy()
    {
        FakeCollector collector = new((ip, _) =>
        {
            string identity = ip switch
            {
                "10.0.0.10" => "ACE01.POP001.001.core",
                "10.0.0.11" => "BR01.POP001.001.edge",
                "10.0.0.12" => "AP01.POP001.001.access",
                "10.0.0.13" => "DCL01.POP001.001.core",
                "10.0.0.14" => "weird-name",
                "10.0.0.15" => " ",
                _ => "AP01.POP001.001.access",
            };
            return Task.FromResult(Success(ip, identity));
        });

        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(collector);
        using StringReader reader = new(
            """
            routerip
            10.0.0.10
            10.0.0.11
            10.0.0.12
            10.0.0.13
            10.0.0.14
            10.0.0.15
            """);

        LiveFleetAuditReport report = await auditor.AuditAsync(LiveAuditRouterCsvParser.Parse(reader));

        Assert.Equal(0, report.Summary.AceOrBrExcludedFromV7);
        Assert.True(report.Summary.UnknownOrUnavailableIdentity >= 2);

        LiveFleetRouterAuditRecord ace = report.Routers.Single(static r => r.Input.RouterIpText == "10.0.0.10");
        Assert.Equal(
            CatalogueCompatibilityStatus.Compatible,
            ace.Compatibility!.Stages.Single(static s => s.Stage == PackageCompatibilityUpgradeStage.RouterOs7).Status);

        LiveFleetRouterAuditRecord ap = report.Routers.Single(static r => r.Input.RouterIpText == "10.0.0.12");
        Assert.Equal(
            CatalogueCompatibilityStatus.Compatible,
            ap.Compatibility!.Stages.Single(static s => s.Stage == PackageCompatibilityUpgradeStage.V6Stepping).Status);
    }

    [Fact]
    public async Task ConnectionFailure_IsNeverReportedCompatible()
    {
        FakeCollector collector = new((ip, _) => Task.FromResult(new ReadOnlyInventoryCollectionResult
        {
            RouterIp = ip,
            CollectionStatus = LiveInventoryCollectionStatus.AuthenticationFailed,
            FailureDetail = "Authentication failed.",
        }));

        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(collector);
        using StringReader reader = new("routerip\n10.0.0.99\n");
        LiveFleetAuditReport report = await auditor.AuditAsync(LiveAuditRouterCsvParser.Parse(reader));

        Assert.Equal(0, report.Summary.FullyCompatibleDevices);
        Assert.Null(report.Routers.Single().Compatibility);
    }

    [Fact]
    public async Task Reports_CsvAndJson_AreConsistent()
    {
        FakeCollector collector = new((ip, _) => Task.FromResult(Success(ip, "AP01.POP001.001.access")));
        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(collector);
        using StringReader reader = new("routerip,routername\n10.0.0.40,Edge\n");
        LiveFleetAuditReport report = await auditor.AuditAsync(LiveAuditRouterCsvParser.Parse(reader));

        string dir = Directory.CreateTempSubdirectory("live-report-").FullName;
        (string csvPath, string jsonPath) = await LivePackageCompatibilityAuditReportWriter.WriteAsync(dir, report);

        Assert.True(File.Exists(csvPath));
        Assert.True(File.Exists(jsonPath));
        string csv = await File.ReadAllTextAsync(csvPath);
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));

        Assert.Contains("10.0.0.40", csv, StringComparison.Ordinal);
        Assert.Contains("Edge", csv, StringComparison.Ordinal);
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("successfullyCollected").GetInt32());
        Assert.Equal("10.0.0.40", json.RootElement.GetProperty("routers")[0].GetProperty("routerIp").GetString());
        Assert.DoesNotContain("secret", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", await File.ReadAllTextAsync(jsonPath), StringComparison.OrdinalIgnoreCase);

        // Unique names — second write must not overwrite.
        (string csv2, string json2) = await LivePackageCompatibilityAuditReportWriter.WriteAsync(dir, report);
        Assert.NotEqual(csvPath, csv2);
        Assert.NotEqual(jsonPath, json2);
        Assert.True(File.Exists(csvPath));
    }

    [Fact]
    public async Task MissingCredentials_FailBeforeConnect()
    {
        FakeCollector collector = new((_, _) => throw new InvalidOperationException("should not connect"));
        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(
            collector,
            username: " ",
            password: "");

        using StringReader reader = new("routerip\n10.0.0.1\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            auditor.AuditAsync(LiveAuditRouterCsvParser.Parse(reader)));
        Assert.Equal(0, collector.CallCount);
    }

    [Fact]
    public async Task DynamicV7Target_IsSnapshotConsistentInReport()
    {
        FakeCollector collector = new((ip, _) => Task.FromResult(Success(ip, "AP01.POP001.001.access")));
        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(collector, v7: "7.23.8");
        using StringReader reader = new("routerip\n10.0.0.50\n");
        LiveFleetAuditReport report = await auditor.AuditAsync(LiveAuditRouterCsvParser.Parse(reader));

        Assert.Equal(RouterOsUpgradeVersions.V6SteppingVersionText, report.V6SteppingTarget);
        Assert.Equal("7.23.8", report.V7Target);
    }

    private static LivePackageCompatibilityFleetAuditor CreateAuditor(
        FakeCollector collector,
        int maxConcurrent = 5,
        string username = "admin",
        string password = "secret",
        string v7 = "7.23.8")
    {
        string root = Directory.CreateTempSubdirectory("live-fleet-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "user-manager-6.49.23-mipsbe.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "user-manager-7.23.8-mipsbe.npk"), [1]);
        DirectoryCatalog catalog = new(root);
        FixedRouterOsV7TargetProvider target = new(v7);

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
            }),
            Options.Create(new MikroTikOptions
            {
                Username = username,
                Password = password,
                Port = 8728,
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            }),
            target,
            NullLogger<LivePackageCompatibilityFleetAuditor>.Instance);
    }

    private static ReadOnlyInventoryCollectionResult Success(string ip, string identity) =>
        new()
        {
            RouterIp = ip,
            CollectionStatus = LiveInventoryCollectionStatus.Succeeded,
            IdentityStatus = string.IsNullOrWhiteSpace(identity)
                ? LiveIdentityCollectionStatus.Unavailable
                : LiveIdentityCollectionStatus.Succeeded,
            Identity = string.IsNullOrWhiteSpace(identity) ? null : identity,
            RouterOsVersion = "6.49.10",
            Architecture = "mipsbe",
            Packages =
            [
                new InstalledPackageInfo("routeros-mipsbe", "6.49.10", false),
                new InstalledPackageInfo("system", "6.49.10", false, "routeros-mipsbe"),
                new InstalledPackageInfo("user-manager", "6.49.10", false),
            ],
            CommandsIssued =
            [
                "/system/identity/print",
                "/system/resource/print",
                "/system/package/print",
                "/system/routerboard/print",
            ],
        };

    private sealed class FakeCollector(
        Func<string, CancellationToken, Task<ReadOnlyInventoryCollectionResult>> factory)
        : IReadOnlyMikrotikInventoryCollector
    {
        public int CallCount { get; private set; }

        public async Task<ReadOnlyInventoryCollectionResult> CollectAsync(
            string routerIp,
            string username,
            string password,
            int port,
            TimeSpan connectionTimeout,
            string? routerName = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return await factory(routerIp, cancellationToken);
        }
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
