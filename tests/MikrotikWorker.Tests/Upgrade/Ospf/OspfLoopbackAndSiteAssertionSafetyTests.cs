// -----------------------------------------------------------------------------
// File: OspfLoopbackAndSiteAssertionSafetyTests.cs
//
// Description:
// Loopback remove/recreate staging, site-template readback assertion, and
// cleanup gating.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Loopback remove/recreate staging, site-template readback assertion, and cleanup gating.
/// </summary>
public sealed class OspfLoopbackAndSiteAssertionSafetyTests
{
    /// <summary>
    /// Scenario: source-text guard over <c>OspfInterfaceTemplateMatchingSemanticsTests.cs</c> and <c>OspfMikrotikOperations.cs</c>; the semantics test documents that interface and network matchers are alternatives (OR), so temporary duplicate Loopback templates are not proven safe.
    /// Expected: the source states "Make-before-break is NOT used" and carries the stages <c>remove-begin</c>, <c>add-failed after-remove</c> and <c>verify-failed after-remove-and-add</c>.
    /// Invariant: loopback templates are recreated remove-then-add with explicit failure stages, never with overlapping duplicates.
    /// </summary>
    [Fact]
    public void MakeBeforeBreak_IsRejected_BecauseOverlappingTemplatesAreUnsafe()
    {
        // Repo evidence: OspfInterfaceTemplateMatchingSemanticsTests documents interfaces=/networks=
        // as alternative matchers without assuming safe AND coexistence. AssertLoopback requires
        // exactly one match. Temporary duplicate Loopback templates are therefore not established safe.
        string semantics = File.ReadAllText(
            Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")),
                "Upgrade",
                "Ospf",
                "OspfInterfaceTemplateMatchingSemanticsTests.cs"));
        Assert.Contains("either by specifying network or interface", semantics, StringComparison.Ordinal);
        Assert.Contains("OR-safe isolation", semantics, StringComparison.Ordinal);

