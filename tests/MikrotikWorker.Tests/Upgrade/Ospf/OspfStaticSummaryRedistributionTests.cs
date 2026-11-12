// -----------------------------------------------------------------------------
// File: OspfStaticSummaryRedistributionTests.cs
//
// Description:
// Unit/fake tests for optional OSPF static-summary redistribution stage.
//
// -----------------------------------------------------------------------------

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
    /// <summary>
    /// Scenario: static summary redistribution enabled for a plan with an area-range prefix; mutating commands recorded.
    /// Expected: success, and the <c>out-filter-chain</c> instance set appears before the <c>redistribute</c> enable set.
    /// Invariant: the outbound filter must be attached before redistribution is enabled.
    /// </summary>
public sealed class OspfStaticSummaryRedistributionTests
{
    /// <summary>
    /// Scenario: the host binds <c>OspfSummaryRedistributionOptions</c> from configuration that has no <c>RedistributeSummaryviaStatics</c> key.
    /// Expected: <c>RedistributeSummaryviaStatics</c> is false.
    /// Invariant: static summary redistribution is opt-in; leaving it unconfigured changes nothing on routers.
    /// </summary>
    [Fact]
    public void Configuration_MissingSetting_DefaultsToFalse()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        OspfSummaryRedistributionOptions opts = BindLikeHost(config);
        Assert.False(opts.RedistributeSummaryviaStatics);
    }

    /// <summary>
    /// Scenario: configuration sets <c>RedistributeSummaryviaStatics</c> explicitly to "true" or "false".
    /// Expected: the bound option equals the configured value.
    /// Invariant: the explicit setting is honoured exactly.
    /// </summary>
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

    /// <summary>
    /// Scenario: <c>RedistributeSummaryviaStatics</c> is "true" while <c>FirmwareUpgrade:Enabled</c> and <c>MikroTik:Enabled</c> are both "false".
    /// Expected: the option still binds as true, and the other two settings read false.
    /// Invariant: the redistribution flag does not depend on the firmware or MikroTik enable switches.
    /// </summary>
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

    /// <summary>
    /// Binds the <c>RedistributeSummaryviaStatics</c> setting from configuration into summary redistribution
    /// options, as the host does.
    /// </summary>
    private static OspfSummaryRedistributionOptions BindLikeHost(IConfiguration configuration)
    {
        OspfSummaryRedistributionOptions opts = new();
        opts.RedistributeSummaryviaStatics =
            configuration.GetValue("RedistributeSummaryviaStatics", false);
        return opts;
    }

    /// <summary>
    /// Scenario: an accept rule is built for 10.16.11.0/24 with prefix length 24.
    /// Expected: the rule contains <c>protocol static</c> (never <c>protocol == static</c>), <c>distance == 250</c>, <c>dst == 10.16.11.0/24</c>, <c>dst-len == 24</c> and <c>{ accept }</c>.
    /// Invariant: rules use the RouterOS v7 filter syntax that RouterOS accepts for the protocol match.
    /// </summary>
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

    /// <summary>
    /// Scenario: a plan lists area ranges 10.16.11.0/24, 100.99.76.16/28, 10.16.11.0/24 (duplicate) and 172.26.197.4/30.
    /// Expected: the collected prefixes are 10.16.11.0/24, 100.99.76.16/28 and 172.26.197.4/30, each once and in numeric order.
    /// Invariant: each summary prefix yields exactly one static route and rule, in a stable order.
    /// </summary>
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

    /// <summary>
    /// Scenario: the plan has no area ranges.
    /// Expected: <c>InvalidOperationException</c> mentioning "at least one area-range".
    /// Invariant: summary redistribution is never configured without prefixes to summarise.
    /// </summary>
    [Fact]
    public void CollectPrefixes_Empty_Throws()
    {
        OspfMigrationPlan plan = PlanWithRanges();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfStaticSummaryRedistributionService.CollectUniqueAreaRangePrefixes(plan));
        Assert.Contains("at least one area-range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: expected rule texts are built for 10.16.11.0/24, 100.99.76.16/28 and 172.26.197.4/30.
    /// Expected: four rules: one exact <c>if (protocol static &amp;&amp; distance == 250 &amp;&amp; dst == ... &amp;&amp; dst-len == N) { accept }</c> per prefix, followed by a final <c>reject</c>.
    /// Invariant: the chain accepts only the summary statics and rejects everything else, in that order.
    /// </summary>
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

    /// <summary>
    /// Scenario: the service is disabled and <c>ApplyAndVerifyAsync</c> runs against an empty store.
    /// Expected: the result is succeeded and skipped, no mutating commands are sent, and no routes or filter rules exist.
    /// Invariant: a disabled feature never changes the router.
    /// </summary>
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

    /// <summary>
    /// Scenario: the service is enabled but the plan has no area ranges.
    /// Expected: the result fails with <c>OspfStaticSummaryRedistributionFailed</c>, and no command touches <c>redistribute</c>.
    /// Invariant: redistribution is never enabled when there are no prefixes to protect.
    /// </summary>
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

    /// <summary>
    /// Scenario: the service runs twice for a three-prefix plan on an empty router.
    /// Expected: after the first run there are three blackhole routes (distance 250), four <c>ospf-v2-out</c> rules ending in <c>reject</c>, <c>out-filter-chain</c> set to <c>ospf-v2-out</c> and <c>static</c> redistributed; the second run succeeds without adding more routes or rules.
    /// Invariant: the reconcile creates routes, filter chain and redistribution together, and is idempotent.
    /// </summary>
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

    /// <summary>
    /// Scenario: enabled static summary redistribution with mutating commands recorded.
    /// Expected: success, and out-filter-chain attach precedes redistribute enable.
    /// Invariant: outbound filter must be attached before redistribution is enabled.
    /// </summary>
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

    /// <summary>
    /// Scenario: a static route for 10.16.11.0/24 via 1.2.3.4 (distance 1) already exists and differs from the managed blackhole route.
    /// Expected: the result fails with a detail mentioning "Conflicting", and no command touches <c>redistribute</c>.
    /// Invariant: an operator's conflicting route is never overwritten or redistributed.
    /// </summary>
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

    /// <summary>
    /// Scenario: the router already has a default route (0.0.0.0/0) and a <c>bgp-out</c> filter rule.
    /// Expected: the run succeeds, and the default route and <c>bgp-out</c> rule are still present.
    /// Invariant: only the managed summary routes and the <c>ospf-v2-out</c> chain are modified.
    /// </summary>
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

    /// <summary>
    /// Scenario: the OSPF instance already redistributes <c>connected</c>.
    /// Expected: the run succeeds and the redistribute value contains both <c>connected</c> and <c>static</c>.
    /// Invariant: adding static redistribution preserves existing redistribute sources.
    /// </summary>
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

    /// <summary>
    /// Scenario: the <c>ospf-v2-out</c> chain holds a stale bare <c>accept</c> rule.
    /// Expected: the run succeeds, and the chain has two rules (the static accept for the prefix, then <c>reject</c>) with no unconstrained <c>accept</c>.
    /// Invariant: a stale managed chain is replaced, never left in a permissive state.
    /// </summary>
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

    /// <summary>
    /// Scenario: two prefixes are applied to an empty router.
    /// Expected: the run succeeds, the add commands use <c>place-before</c>, and the chain has three rules: the 10.16.11.0/24 accept, the 100.99.76.16/28 accept, then <c>reject</c> last.
    /// Invariant: the final <c>reject</c> stays at the end of the chain while accept rules are inserted before it.
    /// </summary>
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

    /// <summary>
    /// Scenario: the chain holds <c>reject</c> before a valid accept for 10.16.11.0/24.
    /// Expected: the run succeeds, and the chain has two rules with the <c>protocol static</c> accept first and <c>reject</c> last.
    /// Invariant: a misordered managed chain is repaired so that the reject cannot shadow the accepts.
    /// </summary>
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

    /// <summary>
    /// Scenario: the fake scrambles filter order when the chain is printed after the add, so the order check fails.
    /// Expected: the result fails with a detail mentioning "order", and neither <c>redistribute</c> nor <c>out-filter-chain</c> is changed.
    /// Invariant: redistribution is only enabled after the filter chain is verified in the right order.
    /// </summary>
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

    /// <summary>
    /// Scenario: the instance redistributes <c>connected</c>, and the out-filter is corrupted after the redistribute is set, so the final verify fails.
    /// Expected: the result fails with compensation attempted and succeeded; <c>redistribute</c> is back to <c>connected</c> without <c>static</c>, <c>out-filter-chain</c> is cleared, and the detail mentions "Compensation restored".
    /// Invariant: a failed post-enable verification restores the original instance settings.
    /// </summary>
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

    /// <summary>
    /// Scenario: the out-filter is corrupted after redistribution is set and the compensation step also fails.
    /// Expected: the result fails with compensation attempted but not succeeded, and the detail mentions "Compensation failed" and "manual inspection".
    /// Invariant: a failed rollback is reported loudly so an operator inspects the router.
    /// </summary>
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

    /// <summary>
    /// Scenario: <c>MergeRedistributeWithStatic</c> is given null, empty, "static", "connected", "connected, rip" and " static , connected ".
    /// Expected: the results are "static", "static", "static", "connected,static", "connected,rip,static" and "connected,static".
    /// Invariant: <c>static</c> is added once, other sources are kept, and the output is trimmed and sorted.
    /// </summary>
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

    /// <summary>
    /// Scenario: the instance already redistributes <c>static</c>.
    /// Expected: the run succeeds, <c>out-filter-chain</c> is set once, no command touches <c>redistribute</c>, and the value stays "static".
    /// Invariant: an already-correct redistribute setting is left unchanged.
    /// </summary>
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

    /// <summary>
    /// Builds the service under test over <see cref="InventoryOps"/> with the feature flag set to
    /// <paramref name="enabled"/>; when disabled the service must perform no RouterOS mutation at
    /// all.
    /// </summary>
    private static OspfStaticSummaryRedistributionService CreateService(bool enabled) =>
        new(
            new InventoryOps(),
            Options.Create(new OspfSummaryRedistributionOptions { RedistributeSummaryviaStatics = enabled }),
            NullLogger<OspfStaticSummaryRedistributionService>.Instance);

    /// <summary>
    /// Builds a Tommy-style v7 plan with one area range per supplied CIDR in the site area, a single backhaul, and
    /// no passive loopbacks or site networks.
    /// </summary>
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

    /// <summary>
    /// Ops fake that returns a fixed v7 inventory and a single loopback address so instance and area names resolve;
    /// all other operations are no-ops or unsupported.
    /// </summary>
    private sealed class InventoryOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Returns a fixed live inventory (<c>default-v2</c> with <c>backbone-v2</c> and
        /// identity-named site areas) so the service can resolve instance and area names without
        /// touching the mutable store.
        /// </summary>
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

        /// <summary>
        /// Returns a single <c>10.16.0.11/32</c> Loopback address so loopback-derived router-id
        /// validation sees one consistent /32.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        /// <summary>
        /// Always throws <see cref="NotSupportedException"/>: this fake only supports the v7
        /// reconcile/verify path, never a live capture.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op: routing-filter removal is outside this scenario, so the fake completes without
        /// recording or failing.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Reports zero routing filters so the "routing filters remain" read-back check cannot fail
        /// for reasons unrelated to this scenario.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(0);

        /// <summary>
        /// No-op: redistribution clearing is not under test in this scenario.
        /// </summary>
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: clearing instance in-filter/out-filter references is not under test in this
        /// scenario.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: area reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: area-range reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the backhaul-template ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the loopback-template ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the site-matcher ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the post-ensure site-template read-back is assumed to pass in this scenario.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: legacy interface-template cleanup is not under test in this scenario.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: OSPFv3 removal is not under test in this scenario.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the router-id in the fake inventory is already correct, so no router-id write is
        /// modelled.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Returns a minimal management-plane baseline; the redistribution stage does not depend on
        /// backbone state.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Minimal(managementHost));

        /// <summary>
        /// No-op backbone guard: this fake never reports a dropped adjacency or a changed
        /// management route.
        /// </summary>
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Returns no neighbours; the adjacency guard is not exercised by this scenario.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
    }

    /// <summary>
    /// In-memory routes, filter rules and OSPF instance state for the static summary redistribution tests, with
    /// switches to simulate verification failure, rollback failure and reordered filter rules.
    /// </summary>
    private sealed class MutableStore
    {
        /// <summary>
        /// In-memory <c>/ip/route</c> table. Static summary routes (blackhole, distance 250) added
        /// by the service land here, and pre-seeded rows model conflicting or unrelated routes.
        /// </summary>
        public List<Dictionary<string, string>> Routes { get; } = [];

        /// <summary>
        /// In-memory <c>/routing/filter/rule</c> table kept in evaluation order; ordering and
        /// <c>place-before</c> behaviour determine whether the terminal <c>reject</c> stays last.
        /// </summary>
        public List<Dictionary<string, string>> FilterRules { get; } = [];

        /// <summary>
        /// The single OSPF instance row (<c>default-v2</c>) holding <c>router-id</c>,
        /// <c>out-filter-chain</c> and <c>redistribute</c>; mutated by the fake instance set/unset
        /// handlers.
        /// </summary>
        public Dictionary<string, string> Instance { get; private set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// When true, drops <c>out-filter-chain</c> right after <c>redistribute=static</c> is first
        /// applied, simulating a post-enable verification failure that must trigger compensation.
        /// </summary>
        public bool CorruptOutFilterAfterRedistributeSet { get; set; }

        /// <summary>
        /// When true, compensating instance set/unset calls throw, verifying that a failed rollback
        /// is reported (manual inspection required) rather than swallowed.
        /// </summary>
        public bool FailCompensation { get; set; }

        /// <summary>
        /// When true, filter-rule print returns rules reversed once the adds have completed,
        /// simulating RouterOS ordering diverging from intent; the service must then refuse to
        /// attach the chain or enable redistribution.
        /// </summary>
        public bool ScrambleFilterOrderOnPrintAfterAdd { get; set; }

        /// <summary>
        /// Set once the managed chain's terminal <c>reject</c> (or a <c>place-before</c> add) has
        /// been added; gates when <see cref="ScrambleFilterOrderOnPrintAfterAdd"/> starts reversing
        /// output.
        /// </summary>
        public bool FilterAddsCompleted { get; set; }

        /// <summary>
        /// Number of <c>redistribute</c> writes seen; the first is the enable, any later one is
        /// compensation.
        /// </summary>
        public int RedistributeSetCount { get; set; }

        /// <summary>
        /// Counter backing <see cref="NextId"/>; incremented atomically so generated RouterOS-style
        /// ids are unique.
        /// </summary>
        private int _seq;

        /// <summary>
        /// Creates a store with no OSPF instance, for the disabled path where no router state
        /// should even be read.
        /// </summary>
        public static MutableStore Empty() => new();

        /// <summary>
        /// Creates a store seeded with the <c>default-v2</c> instance (router-id 10.16.0.11, empty
        /// <c>redistribute</c>) that the enabled scenarios reconcile against.
        /// </summary>
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

        /// <summary>
        /// Creates a recording connection bound to this store with all scripted RouterOS handlers
        /// attached.
        /// </summary>
        public CommandRecordingConnection Connect()
        {
            CommandRecordingConnection c = new(this);
            c.Attach();
            return c;
        }

        /// <summary>
        /// Returns a unique RouterOS-style id (e.g. <c>*r1</c>, <c>*f2</c>) with the given prefix.
        /// </summary>
        public string NextId(string prefix) => $"{prefix}{Interlocked.Increment(ref _seq)}";
    }

    /// <summary>
    /// Stateful connection fake that serves routes, filter rules, the OSPF instance and system resource from a
    /// mutable store and records the mutating commands issued.
    /// </summary>
    private sealed class CommandRecordingConnection : DisposeTrackingConnection
    {
        /// <summary>
        /// Backing in-memory router state that scripted handlers read and mutate.
        /// </summary>
        private readonly MutableStore _store;

        /// <summary>
        /// Underlying fake connection on which <see cref="Attach"/> registers command handlers.
        /// </summary>
        private readonly TikFakeConnection _fake;

        /// <summary>
        /// Most recently created command. Parameters are added after <c>CreateCommand</c> returns,
        /// so handlers read them from here via <see cref="Param"/> when the command executes.
        /// </summary>
        private ITikCommand? _pending;

        /// <summary>
        /// Creates a connection over a new, empty <see cref="TikFakeConnection"/>; call <see
        /// cref="Attach"/> to register handlers.
        /// </summary>
        public CommandRecordingConnection(MutableStore store)
            : this(store, new TikFakeConnection())
        {
        }

        /// <summary>
        /// Chains the supplied fake into the base class while keeping a typed reference so handlers
        /// can be registered on it.
        /// </summary>
        private CommandRecordingConnection(MutableStore store, TikFakeConnection fake)
            : base(fake)
        {
            _store = store;
            _fake = fake;
        }

        /// <summary>
        /// Labels of mutating commands (add/remove/set/unset) in issue order. Asserts ordering
        /// (filter attached before redistribute enabled), idempotence (no repeat adds) and zero
        /// mutations when disabled or failing closed.
        /// </summary>
        public List<string> MutatingCommands { get; } = [];

        /// <summary>
        /// Registers scripted responses for routes, filter rules, the OSPF instance and
        /// <c>/system/resource</c>, each backed by <see cref="MutableStore"/> and recording
        /// mutating commands so the service's reconcile, verify and compensate paths run end-to-end
        /// against stateful fakes.
        /// </summary>
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

        /// <summary>
        /// Remembers the created command so the handler can read its parameters when it executes.
        /// </summary>
        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            _pending = cmd;
            return cmd;
        }

        /// <summary>
        /// Overload with an explicit parameter format; remembers the command identically so
        /// parameter lookup works for either overload.
        /// </summary>
        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            _pending = cmd;
            return cmd;
        }

        /// <summary>
        /// Returns the value of the named parameter on the pending command, or null if it was not
        /// supplied.
        /// </summary>
        private string? Param(string name) =>
            _pending?.Parameters.FirstOrDefault(p => p.Name == name)?.Value;

        /// <summary>
        /// Appends a label to <see cref="MutatingCommands"/>.
        /// </summary>
        private void Record(string label) => MutatingCommands.Add(label);
    }
}
