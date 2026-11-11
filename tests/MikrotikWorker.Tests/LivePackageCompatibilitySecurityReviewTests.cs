using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests;

/// <summary>
/// Focused security/correctness coverage for the live read-only fleet audit.
/// </summary>
public sealed class LivePackageCompatibilitySecurityReviewTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ActivationGate_AllCombinations(bool cli, bool config, bool expectedPermit)
    {
        string? failure = LivePackageCompatibilityAuditRunner.DescribeActivationFailure(cli, config);
        if (expectedPermit)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.NotNull(failure);
        }
    }

    [Fact]
    public void LiveAuditFlag_TakesPrecedenceOverFixtureFlag_WithoutFallingThrough()
    {
        string[] args =
        [
            "--audit-package-compatibility-live",
            "--audit-package-compatibility",
            "--input",
            "routers.csv",
        ];
        Assert.True(LivePackageCompatibilityAuditRunner.IsLiveAuditRequested(args));
        Assert.True(PackageCompatibilityAuditRunner.IsFixtureAuditRequested(args));
        // Program checks live first; fixture is never reached when live is requested.
    }

    [Fact]
    public void LiveAudit_DoesNotEnableFirmwareUpgradeOptions()
    {
        Assert.False(new FirmwareUpgradeOptions().Enabled);
        Assert.False(new LivePackageCompatibilityAuditOptions().Enabled);
        Assert.False(new MikroTikOptions().Enabled);
    }

    [Theory]
    [InlineData("/tool/fetch")]
    [InlineData("/system/reboot")]
    [InlineData("/system/backup/save")]
    [InlineData("/export")]
    [InlineData("/system/routerboard/upgrade")]
    [InlineData("/system/package/update")]
    [InlineData("/system/package/disable")]
    [InlineData("/system/package/uninstall")]
    [InlineData("/file/print")]
    [InlineData("/system/identity/print ")]
    [InlineData("/system/identity/print\n")]
    [InlineData("/system/package/print; /system/reboot")]
    [InlineData("/system/package")]
    [InlineData("system/package/print")]
    public void ReadOnlyExecutor_RejectsDisallowedCommands_ExactMatchOnly(string command)
    {
        Assert.Throws<InvalidOperationException>(() => ReadOnlyTikCommandExecutor.EnsureAllowed(command));
    }

    [Theory]
    [InlineData("/system/identity/print")]
    [InlineData("/system/resource/print")]
    [InlineData("/system/package/print")]
    [InlineData("/system/routerboard/print")]
    public void ReadOnlyExecutor_AllowsExactPermittedCommands(string command)
    {
        ReadOnlyTikCommandExecutor.EnsureAllowed(command);
    }

    [Fact]
    public async Task Collector_IssuesExactPrintCommands_WithoutParameters()
    {
        CommandRecordingConnection recording = new(
            FakeMikrotikConnections.CreateWithPackageInventory());
        ReadOnlyMikrotikInventoryCollector collector = new(
            new ScriptedMikrotikConnectionFactory(_ => recording),
            NullLogger<ReadOnlyMikrotikInventoryCollector>.Instance);

        ReadOnlyInventoryCollectionResult result = await collector.CollectAsync(
            "10.0.0.70",
            "admin",
            "secret",
            8728,
            TimeSpan.FromSeconds(5));

        Assert.Equal(
            new[]
            {
                "/system/identity/print",
                "/system/resource/print",
                "/system/package/print",
                "/system/routerboard/print",
            },
            recording.Commands);
        Assert.Equal(recording.Commands, result.CommandsIssued);
        Assert.Equal(1, recording.DisposeCount);
    }

    [Fact]
    public void Collector_HasNoCommandStringApi()
    {
        MethodInfo[] methods = typeof(ReadOnlyMikrotikInventoryCollector).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(
            methods,
            static m => m.GetParameters().Any(static p =>
                p.ParameterType == typeof(string)
                && p.Name is not null
                && p.Name.Contains("command", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Authentication_UsesTikConnectionLoginException()
    {
        Assert.True(ReadOnlyMikrotikInventoryCollector.IsAuthenticationFailure(
            new TikConnectionLoginException(new Exception("denied"))));
        Assert.True(ReadOnlyMikrotikInventoryCollector.IsAuthenticationFailure(
            new InvalidOperationException("wrapper", new TikConnectionLoginException(new Exception("denied")))));
        Assert.True(ReadOnlyMikrotikInventoryCollector.IsAuthenticationFailure(
            new InvalidOperationException("cannot log in")));
        Assert.True(ReadOnlyMikrotikInventoryCollector.IsAuthenticationFailure(
            new InvalidOperationException("invalid user name or password")));
        Assert.False(ReadOnlyMikrotikInventoryCollector.IsAuthenticationFailure(
            new IOException("connection refused")));
        Assert.False(ReadOnlyMikrotikInventoryCollector.IsAuthenticationFailure(
            new TimeoutException("timed out")));
        Assert.False(ReadOnlyMikrotikInventoryCollector.IsAuthenticationFailure(
            new InvalidOperationException("TLS authentication cipher unavailable")));
    }

    [Fact]
    public async Task AuthenticationFailure_NeverProducesPartialOrCompatibleInventory()
    {
        ScriptedMikrotikConnectionFactory factory = new(_ =>
            throw new TikConnectionLoginException(new Exception("denied")));
        ReadOnlyInventoryCollectionResult result = await new ReadOnlyMikrotikInventoryCollector(
                factory,
                NullLogger<ReadOnlyMikrotikInventoryCollector>.Instance)
            .CollectAsync("10.0.0.71", "admin", "SuperSecret!", 8728, TimeSpan.FromSeconds(5));

        Assert.Equal(LiveInventoryCollectionStatus.AuthenticationFailed, result.CollectionStatus);
        Assert.False(result.HasUsableInventory);
        Assert.Equal("Authentication failed.", result.FailureDetail);
        Assert.DoesNotContain("SuperSecret!", result.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(result.Packages);
    }

    [Fact]
    public async Task ConnectionRefused_IsConnectionFailed_NotCompatible()
    {
        ScriptedMikrotikConnectionFactory factory = new(_ => throw new IOException("connection refused"));
        ReadOnlyInventoryCollectionResult result = await new ReadOnlyMikrotikInventoryCollector(
                factory,
                NullLogger<ReadOnlyMikrotikInventoryCollector>.Instance)
            .CollectAsync("10.0.0.72", "admin", "x", 8728, TimeSpan.FromSeconds(5));

        Assert.Equal(LiveInventoryCollectionStatus.ConnectionFailed, result.CollectionStatus);
        Assert.False(result.HasUsableInventory);
        Assert.Equal("Connection failed.", result.FailureDetail);
    }

    [Fact]
    public async Task EmptyIdentity_RemainsEligible_AndInventoryCanSucceed()
    {
        TikFakeConnection fake = FakeMikrotikConnections.CreateWithPackageInventory(identity: " ");
        ReadOnlyInventoryCollectionResult result = await new ReadOnlyMikrotikInventoryCollector(
                new ScriptedMikrotikConnectionFactory(_ => fake),
                NullLogger<ReadOnlyMikrotikInventoryCollector>.Instance)
            .CollectAsync("10.0.0.73", "admin", "x", 8728, TimeSpan.FromSeconds(5));

        Assert.Equal(LiveIdentityCollectionStatus.Unavailable, result.IdentityStatus);
        Assert.Null(result.Identity);
        Assert.Equal(LiveInventoryCollectionStatus.Succeeded, result.CollectionStatus);
        Assert.True(result.HasUsableInventory);
    }

    [Fact]
    public async Task MissingVersion_IsPartial_NotUsable()
    {
        TikFakeConnection fake = BuildInventoryFake(version: null, architecture: "mipsbe", includePackages: true);
        ReadOnlyInventoryCollectionResult result = await Collect(fake);
        Assert.Equal(LiveInventoryCollectionStatus.Partial, result.CollectionStatus);
        Assert.False(result.HasUsableInventory);
    }

    [Fact]
    public async Task MissingArchitecture_IsPartial_NotUsable()
    {
        TikFakeConnection fake = BuildInventoryFake(version: "6.49.10", architecture: null, includePackages: true);
        ReadOnlyInventoryCollectionResult result = await Collect(fake);
        Assert.Equal(LiveInventoryCollectionStatus.Partial, result.CollectionStatus);
        Assert.False(result.HasUsableInventory);
    }

    [Fact]
    public async Task MissingPackages_IsPartial_NotUsable()
    {
        TikFakeConnection fake = BuildInventoryFake(version: "6.49.10", architecture: "mipsbe", includePackages: false);
        ReadOnlyInventoryCollectionResult result = await Collect(fake);
        Assert.Equal(LiveInventoryCollectionStatus.Partial, result.CollectionStatus);
        Assert.False(result.HasUsableInventory);
    }

    [Fact]
    public async Task UnparsablePackageRow_MarksIncomplete()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/identity/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["name"] = "AP01.POP001.001.access" }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["version"] = "6.49.10",
                        ["architecture-name"] = "mipsbe",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/package/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["version"] = "6.49.10",
                        ["disabled"] = "false",
                    }),
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["name"] = "routeros-mipsbe",
                        ["version"] = "6.49.10",
                        ["disabled"] = "false",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithTrap(static rows => rows.First() == "/system/routerboard/print", "no such command");

        ReadOnlyInventoryCollectionResult result = await Collect(fake);
        Assert.Equal(LiveInventoryCollectionStatus.Partial, result.CollectionStatus);
        Assert.False(result.HasUsableInventory);
        Assert.Contains("unparsable", result.FailureDetail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Packages, static p => p.Name == "routeros-mipsbe");
    }

    [Fact]
    public async Task PackageInventory_PreservesBundleAndDoesNotInferFromNames()
    {
        TikFakeConnection fake = FakeMikrotikConnections.CreateWithPackageInventory(
            version: "6.49.10",
            architecture: "mipsbe",
            mainPackageName: "routeros-mipsbe",
            includeBundled: true);

        ReadOnlyInventoryCollectionResult result = await Collect(fake);
        Assert.Equal(LiveInventoryCollectionStatus.Succeeded, result.CollectionStatus);
        InstalledPackageInfo ipv6 = Assert.Single(result.Packages, static p => p.Name == "ipv6");
        Assert.True(ipv6.IsBundledComponent);
        Assert.Equal("routeros-mipsbe", ipv6.Bundle);
        Assert.True(ipv6.Disabled);

        InstalledPackageInfo userManager = Assert.Single(result.Packages, static p => p.Name == "user-manager");
        Assert.False(userManager.IsBundledComponent);
        Assert.Null(userManager.Bundle);
    }

    [Fact]
    public async Task V7PackageLayout_PreservesIndependentMainAndOptionals()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/identity/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["name"] = "AP01.POP001.001.access" }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["version"] = "7.15.3",
                        ["architecture-name"] = "arm",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/package/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["name"] = "routeros",
                        ["version"] = "7.15.3",
                        ["disabled"] = "false",
                    }),
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["name"] = "wifiwave2",
                        ["version"] = "7.15.3",
                        ["disabled"] = "false",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithTrap(static rows => rows.First() == "/system/routerboard/print", "no such command");

        ReadOnlyInventoryCollectionResult result = await Collect(fake);
        Assert.Equal(LiveInventoryCollectionStatus.Succeeded, result.CollectionStatus);
        Assert.All(result.Packages, static p => Assert.False(p.IsBundledComponent));
        Assert.Contains(result.Packages, static p => p.Name == "routeros");
        Assert.Contains(result.Packages, static p => p.Name == "wifiwave2");
    }

    [Fact]
    public async Task ConnectTimeout_IsPassedToConnectionFactory()
    {
        TimeSpan? seen = null;
        ScriptedMikrotikConnectionFactory factory = new(opts =>
        {
            seen = opts.ConnectTimeout;
            return FakeMikrotikConnections.CreateWithPackageInventory();
        });

        _ = await new ReadOnlyMikrotikInventoryCollector(
                factory,
                NullLogger<ReadOnlyMikrotikInventoryCollector>.Instance)
            .CollectAsync("10.0.0.74", "admin", "x", 8728, TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), seen);
    }

    [Fact]
    public async Task PartialInventory_IsNeverAssessedCompatible()
    {
        FakeCollector collector = new((_, _) => Task.FromResult(new ReadOnlyInventoryCollectionResult
        {
            RouterIp = "10.0.0.75",
            CollectionStatus = LiveInventoryCollectionStatus.Partial,
            IdentityStatus = LiveIdentityCollectionStatus.Succeeded,
            Identity = "AP01.POP001.001.access",
            RouterOsVersion = "6.49.10",
            Architecture = "mipsbe",
            Packages = [new InstalledPackageInfo("routeros-mipsbe", "6.49.10", false)],
            FailureDetail = "One or more required inventory fields were unavailable.",
        }));

        LiveFleetAuditReport report = await CreateAuditor(collector).AuditAsync(Parse("routerip\n10.0.0.75\n"));
        LiveFleetRouterAuditRecord row = Assert.Single(report.Routers);
        Assert.Null(row.Compatibility);
        Assert.Equal(0, report.Summary.FullyCompatibleDevices);
        Assert.Equal(1, report.Summary.PartialInventories);
    }

    [Fact]
    public async Task TotalAuditTimeout_MarksIncomplete()
    {
        FakeCollector collector = new(async (ip, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success(ip);
        });

        LivePackageCompatibilityFleetAuditor auditor = CreateAuditor(
            collector,
            totalTimeout: TimeSpan.FromMilliseconds(50),
            maxConcurrent: 1);

        LiveFleetAuditReport report = await auditor.AuditAsync(Parse(
            """
            routerip
            10.0.0.80
            10.0.0.81
            """));

        Assert.True(report.Summary.Cancelled);
        Assert.False(report.Summary.Completed);
        Assert.Contains(report.Routers, static r =>
            r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Cancelled);
        Assert.Contains("timed out", report.Summary.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Report_EscapesCsvAndOmitsSecrets()
    {
        FakeCollector collector = new((_, _) => Task.FromResult(Success(
            "10.0.0.90",
            identity: "AP,\"evil\"\nname",
            routerName: "Edge, \"Site\"")));

        // Inject router name via CSV (quoted field with embedded quotes).
        LiveFleetAuditReport report = await CreateAuditor(collector).AuditAsync(Parse(
            "routerip,routername\n10.0.0.90,\"Edge, \"\"Site\"\"\"\n"));

        string dir = Directory.CreateTempSubdirectory("live-sec-report-").FullName;
        (string csvPath, string jsonPath) = await LivePackageCompatibilityAuditReportWriter.WriteAsync(dir, report);
        string csv = await File.ReadAllTextAsync(csvPath);
        string json = await File.ReadAllTextAsync(jsonPath);

        Assert.Contains("\"Edge, \"\"Site\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"AP,\"\"evil\"\"\nname\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("summary").GetProperty("completed").GetBoolean()
            && doc.RootElement.GetProperty("summary").GetProperty("cancelled").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("summary").GetProperty("completed").GetBoolean());
    }

    [Fact]
    public void OutputPath_UnderFirmwareFiles_IsRejected()
    {
        string firmware = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fw-root-" + Guid.NewGuid().ToString("N")));
        string nested = Path.Combine(firmware, "Reports");
        Assert.True(LivePackageCompatibilityAuditRunner.IsPathEqualOrUnder(nested, firmware));
        Assert.True(LivePackageCompatibilityAuditRunner.IsPathEqualOrUnder(firmware, firmware));
        Assert.False(LivePackageCompatibilityAuditRunner.IsPathEqualOrUnder(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "other-" + Guid.NewGuid().ToString("N"))),
            firmware));
    }

    [Fact]
    public async Task SummaryCounts_ReconcileWithPerRouterRows()
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

            return Task.FromResult(Success(ip));
        });

        LiveFleetAuditReport report = await CreateAuditor(collector).AuditAsync(Parse(
            """
            routerip
            10.0.0.1
            10.0.0.2
            not-an-ip
            10.0.0.1
            """));

        Assert.Equal(4, report.Summary.TotalInputRows);
        Assert.Equal(2, report.Summary.ValidUniqueAddresses);
        Assert.Equal(1, report.Summary.DuplicateAddresses);
        Assert.Equal(1, report.Summary.InvalidInputRows);
        Assert.Equal(1, report.Summary.SuccessfullyCollected);
        Assert.Equal(1, report.Summary.ConnectionFailures);
        Assert.Equal(report.Routers.Count, report.Summary.TotalInputRows);
    }

    private static async Task<ReadOnlyInventoryCollectionResult> Collect(TikFakeConnection fake) =>
        await new ReadOnlyMikrotikInventoryCollector(
                new ScriptedMikrotikConnectionFactory(_ => fake),
                NullLogger<ReadOnlyMikrotikInventoryCollector>.Instance)
            .CollectAsync("10.0.0.100", "admin", "x", 8728, TimeSpan.FromSeconds(5));

    private static TikFakeConnection BuildInventoryFake(
        string? version,
        string? architecture,
        bool includePackages)
    {
        Dictionary<string, string> resource = new();
        if (version is not null)
        {
            resource["version"] = version;
        }

        if (architecture is not null)
        {
            resource["architecture-name"] = architecture;
        }

        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/identity/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["name"] = "AP01.POP001.001.access" }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(resource),
                    new TikFakeDoneSentence(),
                ]);

        if (includePackages)
        {
            fake = fake.WithResponse(
                static rows => rows.First() == "/system/package/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["name"] = "routeros-mipsbe",
                        ["version"] = version ?? "6.49.10",
                        ["disabled"] = "false",
                    }),
                    new TikFakeDoneSentence(),
                ]);
        }
        else
        {
            fake = fake.WithResponse(
                static rows => rows.First() == "/system/package/print",
                [new TikFakeDoneSentence()]);
        }

        return fake.WithTrap(static rows => rows.First() == "/system/routerboard/print", "no such command");
    }

    private static LiveAuditRouterCsvParser.ParseResult Parse(string csv)
    {
        using StringReader reader = new(csv);
        return LiveAuditRouterCsvParser.Parse(reader);
    }

    private static ReadOnlyInventoryCollectionResult Success(
        string ip,
        string identity = "AP01.POP001.001.access",
        string? routerName = null) =>
        new()
        {
            RouterIp = ip,
            RouterName = routerName,
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

    private static LivePackageCompatibilityFleetAuditor CreateAuditor(
        FakeCollector collector,
        TimeSpan? totalTimeout = null,
        int maxConcurrent = 5)
    {
        string root = Directory.CreateTempSubdirectory("live-sec-").FullName;
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