        string source = File.ReadAllText(
            Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
                "src",
                "MikrotikWorker",
                "Upgrade",
                "Ospf",
                "OspfMikrotikOperations.cs"));
        Assert.Contains("Make-before-break is NOT used", source, StringComparison.Ordinal);
        Assert.Contains("stage=remove-begin", source, StringComparison.Ordinal);
        Assert.Contains("stage=add-failed after-remove", source, StringComparison.Ordinal);
        Assert.Contains("stage=verify-failed after-remove-and-add", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: the live Loopback template <c>*2</c> is a ptp template, and the first print is followed by a correct <c>*9</c> on the read-back print.
    /// Expected: <c>*2</c> is removed, exactly one <c>add</c> is issued with no <c>type</c> parameter, and the template list is printed at least twice.
    /// Invariant: a ptp loopback is recreated by remove then add, and the result is verified by an independent re-read.
    /// </summary>
    [Fact]
    public async Task EnsureLoopback_PtpRecreate_RemoveThenAdd_ThenIndependentAssert()
    {
        int prints = 0;
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        prints++;
                        if (prints == 1)
                        {
                            return
                            [
                                new TikFakeReSentence(PtpLoopbackRow("*2")),
                                new TikFakeDoneSentence(),
                            ];
                        }

                        return
                        [
                            new TikFakeReSentence(CorrectLoopbackRow("*9")),
                            new TikFakeDoneSentence(),
                        ];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/remove",
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/add",
                    static _ => [new TikFakeDoneSentence()]));

        await CreateOps().EnsureLoopbackTemplateAsync(
            capturing,
            LoopbackPlan(),
            [BackhaulPlan()],
            CancellationToken.None);

        Assert.Contains(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*2"));
        IReadOnlyList<(string Name, string? Value)> add = Assert.Single(capturing.Adds);
        Assert.DoesNotContain(add, p => p.Name.Equals("type", StringComparison.OrdinalIgnoreCase));
        Assert.True(prints >= 2, "Assert must independently print after recreate.");
    }

    /// <summary>
    /// Scenario: the ptp loopback <c>*2</c> is removed and re-added, but the read-back print finds no loopback template.
    /// Expected: <c>OspfBackboneMutationGuardException</c> mentioning <c>stage=verify-failed</c> and <c>prior.id=*2</c>; <c>*2</c> was removed and one add was attempted.
    /// Invariant: a recreate that cannot be verified fails and reports its stage instead of succeeding.
    /// </summary>
    [Fact]
    public async Task EnsureLoopback_PtpRecreate_VerifyFails_ReportsStageAndDoesNotSucceed()
    {
        int prints = 0;
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        prints++;
                        if (prints == 1)
                        {
                            return [new TikFakeReSentence(PtpLoopbackRow("*2")), new TikFakeDoneSentence()];
                        }

                        // Missing after recreate — verify stage fails.
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/remove",
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/add",
                    static _ => [new TikFakeDoneSentence()]));

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().EnsureLoopbackTemplateAsync(
                capturing,
                LoopbackPlan(),
                [BackhaulPlan()],
                CancellationToken.None));

        Assert.Contains("stage=verify-failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("prior.id=*2", ex.Message, StringComparison.Ordinal);
        Assert.Contains(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*2"));
        Assert.Single(capturing.Adds);
    }

    /// <summary>
    /// Scenario: the live ptp loopback <c>*2</c> exists but RouterOS traps the remove with "failure: cannot remove".
    /// Expected: <c>OspfBackboneMutationGuardException</c> mentioning <c>stage=remove-failed</c> and <c>.id=*2</c>; no add is issued.
    /// Invariant: when removal fails nothing is added, so no duplicate loopback template is created.
    /// </summary>
    [Fact]
    public async Task EnsureLoopback_PtpRecreate_RemoveFails_ReportsStage()
    {
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(PtpLoopbackRow("*2")), new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/remove",
                    static _ =>
                    [
                        new TikFakeTrapSentence("failure: cannot remove"),
                        new TikFakeDoneSentence(),
                    ]));

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().EnsureLoopbackTemplateAsync(
                capturing,
                LoopbackPlan(),
                [BackhaulPlan()],
                CancellationToken.None));

        Assert.Contains("stage=remove-failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".id=*2", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.Adds);
    }

    /// <summary>
    /// Scenario: the live ptp loopback <c>*2</c> is removed successfully, but RouterOS traps the add with "failure: cannot add".
    /// Expected: <c>OspfBackboneMutationGuardException</c> mentioning <c>stage=add-failed after-remove</c> and the stale removed <c>.id</c>; <c>*2</c> was removed and one add was attempted.
    /// Invariant: the failure report states that the old template is already gone so an operator can recover.
    /// </summary>
    [Fact]
    public async Task EnsureLoopback_PtpRecreate_AddFails_ReportsStageAfterRemove()
    {
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(PtpLoopbackRow("*2")), new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/remove",
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/add",
                    static _ =>
                    [
                        new TikFakeTrapSentence("failure: cannot add"),
                        new TikFakeDoneSentence(),
                    ]));

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().EnsureLoopbackTemplateAsync(
                capturing,
                LoopbackPlan(),
                [BackhaulPlan()],
                CancellationToken.None));

        Assert.Contains("stage=add-failed after-remove", ex.Message, StringComparison.Ordinal);
        Assert.Contains("stale removed .id", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*2"));
        Assert.Single(capturing.Adds);
    }

    /// <summary>
    /// Scenario: no loopback template exists on the first print, one is added, and the read-back shows two matching templates (<c>*8</c> and <c>*9</c>).
    /// Expected: <c>OspfBackboneMutationGuardException</c> mentioning "2 templates match".
    /// Invariant: exactly one loopback template may match after ensure; duplicates are an error.
    /// </summary>
    [Fact]
    public async Task AssertLoopback_RejectsDuplicateMatches()
    {
        int prints = 0;
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        prints++;
                        if (prints == 1)
                        {
                            return [new TikFakeDoneSentence()];
                        }

                        return
                        [
                            new TikFakeReSentence(CorrectLoopbackRow("*8")),
                            new TikFakeReSentence(CorrectLoopbackRow("*9")),
                            new TikFakeDoneSentence(),
                        ];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/add",
                    static _ => [new TikFakeDoneSentence()]));

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().EnsureLoopbackTemplateAsync(
                capturing,
                LoopbackPlan(),
                [BackhaulPlan()],
                CancellationToken.None));

        Assert.Contains("2 templates match", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: EnsureLoopbackTemplateAsync starts with no loopback template, adds one, and the post-add
    /// verification print then returns a wrong row. The cases cover a wrong area, the wrong interface (lo instead of
    /// Loopback), a wrong network (10.16.0.99/32), a missing passive flag, a template still typed ptp, and a disabled
    /// template.
    /// Expected: An OspfBackboneMutationGuardException is thrown.
    /// Invariant: A loopback template that is not exactly the planned passive replacement is rejected rather than
    /// accepted.
    /// </summary>
    [Theory]
    [InlineData("wrong-area", "other-area", "Loopback", "10.16.0.11/32", "", "false", null)]
    [InlineData("wrong-iface", "backbone-v2", "lo", "10.16.0.11/32", "", "false", null)]
    [InlineData("wrong-net", "backbone-v2", "Loopback", "10.16.0.99/32", "", "false", null)]
    [InlineData("not-passive", "backbone-v2", "Loopback", "10.16.0.11/32", null, "false", null)]
    [InlineData("still-ptp", "backbone-v2", "Loopback", "10.16.0.11/32", "", "false", "ptp")]
    [InlineData("disabled", "backbone-v2", "Loopback", "10.16.0.11/32", "", "true", null)]
    public async Task AssertLoopback_RejectsIncorrectReplacement(
        string _,
        string area,
        string iface,
        string networks,
        string? passive,
        string disabled,
        string? type)
    {
        Dictionary<string, string> row = new()
        {
            [".id"] = "*9",
            ["area"] = area,
            ["interfaces"] = iface,
            ["networks"] = networks,
            ["disabled"] = disabled,
        };
        if (passive is not null)
        {
            row["passive"] = passive;
        }

        if (type is not null)
        {
            row["type"] = type;
        }

        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(row), new TikFakeDoneSentence()]));

        // Invoke assert via ensure no-match path: empty first print then... use Ensure with already-correct
        // forced path skipped. Call through Ensure when match has forced ptp then bad verify already covered.
        // Directly exercise via Ensure when existing is correct except we only have Assert private —
        // use Ensure with missing template then bad add result: first print empty, add, second print bad.
        int prints = 0;
        capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        prints++;
                        if (prints == 1)
                        {
                            return [new TikFakeDoneSentence()];
                        }

                        return [new TikFakeReSentence(row), new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/add",
                    static _ => [new TikFakeDoneSentence()]));

        await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().EnsureLoopbackTemplateAsync(
                capturing,
                LoopbackPlan(),
                [BackhaulPlan()],
                CancellationToken.None));
    }

    /// <summary>
    /// Scenario: the live loopback template <c>*2</c> already matches the plan.
    /// Expected: no <c>remove</c> and no <c>add</c> are issued.
    /// Invariant: ensuring a correct loopback template is idempotent and never recreates it.
    /// </summary>
    [Fact]
    public async Task EnsureLoopback_AlreadyCorrect_IsIdempotentNoRecreate()
    {
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(CorrectLoopbackRow("*2")), new TikFakeDoneSentence()]));

        await CreateOps().EnsureLoopbackTemplateAsync(
            capturing,
            LoopbackPlan(),
            [BackhaulPlan()],
            CancellationToken.None);

        Assert.Empty(capturing.Removes);
        Assert.Empty(capturing.Adds);
    }

    /// <summary>
    /// Scenario: the reconciler runs with ops whose site-template assertion throws because the planned matcher is absent on read-back.
    /// Expected: <c>ApplyAsync</c> throws <c>OspfBackboneMutationGuardException</c>; <c>site-ensure</c> and <c>site-assert</c> ran, <c>cleanup</c> did not, and <c>CleanupInvoked</c> is false.
    /// Invariant: legacy cleanup never starts unless the consolidated site template is confirmed present.
    /// </summary>
    [Fact]
    public async Task AssertSite_Fails_WhenAbsent_AndCleanupNotStarted()
    {
        TrackingReconcilerOps ops = new()
        {
            SiteAssertException = new OspfBackboneMutationGuardException(
                "Site template assertion failed: planned matcher is absent on readback. Cleanup must not start."),
        };

        await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
                .ApplyAsync(
                    FakeMikrotikConnections.CreateFullyPopulated(version: "7.25.1"),
                    TommyPlan(),
                    managementHost: "198.18.240.11",
                    CancellationToken.None));

        Assert.Contains("site-ensure", ops.Events);
        Assert.Contains("site-assert", ops.Events);
        Assert.DoesNotContain(ops.Events, e => e == "cleanup");
        Assert.False(ops.CleanupInvoked);
    }

    /// <summary>
    /// Scenario: a single live site template deviates from the plan in one respect: partial networks, wrong interfaces, ptp type, not passive, disabled, or wrong area.
    /// Expected: <c>AssertSiteNetworkTemplateStateAsync</c> throws <c>OspfBackboneMutationGuardException</c> mentioning "Site template assertion failed" and "Cleanup must not start".
    /// Invariant: only a template matching the planned consolidated matcher exactly counts as present.
    /// </summary>
    [Theory]
    [InlineData("partial-networks", "10.16.11.0/24", "all", "", "false", null)]
    [InlineData("wrong-interfaces", "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12", "Bridge-VLAN-99_MGMT", "", "false", null)]
    [InlineData("ptp-type", "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12", "all", "", "false", "ptp")]
    [InlineData("not-passive", "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12", "all", null, "false", null)]
    [InlineData("disabled", "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12", "all", "", "true", null)]
    [InlineData("wrong-area", "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12", "all", "", "false", null)]
    public async Task AssertSite_RejectsIncorrectLiveTemplate(
        string scenario,
        string networks,
        string interfaces,
        string? passive,
        string disabled,
        string? type)
    {
        Dictionary<string, string> row = new()
        {
            [".id"] = "*A",
            ["area"] = scenario == "wrong-area" ? "other-site-v2" : "BR01.POP011.016.Tommy-v2",
            ["interfaces"] = interfaces,
            ["networks"] = networks,
            ["disabled"] = disabled,
        };
        if (passive is not null)
        {
            row["passive"] = passive;
        }

        if (type is not null)
        {
            row["type"] = type;
        }

        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(row), new TikFakeDoneSentence()]));

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertSiteNetworkTemplateStateAsync(
                capturing,
                SitePlan(),
                CancellationToken.None));

        Assert.Contains("Site template assertion failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Cleanup must not start", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: two live rows (<c>*A</c> and <c>*B</c>) both match the planned site matcher.
    /// Expected: <c>OspfBackboneMutationGuardException</c> mentioning "matches 2 live rows" and "Cleanup must not start".
    /// Invariant: the planned site template must match exactly one live row.
    /// </summary>
    [Fact]
    public async Task AssertSite_RejectsDuplicatePlannedTemplates()
    {
        Dictionary<string, string> row = new()
        {
            [".id"] = "*A",
            ["area"] = "BR01.POP011.016.Tommy-v2",
            ["interfaces"] = "all",
            ["networks"] = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
            ["passive"] = "",
            ["disabled"] = "false",
        };
        Dictionary<string, string> dup = new(row) { [".id"] = "*B" };

        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    [
                        new TikFakeReSentence(row),
                        new TikFakeReSentence(dup),
                        new TikFakeDoneSentence(),
                    ]));

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertSiteNetworkTemplateStateAsync(capturing, SitePlan(), CancellationToken.None));

        Assert.Contains("matches 2 live rows", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Cleanup must not start", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: one live row has the planned area, <c>interfaces=all</c>, the three consolidated networks, passive set, enabled, and type broadcast.
    /// Expected: <c>AssertSiteNetworkTemplateStateAsync</c> completes without throwing.
    /// Invariant: an exact consolidated site template passes the assertion.
    /// </summary>
    [Fact]
    public async Task AssertSite_AcceptsExactConsolidatedMatcher()
    {
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*A",
                            ["area"] = "BR01.POP011.016.Tommy-v2",
                            ["interfaces"] = "all",
                            ["networks"] = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
                            ["passive"] = "",
                            ["disabled"] = "false",
                            ["type"] = "broadcast",
                        }),
                        new TikFakeDoneSentence(),
                    ]));

        await CreateOps().AssertSiteNetworkTemplateStateAsync(capturing, SitePlan(), CancellationToken.None);
    }

    /// <summary>
    /// Scenario: a full reconciler run against a tracking ops fake that records the order of stages.
    /// Expected: <c>site-assert</c> occurs before <c>cleanup</c> and again after it, and cleanup was invoked.
    /// Invariant: the site template is verified both before and after legacy cleanup.
    /// </summary>
    [Fact]
    public async Task Reconciler_AssertsSite_BeforeAndAfterCleanup()
    {
        TrackingReconcilerOps ops = new();
        await new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
            .ApplyAsync(
                FakeMikrotikConnections.CreateFullyPopulated(version: "7.25.1"),
                TommyPlan(),
                managementHost: "198.18.240.11",
                CancellationToken.None);

        int firstAssert = ops.Events.IndexOf("site-assert");
        int cleanup = ops.Events.IndexOf("cleanup");
        int lastAssert = ops.Events.LastIndexOf("site-assert");
        Assert.True(firstAssert >= 0);
        Assert.True(cleanup > firstAssert);
        Assert.True(lastAssert > cleanup);
        Assert.True(ops.CleanupInvoked);
    }

    /// <summary>
    /// Scenario: the Tommy plan is classified against live rows with the backhaul, loopback, consolidated site template and five legacy management subdivisions; then again with the site template omitted.
    /// Expected: with the site template, all five legacy rows (10.16.11.0/27, .32/27, .64/26, .128/26, .192/26) are known legacy and nothing is unknown; without it, classification throws <c>InvalidOperationException</c> mentioning "No deletion was attempted".
    /// Invariant: subdivisions are only treated as removable while the consolidated site template that replaces them exists.
    /// </summary>
    [Fact]
    public void TommyFiveLegacyCandidates_RemainKnownLegacy_OnlyAfterSitePresent()
    {
        OspfMigrationPlan plan = TommyPlan();
        List<OspfTemplateInventoryRow> withSite =
        [
            InventoryBackhaul(plan),
            InventoryLoopback(plan),
            InventorySite(plan),
            ..TommyLegacyRows(),
        ];

        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, withSite);
        Assert.Equal(5, report.KnownLegacy.Count);
        Assert.Empty(report.Unknown);
        Assert.All(
            report.KnownLegacy,
            l => Assert.Contains(
                new[]
                {
                    "10.16.11.0/27",
                    "10.16.11.32/27",
                    "10.16.11.64/26",
                    "10.16.11.128/26",
                    "10.16.11.192/26",
                },
                n => string.Equals(Normalize(l.Row.Networks), Normalize(n), StringComparison.Ordinal)));

        InvalidOperationException missingSite = Assert.Throws<InvalidOperationException>(() =>
            OspfTemplateCleanupClassifier.ClassifyOrThrow(
                plan,
                [
                    InventoryBackhaul(plan),
                    InventoryLoopback(plan),
                    ..TommyLegacyRows(),
                ]));
        Assert.Contains("No deletion was attempted", missingSite.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates the real <c>OspfMikrotikOperations</c> with a null logger.
    /// </summary>
    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    /// <summary>
    /// Planned passive loopback template in <c>backbone-v2</c> for <c>10.16.0.11/32</c>.
    /// </summary>
    private static OspfPlanLoopbackTemplate LoopbackPlan() => new()
    {
        AreaName = "backbone-v2",
        Interface = "Loopback",
        NetworkCidr = "10.16.0.11/32",
        Passive = true,
    };

    /// <summary>
    /// Protected active backhaul (ptp cost 10, /30) that loopback and site operations must never
    /// remove, retarget or passivate.
    /// </summary>
    private static OspfPlanBackhaulTemplate BackhaulPlan() => new()
    {
        AreaName = "backbone-v2",
        Interface = "eth-01_BH01.Tommy-Shylock",
        NetworkCidr = "10.16.248.76/30",
        Cost = 10,
        Type = "ptp",
        Passive = false,
    };

    /// <summary>
    /// Planned consolidated site matcher (<c>interfaces=all</c>, three networks, passive) whose
    /// exact read-back gates cleanup.
    /// </summary>
    private static OspfPlanSiteNetworkTemplate SitePlan() => new()
    {
        AreaName = "BR01.POP011.016.Tommy-v2",
        Interfaces = "all",
        NetworkCidr = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
        Passive = true,
        Comment = "ACS-Provisioning Subnet",
    };

    /// <summary>
    /// Full Tommy plan: backbone and site areas, three area ranges, the protected backhaul,
    /// loopback and site matcher, and the five legacy site templates as superseded provenance.
    /// </summary>
    private static OspfMigrationPlan TommyPlan() =>
        new()
        {
            ImportId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            JobId = "prep",
            RouterIp = "198.18.240.11",
            Identity = "BR01.POP011.016.Tommy",
            SourceRouterOsVersion = "7.25.1",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            InstanceName = "default-v2",
            RouterId = "10.16.0.11",
            TargetInstanceName = "default-v2",
            TargetBackboneAreaName = "backbone-v2",
            TargetSiteAreaName = "BR01.POP011.016.Tommy-v2",
            LoopbackInterfaceName = "Loopback",
            LoopbackCidr = "10.16.0.11/32",
            DerivedManagementCidr = "10.16.11.0/24",
            Areas =
            [
                new OspfPlanArea
                {
                    Name = "backbone-v2",
                    AreaId = "0.0.0.0",
                    IsBackbone = true,
                    TargetType = "default",
                },
                new OspfPlanArea
                {
                    Name = "BR01.POP011.016.Tommy-v2",
                    AreaId = "10.16.0.11",
                    IsBackbone = false,
                    TargetType = "stub",
                    DefaultCost = "1",
                },
            ],
            AreaRanges =
            [
                new OspfPlanAreaRange
                {
                    AreaName = "BR01.POP011.016.Tommy-v2",
                    RangeCidr = "10.16.11.0/24",
                    Comment = "MGMT",
                },
                new OspfPlanAreaRange
                {
                    AreaName = "BR01.POP011.016.Tommy-v2",
                    RangeCidr = "100.99.76.16/28",
                    Comment = "HEROTEL_ACTIVE_POOL",
                },
                new OspfPlanAreaRange
                {
                    AreaName = "BR01.POP011.016.Tommy-v2",
                    RangeCidr = "172.26.197.4/30",
                    Comment = "HEROTEL_INACTIVE_POOL",
                },
            ],
            ActiveBackhauls = [BackhaulPlan()],
            PassiveLoopbacks = [LoopbackPlan()],
            PassiveSiteNetworks = [SitePlan()],
            SupersededTemplates = TommyLegacyRows()
                .Select(static r => OspfTemplateSemanticIdentity.FromInventoryRow(r))
                .ToList(),
        };

    /// <summary>
    /// Builds the legacy per-VLAN management templates for the Tommy site that consolidation supersedes.
    /// </summary>
    private static List<OspfTemplateInventoryRow> TommyLegacyRows() =>
    [
        Inv("*3", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"),
        Inv("*4", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-197_MGMT", "10.16.11.32/27"),
        Inv("*5", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-99_MGMT", "10.16.11.64/26"),
        Inv("*6", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-99_MGMT", "10.16.11.128/26"),
        Inv("*7", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-99_MGMT", "10.16.11.192/26"),
    ];

    /// <summary>
    /// Builds an inventory row matching the plan's first active backhaul template.
    /// </summary>
    private static OspfTemplateInventoryRow InventoryBackhaul(OspfMigrationPlan plan) =>
        Inv("*1", plan.ActiveBackhauls[0].AreaName, plan.ActiveBackhauls[0].Interface, plan.ActiveBackhauls[0].NetworkCidr, passive: false, type: "ptp", cost: "10");

    /// <summary>
    /// Inventory row for the planned loopback (<c>*2</c>), a protected role in classification.
    /// </summary>
    private static OspfTemplateInventoryRow InventoryLoopback(OspfMigrationPlan plan) =>
        Inv("*2", plan.PassiveLoopbacks[0].AreaName, plan.PassiveLoopbacks[0].Interface!, plan.PassiveLoopbacks[0].NetworkCidr);

    /// <summary>
    /// Builds an inventory row matching the plan's first passive site network template.
    /// </summary>
    private static OspfTemplateInventoryRow InventorySite(OspfMigrationPlan plan) =>
        Inv("*A", plan.PassiveSiteNetworks[0].AreaName, "all", plan.PassiveSiteNetworks[0].NetworkCidr);

    /// <summary>
    /// Builds an interface-template inventory row with the given id, area, interfaces, networks, passive flag, type
    /// and cost, plus fixed auth, priority and BFD defaults.
    /// </summary>
    private static OspfTemplateInventoryRow Inv(
        string id,
        string area,
        string iface,
        string networks,
        bool passive = true,
        string? type = null,
        string cost = "1") =>
        new()
        {
            Id = id,
            Area = area,
            Interfaces = iface,
            Networks = networks,
            Passive = passive,
            Disabled = false,
            Type = type,
            Cost = cost,
            AuthId = "1",
            Priority = "1",
            UseBfd = "false",
            AreaInstanceName = "default-v2",
        };

    /// <summary>
    /// Normalises a CIDR through the production prefix parser (falling back to the trimmed input)
    /// so network strings are compared in canonical form.
    /// </summary>
    private static string Normalize(string? cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p) ? p.ToCidrString() : cidr?.Trim() ?? string.Empty;

    /// <summary>
    /// Builds a raw template row for the Loopback /32 in the backbone area with <c>type=ptp</c>, as left by an
    /// earlier migration.
    /// </summary>
    private static Dictionary<string, string> PtpLoopbackRow(string id) => new()
    {
        [".id"] = id,
        ["area"] = "backbone-v2",
        ["interfaces"] = "Loopback",
        ["networks"] = "10.16.0.11/32",
        ["type"] = "ptp",
        ["passive"] = "",
        ["disabled"] = "false",
    };

    /// <summary>
    /// Live row for a correct loopback replacement: <c>Loopback</c>, <c>10.16.0.11/32</c>,
    /// <c>type=broadcast</c>, passive, enabled.
    /// </summary>
    private static Dictionary<string, string> CorrectLoopbackRow(string id) => new()
    {
        [".id"] = id,
        ["area"] = "backbone-v2",
        ["interfaces"] = "Loopback",
        ["networks"] = "10.16.0.11/32",
        ["type"] = "broadcast",
        ["passive"] = "",
        ["disabled"] = "false",
    };

    /// <summary>
    /// Ops fake that logs site ensure, site assert and cleanup events and can fail the first site assertion,
    /// proving cleanup only runs after a passing site read-back.
    /// </summary>
    private sealed class TrackingReconcilerOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Ordered stage log (<c>site-ensure</c>, <c>site-assert</c>, <c>cleanup</c>) used to prove
        /// the site template is asserted before and after legacy cleanup.
        /// </summary>
        public List<string> Events { get; } = [];

        /// <summary>
        /// True once obsolete-template cleanup ran; must remain false if the site read-back fails.
        /// </summary>
        public bool CleanupInvoked { get; private set; }

        /// <summary>
        /// Exception thrown by the first site assertion, modelling a planned site matcher missing
        /// on read-back.
        /// </summary>
        public Exception? SiteAssertException { get; init; }

        /// <summary>
        /// Number of site assertions so far; the injected failure only applies to the first one.
        /// </summary>
        private int _siteAssertCount;

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
        /// Records a <c>site-ensure</c> event.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Events.Add("site-ensure");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>site-assert</c> and throws <see cref="SiteAssertException"/> on the first
        /// call when configured, modelling an absent matcher that must stop cleanup.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default)
        {
            Events.Add("site-assert");
            _siteAssertCount++;
            if (SiteAssertException is not null && _siteAssertCount == 1)
            {
                throw SiteAssertException;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>cleanup</c> and sets <see cref="CleanupInvoked"/> so tests can prove cleanup
        /// is gated on a passing site assertion.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default)
        {
            CleanupInvoked = true;
            Events.Add("cleanup");
            return Task.CompletedTask;
        }

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
        /// Returns an inventory matching <c>TommyPlan()</c> so identity resolution succeeds.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.InventoryMatchingPlan(TommyPlan()));

        /// <summary>
        /// Returns a single <c>10.16.0.11/32</c> Loopback address so loopback-derived router-id
        /// validation sees one consistent /32.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        /// <summary>
        /// Returns the Tommy lab baseline so backbone guards run against realistic state.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

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
    /// Connection wrapper that captures interface-template add and remove commands so their parameters can be
    /// inspected after the production code has added them.
    /// </summary>
    private sealed class ParameterCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        /// <summary>
        /// Interface-template <c>add</c> commands captured at creation; parameters are read lazily.
        /// </summary>
        private readonly List<ITikCommand> _adds = [];

        /// <summary>
        /// Interface-template <c>remove</c> commands captured at creation; parameters are read
        /// lazily.
        /// </summary>
        private readonly List<ITikCommand> _removes = [];

        /// <summary>
        /// Parameters of every captured template <c>add</c>; the loopback recreate must not carry
        /// <c>type</c>.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Adds => Snapshot(_adds);

        /// <summary>
        /// Parameters of every captured template <c>remove</c>; used to assert the exact prior
        /// <c>.id</c> was removed.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Removes => Snapshot(_removes);

        /// <summary>
        /// Tracks template add/remove commands, then returns the inner command.
        /// </summary>
        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        /// <summary>
        /// Overload with an explicit parameter format; tracks identically so a mutation cannot
        /// bypass capture.
        /// </summary>
        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        /// <summary>
        /// Routes a template command into the add or remove list based on its command text.
        /// </summary>
        private void Track(string commandText, ITikCommand cmd)
        {
            if (commandText.Equals("/routing/ospf/interface-template/add", StringComparison.Ordinal))
            {
                _adds.Add(cmd);
            }
            else if (commandText.Equals("/routing/ospf/interface-template/remove", StringComparison.Ordinal))
            {
                _removes.Add(cmd);
            }
        }

        /// <summary>
        /// Copies <c>(name, value)</c> parameter pairs at read time, after production code finished
        /// calling <c>AddParameter</c>.
        /// </summary>
        private static IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Snapshot(List<ITikCommand> commands) =>
            commands
                .Select(static c => (IReadOnlyList<(string Name, string? Value)>)c.Parameters
                    .Select(static p => (p.Name, p.Value))
                    .ToList())
                .ToList();
    }
}
