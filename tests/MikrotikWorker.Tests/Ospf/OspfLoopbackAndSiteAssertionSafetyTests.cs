using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Loopback remove/recreate staging, site-template readback assertion, and cleanup gating.
/// </summary>
public sealed class OspfLoopbackAndSiteAssertionSafetyTests
{
    [Fact]
    public void MakeBeforeBreak_IsRejected_BecauseOverlappingTemplatesAreUnsafe()
    {
        // Repo evidence: OspfInterfaceTemplateMatchingSemanticsTests documents interfaces=/networks=
        // as alternative matchers without assuming safe AND coexistence. AssertLoopback requires
        // exactly one match. Temporary duplicate Loopback templates are therefore not established safe.
        string semantics = File.ReadAllText(
            Path.Combine(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")),
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

    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    private static OspfPlanLoopbackTemplate LoopbackPlan() => new()
    {
        AreaName = "backbone-v2",
        Interface = "Loopback",
        NetworkCidr = "10.16.0.11/32",
        Passive = true,
    };

    private static OspfPlanBackhaulTemplate BackhaulPlan() => new()
    {
        AreaName = "backbone-v2",
        Interface = "eth-01_BH01.Tommy-Shylock",
        NetworkCidr = "10.16.248.76/30",
        Cost = 10,
        Type = "ptp",
        Passive = false,
    };

    private static OspfPlanSiteNetworkTemplate SitePlan() => new()
    {
        AreaName = "BR01.POP011.016.Tommy-v2",
        Interfaces = "all",
        NetworkCidr = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
        Passive = true,
        Comment = "ACS-Provisioning Subnet",
    };

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

    private static List<OspfTemplateInventoryRow> TommyLegacyRows() =>
    [
        Inv("*3", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-99_MGMT", "10.16.11.0/27"),
        Inv("*4", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-197_MGMT", "10.16.11.32/27"),
        Inv("*5", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-99_MGMT", "10.16.11.64/26"),
        Inv("*6", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-99_MGMT", "10.16.11.128/26"),
        Inv("*7", "BR01.POP011.016.Tommy-v2", "Bridge-VLAN-99_MGMT", "10.16.11.192/26"),
    ];

    private static OspfTemplateInventoryRow InventoryBackhaul(OspfMigrationPlan plan) =>
        Inv("*1", plan.ActiveBackhauls[0].AreaName, plan.ActiveBackhauls[0].Interface, plan.ActiveBackhauls[0].NetworkCidr, passive: false, type: "ptp", cost: "10");

    private static OspfTemplateInventoryRow InventoryLoopback(OspfMigrationPlan plan) =>
        Inv("*2", plan.PassiveLoopbacks[0].AreaName, plan.PassiveLoopbacks[0].Interface!, plan.PassiveLoopbacks[0].NetworkCidr);

    private static OspfTemplateInventoryRow InventorySite(OspfMigrationPlan plan) =>
        Inv("*A", plan.PassiveSiteNetworks[0].AreaName, "all", plan.PassiveSiteNetworks[0].NetworkCidr);

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

    private static string Normalize(string? cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p) ? p.ToCidrString() : cidr?.Trim() ?? string.Empty;

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

    private sealed class TrackingReconcilerOps : IOspfMikrotikOperations
    {
        public List<string> Events { get; } = [];
        public bool CleanupInvoked { get; private set; }
        public Exception? SiteAssertException { get; init; }
        private int _siteAssertCount;

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
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Events.Add("site-ensure");
            return Task.CompletedTask;
        }

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

        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default)
        {
            CleanupInvoked = true;
            Events.Add("cleanup");
            return Task.CompletedTask;
        }

        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.InventoryMatchingPlan(TommyPlan()));
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
    }

    private sealed class ParameterCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        private readonly List<ITikCommand> _adds = [];
        private readonly List<ITikCommand> _removes = [];

        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Adds => Snapshot(_adds);
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Removes => Snapshot(_removes);

        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            Track(commandText, cmd);
            return cmd;
        }

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

        private static IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Snapshot(List<ITikCommand> commands) =>
            commands
                .Select(static c => (IReadOnlyList<(string Name, string? Value)>)c.Parameters
                    .Select(static p => (p.Name, p.Value))
                    .ToList())
                .ToList();
    }
}
