using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
public sealed class OspfStaticSummaryRedistributionTests
{
    [Fact]
    public void Configuration_MissingSetting_DefaultsToFalse()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        OspfSummaryRedistributionOptions opts = BindLikeHost(config);
        Assert.False(opts.RedistributeSummaryviaStatics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configuration_ExplicitValue_Binds(bool value)
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedistributeSummaryviaStatics"] = value ? "true" : "false",
                ["FirmwareUpgrade:Enabled"] = "false",
                ["MikroTik:Enabled"] = "false",
            })
            .Build();
        OspfSummaryRedistributionOptions opts = BindLikeHost(config);
        Assert.Equal(value, opts.RedistributeSummaryviaStatics);
    }

    [Fact]
    public void Configuration_IndependentOfFirmwareAndMikrotikEnabled()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedistributeSummaryviaStatics"] = "true",
                ["FirmwareUpgrade:Enabled"] = "false",
                ["MikroTik:Enabled"] = "false",
            })
            .Build();
        Assert.True(BindLikeHost(config).RedistributeSummaryviaStatics);
        Assert.False(config.GetValue("FirmwareUpgrade:Enabled", true));
        Assert.False(config.GetValue("MikroTik:Enabled", true));
    }

    private static OspfSummaryRedistributionOptions BindLikeHost(IConfiguration configuration)
    {
        OspfSummaryRedistributionOptions opts = new();
        opts.RedistributeSummaryviaStatics =
            configuration.GetValue("RedistributeSummaryviaStatics", false);
        return opts;
    }

    [Fact]
    public void BuildAcceptRule_UsesProtocolStatic_NotEqualsSyntax()
    {
        string rule = OspfStaticSummaryRedistributionConstants.BuildAcceptRule("10.16.11.0/24", 24);
        Assert.Contains("protocol static", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("protocol == static", rule, StringComparison.Ordinal);
        Assert.Contains("distance == 250", rule, StringComparison.Ordinal);
        Assert.Contains("dst == 10.16.11.0/24", rule, StringComparison.Ordinal);
        Assert.Contains("dst-len == 24", rule, StringComparison.Ordinal);
        Assert.Contains("{ accept }", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectPrefixes_DeduplicatesAndOrders()
    {
        OspfMigrationPlan plan = PlanWithRanges("10.16.11.0/24", "100.99.76.16/28", "10.16.11.0/24", "172.26.197.4/30");
        IReadOnlyList<OspfIpPrefix> prefixes =
            OspfStaticSummaryRedistributionService.CollectUniqueAreaRangePrefixes(plan);
        Assert.Equal(
            ["10.16.11.0/24", "100.99.76.16/28", "172.26.197.4/30"],
            prefixes.Select(static p => p.ToCidrString()).ToArray());
    }

    [Fact]
    public void CollectPrefixes_Empty_Throws()
    {
        OspfMigrationPlan plan = PlanWithRanges();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfStaticSummaryRedistributionService.CollectUniqueAreaRangePrefixes(plan));
        Assert.Contains("at least one area-range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpectedRules_MatchProductionExampleShape()
    {
        IReadOnlyList<OspfIpPrefix> prefixes = OspfStaticSummaryRedistributionService.CollectUniqueAreaRangePrefixes(
            PlanWithRanges("10.16.11.0/24", "100.99.76.16/28", "172.26.197.4/30"));
        IReadOnlyList<string> rules = OspfStaticSummaryRedistributionService.BuildExpectedRuleTexts(prefixes);

        Assert.Equal(4, rules.Count);
        Assert.Equal(
            "if (protocol static && distance == 250 && dst == 10.16.11.0/24 && dst-len == 24) { accept }",
            rules[0]);
        Assert.Equal(
            "if (protocol static && distance == 250 && dst == 100.99.76.16/28 && dst-len == 28) { accept }",
            rules[1]);
        Assert.Equal(
            "if (protocol static && distance == 250 && dst == 172.26.197.4/30 && dst-len == 30) { accept }",
            rules[2]);
        Assert.Equal("reject", rules[3]);
        Assert.All(rules.Take(3), r => Assert.DoesNotContain("protocol == static", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_PerformsNoMutations()
    {
        MutableStore store = MutableStore.Empty();
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: false);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Skipped);
        Assert.Empty(fake.MutatingCommands);
        Assert.Empty(store.Routes);
        Assert.Empty(store.FilterRules);
    }

    [Fact]
    public async Task Enabled_EmptyAreaRanges_FailsWithoutRedistribute()
    {
        MutableStore store = MutableStore.WithIdentity();
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeOutcome.OspfStaticSummaryRedistributionFailed, result.FailureOutcome);
        Assert.DoesNotContain(fake.MutatingCommands, c => c.Contains("redistribute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enabled_ReconcilesRoutesFilterAndRedistribute_Idempotent()
    {
        MutableStore store = MutableStore.WithIdentity();
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);
        OspfMigrationPlan plan = PlanWithRanges("10.16.11.0/24", "100.99.76.16/28", "172.26.197.4/30");

        OspfStaticSummaryRedistributionResult first = await service.ApplyAndVerifyAsync(
            fake,
            plan,
            CancellationToken.None);
        Assert.True(first.Succeeded, first.Detail);
        Assert.Equal(3, store.Routes.Count);
        Assert.All(store.Routes, r =>
        {
            Assert.Equal("yes", r["blackhole"]);
            Assert.Equal("250", r["distance"]);
        });
        Assert.Equal(4, store.FilterRules.Count(r => r["chain"] == "ospf-v2-out"));
        Assert.Equal("reject", store.FilterRules.Last(r => r["chain"] == "ospf-v2-out")["rule"]);
        Assert.Equal("ospf-v2-out", store.Instance["out-filter-chain"]);
        Assert.Contains("static", store.Instance["redistribute"], StringComparison.OrdinalIgnoreCase);

        int routeAdds = fake.MutatingCommands.Count(c => c.StartsWith("/ip/route/add", StringComparison.Ordinal));
        int filterAdds = fake.MutatingCommands.Count(c => c.StartsWith("/routing/filter/rule/add", StringComparison.Ordinal));

        OspfStaticSummaryRedistributionResult second = await service.ApplyAndVerifyAsync(
            fake,
            plan,
            CancellationToken.None);
        Assert.True(second.Succeeded, second.Detail);
        Assert.Equal(3, store.Routes.Count);
        Assert.Equal(4, store.FilterRules.Count(r => r["chain"] == "ospf-v2-out"));
        Assert.Equal(
            routeAdds,
            fake.MutatingCommands.Count(c => c.StartsWith("/ip/route/add", StringComparison.Ordinal)));
        Assert.Equal(
            filterAdds,
            fake.MutatingCommands.Count(c => c.StartsWith("/routing/filter/rule/add", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Enabled_AttachesFilterBeforeEnablingRedistribute()
    {
        MutableStore store = MutableStore.WithIdentity();
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Detail);

        int attachIdx = fake.MutatingCommands.FindIndex(c =>
            c.Contains("/routing/ospf/instance/set", StringComparison.Ordinal)
            && c.Contains("out-filter-chain", StringComparison.Ordinal));
        int redistributeIdx = fake.MutatingCommands.FindIndex(c =>
            c.Contains("/routing/ospf/instance/set", StringComparison.Ordinal)
            && c.Contains("redistribute", StringComparison.Ordinal));
        Assert.True(attachIdx >= 0, "out-filter-chain attach missing");
        Assert.True(redistributeIdx >= 0, "redistribute enable missing");
        Assert.True(attachIdx < redistributeIdx, "filter must attach before redistribute enable");
    }

    [Fact]
    public async Task Enabled_ConflictingStaticRoute_FailsClosed()
    {
        MutableStore store = MutableStore.WithIdentity();
        store.Routes.Add(new Dictionary<string, string>
        {
            [".id"] = "*x",
            ["dst-address"] = "10.16.11.0/24",
            ["gateway"] = "1.2.3.4",
            ["distance"] = "1",
        });
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Conflicting", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fake.MutatingCommands, c => c.Contains("redistribute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enabled_PreservesUnrelatedStaticRoutesAndFilterChains()
    {
        MutableStore store = MutableStore.WithIdentity();
        store.Routes.Add(new Dictionary<string, string>
        {
            [".id"] = "*u",
            ["dst-address"] = "0.0.0.0/0",
            ["gateway"] = "10.16.248.77",
            ["distance"] = "1",
        });
        store.FilterRules.Add(new Dictionary<string, string>
        {
            [".id"] = "*other",
            ["chain"] = "bgp-out",
            ["rule"] = "accept",
        });
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Contains(store.Routes, r => r["dst-address"] == "0.0.0.0/0");
        Assert.Contains(store.FilterRules, r => r["chain"] == "bgp-out");
    }

    [Fact]
    public async Task Enabled_PreservesExistingConnectedRedistribute()
    {
        MutableStore store = MutableStore.WithIdentity();
        store.Instance["redistribute"] = "connected";
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Contains("connected", store.Instance["redistribute"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("static", store.Instance["redistribute"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enabled_StaleManagedChain_IsReplaced()
    {
        MutableStore store = MutableStore.WithIdentity();
        store.FilterRules.Add(new Dictionary<string, string>
        {
            [".id"] = "*stale",
            ["chain"] = "ospf-v2-out",
            ["rule"] = "accept",
        });
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        List<Dictionary<string, string>> chain = store.FilterRules
            .Where(r => r["chain"] == "ospf-v2-out")
            .ToList();
        Assert.Equal(2, chain.Count);
        Assert.DoesNotContain(chain, r => r["rule"] == "accept" && !r["rule"].Contains("protocol static", StringComparison.Ordinal));
        Assert.Equal("reject", chain[^1]["rule"]);
    }

    [Fact]
    public async Task Enabled_UsesPlaceBefore_RejectIsLast()
    {
        MutableStore store = MutableStore.WithIdentity();
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24", "100.99.76.16/28"),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Contains(fake.MutatingCommands, c => c.Contains("place-before", StringComparison.Ordinal));
        List<string> chainRules = store.FilterRules
            .Where(r => r["chain"] == "ospf-v2-out")
            .Select(r => r["rule"])
            .ToList();
        Assert.Equal(3, chainRules.Count);
        Assert.Equal("reject", chainRules[^1]);
        Assert.Contains("10.16.11.0/24", chainRules[0], StringComparison.Ordinal);
        Assert.Contains("100.99.76.16/28", chainRules[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_RepairsIncorrectlyOrderedChain()
    {
        MutableStore store = MutableStore.WithIdentity();
        store.FilterRules.Add(new Dictionary<string, string>
        {
            [".id"] = "*rej",
            ["chain"] = "ospf-v2-out",
            ["rule"] = "reject",
        });
        store.FilterRules.Add(new Dictionary<string, string>
        {
            [".id"] = "*acc",
            ["chain"] = "ospf-v2-out",
            ["rule"] = OspfStaticSummaryRedistributionConstants.BuildAcceptRule("10.16.11.0/24", 24),
        });
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        List<string> chainRules = store.FilterRules
            .Where(r => r["chain"] == "ospf-v2-out")
            .Select(r => r["rule"])
            .ToList();
        Assert.Equal(2, chainRules.Count);
        Assert.Contains("protocol static", chainRules[0], StringComparison.Ordinal);
        Assert.Equal("reject", chainRules[1]);
    }

    [Fact]
    public async Task Enabled_FilterOrderVerifyFailure_BlocksRedistribute()
    {
        MutableStore store = MutableStore.WithIdentity();
        store.ScrambleFilterOrderOnPrintAfterAdd = true;
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("order", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fake.MutatingCommands, c => c.Contains("redistribute", StringComparison.Ordinal));
        Assert.DoesNotContain(
            fake.MutatingCommands,
            c => c.Contains("out-filter-chain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enabled_PostEnableVerifyFailure_CompensatesOriginalState()
    {
        MutableStore store = MutableStore.WithIdentity();
        store.Instance["redistribute"] = "connected";
        store.CorruptOutFilterAfterRedistributeSet = true;
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.CompensationAttempted);
        Assert.True(result.CompensationSucceeded);
        Assert.Contains("connected", store.Instance["redistribute"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("static", store.Instance["redistribute"], StringComparison.OrdinalIgnoreCase);
        Assert.False(store.Instance.ContainsKey("out-filter-chain")
                     && !string.IsNullOrWhiteSpace(store.Instance["out-filter-chain"]));
        Assert.Contains("Compensation restored", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enabled_CompensationFailure_IsReported()
    {
        MutableStore store = MutableStore.WithIdentity();
        store.CorruptOutFilterAfterRedistributeSet = true;
        store.FailCompensation = true;
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.CompensationAttempted);
        Assert.False(result.CompensationSucceeded);
        Assert.Contains("Compensation failed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual inspection", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "static")]
    [InlineData("", "static")]
    [InlineData("static", "static")]
    [InlineData("connected", "connected,static")]
    [InlineData("connected, rip", "connected,rip,static")]
    [InlineData(" static , connected ", "connected,static")]
    public void MergeRedistribute_PreservesOthersAndDedupes(string? existing, string expected)
    {
        Assert.Equal(expected, OspfStaticSummaryRedistributionService.MergeRedistributeWithStatic(existing));
    }

    [Fact]
    public async Task Enabled_AlreadyStatic_DoesNotRewriteRedistribute()
    {
        MutableStore store = MutableStore.WithIdentity();
        store.Instance["redistribute"] = "static";
        CommandRecordingConnection fake = store.Connect();
        OspfStaticSummaryRedistributionService service = CreateService(enabled: true);

        OspfStaticSummaryRedistributionResult result = await service.ApplyAndVerifyAsync(
            fake,
            PlanWithRanges("10.16.11.0/24"),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal(1, fake.MutatingCommands.Count(c => c.Contains("out-filter-chain", StringComparison.Ordinal)));
        Assert.Equal(0, fake.MutatingCommands.Count(c => c.Contains("redistribute", StringComparison.Ordinal)));
        Assert.Equal("static", store.Instance["redistribute"]);
    }

    private static OspfStaticSummaryRedistributionService CreateService(bool enabled) =>
        new(
            new InventoryOps(),
            Options.Create(new OspfSummaryRedistributionOptions { RedistributeSummaryviaStatics = enabled }),
            NullLogger<OspfStaticSummaryRedistributionService>.Instance);

    private static OspfMigrationPlan PlanWithRanges(params string[] cidrs) => new()
    {
        ImportId = Guid.NewGuid(),
        JobId = "job",
        RouterIp = "198.18.240.11",
        Identity = "BR01.POP011.016.Tommy",
        SourceRouterOsVersion = "7.25.1",
        Architecture = "mipsbe",
        SourceKind = OspfCaptureSourceKind.RouterOsV7,
        InstanceName = "default",
        RouterId = "10.16.0.11",
        TargetInstanceName = OspfFleetNaming.Ros7InstanceName,
        TargetBackboneAreaName = OspfFleetNaming.Ros7BackboneAreaName,
        TargetSiteAreaName = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
        LoopbackCidr = "10.16.0.11/32",
        LoopbackInterfaceName = "Loopback",
        Areas =
        [
            new OspfPlanArea
            {
                Name = "backbone",
                AreaId = "0.0.0.0",
                IsBackbone = true,
                TargetType = "default",
            },
            new OspfPlanArea
            {
                Name = "BR01.POP011.016.Tommy",
                AreaId = "10.16.0.11",
                IsBackbone = false,
                TargetType = "stub",
                DefaultCost = "1",
            },
        ],
        AreaRanges = cidrs
            .Select(c => new OspfPlanAreaRange
            {
                AreaName = "BR01.POP011.016.Tommy",
                RangeCidr = c,
            })
            .ToList(),
        ActiveBackhauls =
        [
            new OspfPlanBackhaulTemplate
            {
                AreaName = "backbone",
                Interface = "eth-01_BH01.Tommy-Shylock",
                NetworkCidr = "10.16.248.76/30",
                Cost = 10,
                Type = "ptp",
            },
        ],
        PassiveLoopbacks = [],
        PassiveSiteNetworks = [],
        ValidationErrors = [],
        NormalisationNotes = [],
        ContentHash = "test",
    };

    private sealed class InventoryOps : IOspfMikrotikOperations
    {
        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OspfV7Inventory
            {
                Instances =
                [
                    new OspfInstanceRow
                    {
                        Id = "*i",
                        Name = "default-v2",
                        RouterId = "10.16.0.11",
                        Version = "2",
                    },
                ],
                Areas =
                [
                    new OspfAreaRow
                    {
                        Id = "*ab",
                        Name = "backbone-v2",
                        AreaId = "0.0.0.0",
                        Instance = "default-v2",
                        Type = "default",
                    },
                    new OspfAreaRow
                    {
                        Id = "*as",
                        Name = "BR01.POP011.016.Tommy-v2",
                        AreaId = "10.16.0.11",
                        Instance = "default-v2",
                        Type = "stub",
                        DefaultCost = "1",
                    },
                ],
            });

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Minimal(managementHost));
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
    }

    private sealed class MutableStore
    {
        public List<Dictionary<string, string>> Routes { get; } = [];
        public List<Dictionary<string, string>> FilterRules { get; } = [];
        public Dictionary<string, string> Instance { get; private set; } = new(StringComparer.Ordinal);
        public bool CorruptOutFilterAfterRedistributeSet { get; set; }
        public bool FailCompensation { get; set; }
        public bool ScrambleFilterOrderOnPrintAfterAdd { get; set; }
        public bool FilterAddsCompleted { get; set; }
        public int RedistributeSetCount { get; set; }
        private int _seq;

        public static MutableStore Empty() => new();

        public static MutableStore WithIdentity()
        {
            MutableStore store = new()
            {
                Instance = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [".id"] = "*i",
                    ["name"] = "default-v2",
                    ["version"] = "2",
                    ["router-id"] = "10.16.0.11",
                    ["redistribute"] = "",
                },
            };
            return store;
        }

        public CommandRecordingConnection Connect()
        {
            CommandRecordingConnection c = new(this);
            c.Attach();
            return c;
        }

        public string NextId(string prefix) => $"{prefix}{Interlocked.Increment(ref _seq)}";
    }

    private sealed class CommandRecordingConnection : DisposeTrackingConnection
    {
        private readonly MutableStore _store;
        private readonly TikFakeConnection _fake;
        private ITikCommand? _pending;

        public CommandRecordingConnection(MutableStore store)
            : this(store, new TikFakeConnection())
        {
        }

        private CommandRecordingConnection(MutableStore store, TikFakeConnection fake)
            : base(fake)
        {
            _store = store;
            _fake = fake;
        }

        public List<string> MutatingCommands { get; } = [];

        public void Attach()
        {
            _fake.WithResponse(
                    static rows => rows.First() == "/ip/route/print",
                    _ =>
                    [
                        .._store.Routes.Select(static r => new TikFakeReSentence(new Dictionary<string, string>(r))),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/ip/route/add",
                    _ =>
                    {
                        Dictionary<string, string> row = new(StringComparer.Ordinal)
                        {
                            [".id"] = _store.NextId("*r"),
                            ["dst-address"] = Param("dst-address") ?? "",
                            ["distance"] = Param("distance") ?? "",
                        };
                        if (Param("blackhole") is not null)
                        {
                            row["blackhole"] = Param("blackhole")!;
                        }

                        _store.Routes.Add(row);
                        Record("/ip/route/add");
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/filter/rule/print",
                    _ =>
                    {
                        IEnumerable<Dictionary<string, string>> rules = _store.FilterRules;
                        if (_store.ScrambleFilterOrderOnPrintAfterAdd && _store.FilterAddsCompleted)
                        {
                            rules = _store.FilterRules.AsEnumerable().Reverse();
                        }

                        return
                        [
                            ..rules.Select(static r => new TikFakeReSentence(new Dictionary<string, string>(r))),
                            new TikFakeDoneSentence(),
                        ];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/filter/rule/add",
                    _ =>
                    {
                        Dictionary<string, string> row = new(StringComparer.Ordinal)
                        {
                            [".id"] = _store.NextId("*f"),
                            ["chain"] = Param("chain") ?? "",
                            ["rule"] = Param("rule") ?? "",
                        };
                        string? placeBefore = Param("place-before");
                        if (!string.IsNullOrWhiteSpace(placeBefore))
                        {
                            int idx = _store.FilterRules.FindIndex(r => r[".id"] == placeBefore);
                            if (idx >= 0)
                            {
                                _store.FilterRules.Insert(idx, row);
                            }
                            else
                            {
                                _store.FilterRules.Add(row);
                            }

                            Record($"/routing/filter/rule/add place-before={placeBefore}");
                        }
                        else
                        {
                            _store.FilterRules.Add(row);
                            Record("/routing/filter/rule/add");
                        }

                        if (string.Equals(row["rule"], "reject", StringComparison.Ordinal)
                            || placeBefore is not null)
                        {
                            _store.FilterAddsCompleted = _store.FilterRules.Count(r => r["chain"] == "ospf-v2-out") > 1
                                                          || placeBefore is not null;
                        }

                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/filter/rule/remove",
                    _ =>
                    {
                        string? id = Param(".id");
                        _store.FilterRules.RemoveAll(r => r[".id"] == id);
                        Record("/routing/filter/rule/remove");
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/instance/print",
                    _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>(_store.Instance)),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/instance/set",
                    _ =>
                    {
                        if (Param("out-filter-chain") is { } chain)
                        {
                            _store.Instance["out-filter-chain"] = chain;
                            Record("/routing/ospf/instance/set out-filter-chain");
                        }

                        if (Param("redistribute") is { } redistribute)
                        {
                            _store.RedistributeSetCount++;
                            if (_store.FailCompensation && _store.RedistributeSetCount > 1)
                            {
                                Record("/routing/ospf/instance/set COMPENSATION_FAIL");
                                throw new InvalidOperationException("forced compensation failure");
                            }

                            _store.Instance["redistribute"] = redistribute;
                            Record("/routing/ospf/instance/set redistribute");
                            if (_store.CorruptOutFilterAfterRedistributeSet
                                && redistribute.Contains("static", StringComparison.OrdinalIgnoreCase)
                                && _store.RedistributeSetCount == 1)
                            {
                                _store.Instance.Remove("out-filter-chain");
                            }
                        }

                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/instance/unset",
                    _ =>
                    {
                        if (_store.FailCompensation)
                        {
                            Record("/routing/ospf/instance/unset COMPENSATION_FAIL");
                            throw new InvalidOperationException("forced compensation unset failure");
                        }

                        string property = Param("value-name") ?? "out-filter-chain";
                        _store.Instance.Remove(property);
                        Record($"/routing/ospf/instance/unset {property}");
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/system/resource/print",
                    static _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string> { ["version"] = "7.25.1" }),
                        new TikFakeDoneSentence(),
                    ]);
        }

        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            _pending = cmd;
            return cmd;
        }

        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            _pending = cmd;
            return cmd;
        }

        private string? Param(string name) =>
            _pending?.Parameters.FirstOrDefault(p => p.Name == name)?.Value;

        private void Record(string label) => MutatingCommands.Add(label);
    }
}
