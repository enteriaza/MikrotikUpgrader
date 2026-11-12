// -----------------------------------------------------------------------------
// File: OspfBackbonePassiveRegressionTests.cs
//
// Description:
// Critical regression: EnsureBackhaul must never emit a RouterOS flag enable
// for passive (the lab failure was set *1 … passive which destroyed the
// backbone adjacency).
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
    /// <summary>
    /// Scenario: reconciler apply where the fake ops report a backbone neighbour dropping from Full mid-mutation.
    /// Expected: <c>OspfBackboneMutationGuardException</c> mentioning the neighbour drop; backhaul ensured, later stages not.
    /// Invariant: backbone neighbour loss aborts further OSPF mutations fail-closed.
    /// </summary>
public sealed class OspfBackbonePassiveRegressionTests
{
    /// <summary>
    /// Scenario: the live backhaul template (<c>*1</c>) already matches the plan and carries no <c>passive</c> flag.
    /// Expected: <c>EnsureBackhaulTemplateAsync</c> issues no <c>set</c>, <c>unset</c> or <c>add</c> commands at all.
    /// Invariant: ensuring an already-correct backbone template is idempotent and never touches <c>passive</c>.
    /// </summary>
    [Fact]
    public async Task EnsureBackhaul_ExistingActive_DoesNotSendPassiveParameter()
    {
        ParameterCapturingConnection capturing = CreateCapturing(
            printRows: [BackhaulRow("*1", passive: null)]);

        await CreateOps().EnsureBackhaulTemplateAsync(capturing, TommyBackhaul(), CancellationToken.None);

        // Already-correct backhaul must not be rewritten (idempotent ensure).
        Assert.Empty(capturing.Sets);
        Assert.Empty(capturing.Unsets);
        Assert.Empty(capturing.Adds);
    }

    /// <summary>
    /// Scenario: the live backhaul template has cost 20 while the plan wants cost 10 (first print), then reads back correct.
    /// Expected: a <c>set</c> is sent for <c>*1</c> carrying cost=10 and type=ptp, and no captured <c>set</c> contains a <c>passive</c> parameter.
    /// Invariant: correcting a backhaul drift never writes <c>passive</c> (the lab outage came from <c>set *1 passive</c>).
    /// </summary>
    [Fact]
    public async Task EnsureBackhaul_IncorrectCost_SetsWithoutPassive()
    {
        int printCount = 0;
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        printCount++;
                        Dictionary<string, string> row = BackhaulRow("*1", passive: null);
                        if (printCount == 1)
                        {
                            row["cost"] = "20";
                        }

                        return [new TikFakeReSentence(row), new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/set",
                    static _ => [new TikFakeDoneSentence()]));

        await CreateOps().EnsureBackhaulTemplateAsync(capturing, TommyBackhaul(), CancellationToken.None);

        Assert.NotEmpty(capturing.Sets);
        Assert.All(capturing.Sets, set =>
        {
            Assert.DoesNotContain(set, p => p.Name.Equals("passive", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(set, p => p.Name == ".id" && p.Value == "*1");
            Assert.Contains(set, p => p.Name == "cost" && p.Value == "10");
            Assert.Contains(set, p => p.Name == "type" && p.Value == "ptp");
        });
    }

    /// <summary>
    /// Scenario: the live backhaul template is flagged passive on the first print and clean on the read-back.
    /// Expected: the flag is cleared with an <c>unset</c> whose value-name is <c>passive</c>; no <c>set</c> or <c>add</c> carries a <c>passive</c> parameter.
    /// Invariant: RouterOS boolean flags are cleared via <c>unset</c>, never via <c>passive=no</c>.
    /// </summary>
    [Fact]
    public async Task EnsureBackhaul_WhenCurrentlyPassive_UnsetsPassive_NeverSetsPassiveNo()
    {
        int printCount = 0;
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        printCount++;
                        Dictionary<string, string> row = BackhaulRow("*1", passive: printCount == 1 ? "" : null);
                        return [new TikFakeReSentence(row), new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/unset",
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/set",
                    static _ => [new TikFakeDoneSentence()]));

        await CreateOps().EnsureBackhaulTemplateAsync(capturing, TommyBackhaul(), CancellationToken.None);

        Assert.Contains(capturing.Unsets, u => u.Any(p =>
            p.Name == TikSpecialProperties.UnsetValueName && p.Value == "passive"));
        Assert.All(capturing.Sets, set =>
            Assert.DoesNotContain(set, p => p.Name.Equals("passive", StringComparison.OrdinalIgnoreCase)));
        Assert.All(capturing.Adds, add =>
            Assert.DoesNotContain(add, p => p.Name.Equals("passive", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Scenario: no backhaul template exists on the first print, so the ensure must create it (read-back then shows <c>*9</c>).
    /// Expected: exactly one <c>add</c> with interfaces=eth-01_BH01.Tommy-Shylock, networks=10.16.248.76/30 and cost=10, and no <c>passive</c> parameter.
    /// Invariant: a newly created backhaul template is never created passive.
    /// </summary>
    [Fact]
    public async Task EnsureBackhaul_AddPath_OmitsPassiveParameter()
    {
        int printCount = 0;
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        printCount++;
                        if (printCount == 1)
                        {
                            return [new TikFakeDoneSentence()];
                        }

                        return [new TikFakeReSentence(BackhaulRow("*9", passive: null)), new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/add",
                    static _ => [new TikFakeDoneSentence()]));

        await CreateOps().EnsureBackhaulTemplateAsync(capturing, TommyBackhaul(), CancellationToken.None);

        IReadOnlyList<(string Name, string? Value)> add = Assert.Single(capturing.Adds);
        Assert.DoesNotContain(add, p => p.Name.Equals("passive", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(add, p => p.Name == "interfaces" && p.Value == "eth-01_BH01.Tommy-Shylock");
        Assert.Contains(add, p => p.Name == "networks" && p.Value == "10.16.248.76/30");
        Assert.Contains(add, p => p.Name == "cost" && p.Value == "10");
    }

    /// <summary>
    /// Scenario: live inventory holds only the protected backhaul <c>*1</c> when a passive site network template for 10.16.11.0/24 is ensured.
    /// Expected: the site template is created with an <c>add</c>, and no <c>set</c> targets <c>*1</c>.
    /// Invariant: site-template reconciliation can never retarget the backbone template for update.
    /// </summary>
    [Fact]
    public async Task EnsureSite_CannotSelectBackboneTemplateForUpdate()
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
                            // Live inventory is only the protected backhaul — ensure must add, not retarget *1.
                            return [new TikFakeReSentence(BackhaulRow("*1", passive: null)), new TikFakeDoneSentence()];
                        }

                        return
                        [
                            new TikFakeReSentence(BackhaulRow("*1", passive: null)),
                            new TikFakeReSentence(new Dictionary<string, string>
                            {
                                [".id"] = "*A",
                                ["area"] = "BR01.POP011.016.Tommy-v2",
                                ["interfaces"] = "all",
                                ["networks"] = "10.16.11.0/24",
                                ["passive"] = "",
                                ["disabled"] = "false",
                            }),
                            new TikFakeDoneSentence(),
                        ];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/add",
                    static _ => [new TikFakeDoneSentence()]));

        await CreateOps().EnsureSiteNetworkTemplateAsync(
            capturing,
            new OspfPlanSiteNetworkTemplate
            {
                AreaName = "BR01.POP011.016.Tommy-v2",
                NetworkCidr = "10.16.11.0/24",
                Passive = true,
            },
            [TommyBackhaul()],
            CancellationToken.None);

        Assert.DoesNotContain(capturing.Sets, set => set.Any(p => p.Name == ".id" && p.Value == "*1"));
        Assert.NotEmpty(capturing.Adds);
    }

    /// <summary>
    /// Scenario: the planner is fed a post-incident Tommy snapshot where the loopback is a non-passive ptp template next to the real backhaul.
    /// Expected: the plan succeeds with a single active backhaul (non-passive, cost 10), a single passive loopback, and only passive site networks.
    /// Invariant: loopbacks are classified before ptp backhauls, so only the true backhaul stays active.
    /// </summary>
    [Fact]
    public void Planner_ClassifiesLoopbackBeforePtpBackhaul_AndKeepsBackhaulActive()
    {
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(TommyLiveBrokenSnapshot());
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        Assert.False(Assert.Single(result.Plan.ActiveBackhauls).Passive);
        Assert.Equal(10, result.Plan.ActiveBackhauls[0].Cost);
        Assert.True(Assert.Single(result.Plan.PassiveLoopbacks).Passive);
        Assert.All(result.Plan.PassiveSiteNetworks, s => Assert.True(s.Passive));
    }

    /// <summary>
    /// Scenario: a valid Tommy plan is cloned with its backhaul template marked <c>Passive=true</c>.
    /// Expected: <c>ValidatePlanInvariants</c> returns an error mentioning <c>Passive=true</c>.
    /// Invariant: a plan that would passivate the backbone adjacency is rejected before any mutation.
    /// </summary>
    [Fact]
    public void PlanInvariants_RejectPassiveBackhaul()
    {
        OspfMigrationPlan plan = CloneWithBackhauls(
            ValidTommyPlan(),
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone-v2",
                    Interface = "eth-01_BH01.Tommy-Shylock",
                    NetworkCidr = "10.16.248.76/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = true,
                },
            ]);

        IReadOnlyList<string> errors = OspfBackboneSafety.ValidatePlanInvariants(plan, TommyAddresses());
        Assert.Contains(errors, e => e.Contains("Passive=true", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: a valid Tommy plan is cloned with the backhaul network widened from /30 to 10.16.248.76/29.
    /// Expected: <c>ValidatePlanInvariants</c> returns an error mentioning <c>/30</c>.
    /// Invariant: the backhaul matcher must stay a point-to-point /30 so it cannot swallow other networks.
    /// </summary>
    [Fact]
    public void PlanInvariants_RejectNonSlash30AndOverlappingSiteMatcher()
    {
        OspfMigrationPlan plan = CloneWithBackhauls(
            ValidTommyPlan(),
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone-v2",
                    Interface = "eth-01_BH01.Tommy-Shylock",
                    NetworkCidr = "10.16.248.76/29",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
            ]);

        IReadOnlyList<string> errors = OspfBackboneSafety.ValidatePlanInvariants(plan, TommyAddresses());
        Assert.Contains(errors, e => e.Contains("/30", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: reconciler apply where fake ops report a backbone neighbour dropping from Full.
    /// Expected: OspfBackboneMutationGuardException; backhaul ensured; loopback/site not ensured.
    /// Invariant: neighbour loss aborts further OSPF mutations fail-closed.
    /// </summary>
    [Fact]
    public async Task Reconciler_AbortsFurtherMutations_WhenNeighbourDropsFromFull()
    {
        AbortingNeighbourOps ops = new();
        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
                .ApplyAsync(
                    FakeMikrotikConnections.CreateFullyPopulated(version: "7.23.8"),
                    ValidTommyPlan(),
                    managementHost: "198.18.240.11",
                    CancellationToken.None));

        Assert.Contains("neighbour dropped from Full", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(ops.BackhaulEnsured);
        Assert.False(ops.LoopbackEnsured);
        Assert.False(ops.SiteEnsured);
    }

    /// <summary>
    /// Scenario: the live inventory shows the backhaul template with <c>Passive=true</c>, while loopback and site templates look correct.
    /// Expected: verification fails, with an error mentioning <c>passive</c> or a missing active backhaul.
    /// Invariant: post-apply verification catches a passivated backbone rather than reporting success.
    /// </summary>
    [Fact]
    public async Task Verifier_DetectsBackbonePassiveRegression()
    {
        FakeInventoryOps ops = new()
        {
            Inventory = new OspfV7Inventory
            {
                Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.16.0.11", Version = "2" }],
                Areas =
                [
                    new OspfAreaRow { Name = "backbone-v2", AreaId = "0.0.0.0", Instance = "default-v2", Type = "default" },
                    new OspfAreaRow
                    {
                        Name = "BR01.POP011.016.Tommy-v2",
                        AreaId = "10.16.0.11",
                        Instance = "default-v2",
                        Type = "stub",
                        DefaultCost = "1",
                        NoSummaries = false,
                    },
                ],
                AreaRanges =
                [
                    new OspfAreaRangeRow { Area = "BR01.POP011.016.Tommy-v2", Range = "10.16.11.0/24" },
                    new OspfAreaRangeRow { Area = "BR01.POP011.016.Tommy-v2", Range = "100.99.76.16/28" },
                    new OspfAreaRangeRow { Area = "BR01.POP011.016.Tommy-v2", Range = "172.26.197.4/30" },
                ],
                Templates =
                [
                    new OspfInterfaceTemplateRow
                    {
                        Area = "backbone-v2",
                        Interfaces = "eth-01_BH01.Tommy-Shylock",
                        Networks = "10.16.248.76/30",
                        Type = "ptp",
                        Cost = "10",
                        Passive = true,
                        Disabled = false,
                    },
                    new OspfInterfaceTemplateRow
                    {
                        Area = "backbone-v2",
                        Interfaces = "Loopback",
                        Networks = "10.16.0.11/32",
                        Passive = true,
                    },
                    ..ValidTommyPlan().PassiveSiteNetworks.Select(s => new OspfInterfaceTemplateRow
                    {
                        Area = s.AreaName,
                        Interfaces = s.Interfaces,
                        Networks = s.NetworkCidr,
                        Passive = true,
                    }),
                ],
            },
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), ValidTommyPlan(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            e => e.Contains("passive", StringComparison.OrdinalIgnoreCase)
                 || e.Contains("Missing active backhaul", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: the full v7 reconciler is run against the valid Tommy plan using a recording ops fake.
    /// Expected: backhaul ensure is called at least once and only with <c>passive=False</c>; the loopback and site stages are reached.
    /// Invariant: a complete recovery run never requests a passive backbone template.
    /// </summary>
    [Fact]
    public async Task FullTommyRecoveryFlow_NeverPassivatesBackbone()
    {
        RecordingMutationOps ops = new();
        await new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
            .ApplyAsync(
                FakeMikrotikConnections.CreateFullyPopulated(version: "7.23.8"),
                ValidTommyPlan(),
                managementHost: "198.18.240.11",
                CancellationToken.None);

        Assert.NotEmpty(ops.BackhaulEnsureCalls);
        Assert.All(ops.BackhaulEnsureCalls, c => Assert.Contains("passive=False", c, StringComparison.Ordinal));
        Assert.True(ops.LoopbackEnsured);
        Assert.True(ops.SiteEnsured);
    }

    /// <summary>
    /// Scenario: source-text guard over <c>OspfMikrotikOperations.cs</c> for how area ranges are added.
    /// Expected: the source adds the range with the <c>prefix</c> parameter and does not use the legacy <c>range</c> parameter.
    /// Invariant: RouterOS v7 area-range adds use <c>prefix=</c>.
    /// </summary>
    [Fact]
    public void AreaRange_EnsureUsesPrefixProperty_NotLegacyRange()
    {
        string source = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "MikrotikWorker", "Upgrade", "Ospf", "OspfMikrotikOperations.cs"));
        Assert.Contains("add.AddParameter(\"prefix\", range.RangeCidr)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("add.AddParameter(\"range\", range.RangeCidr)", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: source-text guard over <c>OspfMikrotikOperations.cs</c> for how <c>no-summaries</c> is handled.
    /// Expected: the source references <c>no-summaries</c> but never adds it as <c>no-summaries=no</c>.
    /// Invariant: the flag is cleared via <c>unset</c>, not by writing <c>no</c>.
    /// </summary>
    [Fact]
    public void NoSummaries_IsClearedViaUnset_NotSetNo()
    {
        string source = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "MikrotikWorker", "Upgrade", "Ospf", "OspfMikrotikOperations.cs"));
        Assert.Contains("\"no-summaries\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddParameter(\"no-summaries\", \"no\")", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a parameter-capturing connection over a fake that serves the given interface-template rows for print
    /// and accepts set and unset (and add when allowed) with a done response.
    /// </summary>
    private static ParameterCapturingConnection CreateCapturing(
        IReadOnlyList<Dictionary<string, string>> printRows,
        bool allowAdd = false)
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                printRows.Select(static r => (ITikSentence)new TikFakeReSentence(r)).Append(new TikFakeDoneSentence()).ToList())
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/set",
                static _ => [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/unset",
                static _ => [new TikFakeDoneSentence()]);
        if (allowAdd)
        {
            fake = fake.WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/add",
                static _ => [new TikFakeDoneSentence()]);
        }

        return new ParameterCapturingConnection(fake);
    }

    /// <summary>
    /// Creates the real <c>OspfMikrotikOperations</c> with a null logger.
    /// </summary>
    private static OspfMikrotikOperations CreateOps() => new(NullLogger<OspfMikrotikOperations>.Instance);

    /// <summary>
    /// Planned active backhaul for the Tommy lab router (ptp, cost 10, /30, <c>Passive=false</c>) -
    /// the template whose passivation caused the lab outage.
    /// </summary>
    private static OspfPlanBackhaulTemplate TommyBackhaul() => new()
    {
        AreaName = "backbone-v2",
        Interface = "eth-01_BH01.Tommy-Shylock",
        NetworkCidr = "10.16.248.76/30",
        Cost = 10,
        Type = "ptp",
        Passive = false,
    };

    /// <summary>
    /// Live RouterOS row for the Tommy backhaul. The <c>passive</c> key is present only when
    /// <paramref name="passive"/> is non-null (empty string means the flag is set), mirroring how
    /// RouterOS prints boolean flags.
    /// </summary>
    private static Dictionary<string, string> BackhaulRow(string id, string? passive)
    {
        var d = new Dictionary<string, string>
        {
            [".id"] = id,
            ["area"] = "backbone-v2",
            ["interfaces"] = "eth-01_BH01.Tommy-Shylock",
            ["networks"] = "10.16.248.76/30",
            ["type"] = "ptp",
            ["cost"] = "10",
            ["disabled"] = "no",
        };
        if (passive is not null)
        {
            d["passive"] = passive;
        }

        return d;
    }

    /// <summary>
    /// IP address inventory for the Tommy router: backhaul /30, loopback /32, management /27 and
    /// the management host address.
    /// </summary>
    private static IReadOnlyList<OspfIpAddressRow> TommyAddresses() =>
    [
        new() { Address = "10.16.248.78/30", Network = "10.16.248.76", Interface = "eth-01_BH01.Tommy-Shylock" },
        new() { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
        new() { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "VLAN-99_MGMT_wlan" },
        new() { Address = "198.18.240.11/24", Network = "198.18.240.0", Interface = "eth-01_BH01.Tommy-Shylock" },
    ];

    /// <summary>
    /// Capture approximating the post-incident router: the loopback is a non-passive ptp template
    /// next to the real backhaul. The planner must classify loopbacks before ptp backhauls so only
    /// the true backhaul stays active.
    /// </summary>
    private static OspfCaptureSnapshot TommyLiveBrokenSnapshot() => new()
    {
        ImportId = Guid.NewGuid(),
        JobId = "job",
        RouterIp = IPAddress.Parse("198.18.240.11"),
        Identity = "BR01.POP011.016.Tommy",
        RouterOsVersion = "7.23.8",
        Architecture = "mipsbe",
        SourceKind = OspfCaptureSourceKind.RouterOsV7,
        Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.16.0.11", Version = "2" }],
        Areas =
        [
            new OspfAreaRow { Name = "backbone-v2", AreaId = "0.0.0.0", Instance = "default-v2", Type = "default" },
            new OspfAreaRow
            {
                Name = "BR01.POP011.016.Tommy-v2",
                AreaId = "10.16.0.11",
                Instance = "default-v2",
                Type = "stub",
                DefaultCost = "1",
            },
        ],
        InterfaceTemplates =
        [
            new OspfInterfaceTemplateRow
            {
                Area = "backbone-v2",
                Interfaces = "eth-01_BH01.Tommy-Shylock",
                Networks = "10.16.248.76/30",
                Type = "ptp",
                Cost = "10",
                Passive = false,
            },
            new OspfInterfaceTemplateRow
            {
                Area = "backbone-v2",
                Interfaces = "Loopback",
                Networks = "10.16.0.11",
                Type = "ptp",
                Passive = false,
            },
            new OspfInterfaceTemplateRow
            {
                Area = "BR01.POP011.016.Tommy-v2",
                Interfaces = "Bridge-VLAN-99_MGMT",
                Networks = "10.16.11.0/27",
                Type = "ptp",
                Passive = false,
            },
        ],
        IpAddresses = TommyAddresses(),
    };

    /// <summary>
    /// Builds the Tommy plan via the lab recovery builder from live inventory and a temporary
    /// reference <c>.rsc</c>, asserting it is valid.
    /// </summary>
    private static OspfMigrationPlan ValidTommyPlan()
    {
        OspfPlanValidationResult built = OspfLabRecoveryPlanBuilder.BuildFromLiveAndReference(
            Guid.NewGuid(),
            "job",
            IPAddress.Parse("198.18.240.11"),
            "BR01.POP011.016.Tommy",
            "7.23.8",
            "mipsbe",
            new OspfV7Inventory
            {
                Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.16.0.11", Version = "2" }],
                Areas =
                [
                    new OspfAreaRow { Name = "backbone-v2", AreaId = "0.0.0.0", Instance = "default-v2", Type = "default" },
                    new OspfAreaRow
                    {
                        Name = "BR01.POP011.016.Tommy-v2",
                        AreaId = "10.16.0.11",
                        Instance = "default-v2",
                        Type = "stub",
                        DefaultCost = "1",
                    },
                ],
            },
            TommyAddresses(),
            WriteTempTommyReference());
        Assert.True(built.Succeeded, string.Join("; ", built.Plan.ValidationErrors));
        return built.Plan;
    }

    /// <summary>
    /// Writes a temporary <c>.rsc</c> file holding the Tommy reference OSPF area ranges and networks, and returns
    /// its path.
    /// </summary>
    private static string WriteTempTommyReference()
    {
        string path = Path.Combine(Path.GetTempPath(), "tommy-ospf-ref-" + Guid.NewGuid().ToString("N") + ".rsc");
        File.WriteAllText(
            path,
            """
            /routing ospf area range
            add area=BR01.POP011.016.Tommy comment=MGMT range=10.16.11.0/24
            add area=BR01.POP011.016.Tommy comment=HEROTEL_ACTIVE_POOL range=100.99.76.16/28
            add area=BR01.POP011.016.Tommy comment=HEROTEL_INACTIVE_POOL range=172.26.197.4/30
            /routing ospf network
            add area=backbone network=10.16.248.76/30
            add area=backbone network=10.16.0.11/32
            add area=BR01.POP011.016.Tommy network=10.16.11.0/24
            add area=BR01.POP011.016.Tommy comment="ACS-Provisioning Subnet" network=100.67.16.0/24
            add area=BR01.POP011.016.Tommy comment="Active PPPoE Range" network=100.99.0.0/16
            add area=BR01.POP011.016.Tommy comment="Inactive PPPoE Range" network=172.26.0.0/16
            """);
        return path;
    }

    /// <summary>
    /// Copies a migration plan field for field but replaces its active backhaul templates with the supplied list.
    /// </summary>
    private static OspfMigrationPlan CloneWithBackhauls(
        OspfMigrationPlan plan,
        IReadOnlyList<OspfPlanBackhaulTemplate> backhauls) =>
        new()
        {
            ImportId = plan.ImportId,
            JobId = plan.JobId,
            RouterIp = plan.RouterIp,
            Identity = plan.Identity,
            SourceRouterOsVersion = plan.SourceRouterOsVersion,
            Architecture = plan.Architecture,
            SourceKind = plan.SourceKind,
            ContentHash = plan.ContentHash,
            InstanceName = plan.InstanceName,
            RouterId = plan.RouterId,
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = backhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = plan.NormalisationNotes,
            ValidationErrors = plan.ValidationErrors,
        };

    /// <summary>
    /// Walks up from the test binary directory to the folder containing
    /// <c>MikrotikWorker.csproj</c>, falling back to a relative path; used by the source-text guard
    /// tests.
    /// </summary>
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "src", "MikrotikWorker", "MikrotikWorker.csproj")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    /// <summary>
    /// Connection wrapper that captures interface-template set, add and unset commands so their parameters can be
    /// inspected after the production code has added them.
    /// </summary>
    private sealed class ParameterCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        /// <summary>
        /// Interface-template <c>set</c> commands captured at creation; parameters are read lazily.
        /// </summary>
        private readonly List<ITikCommand> _sets = [];

        /// <summary>
        /// Interface-template <c>add</c> commands captured at creation; parameters are read lazily.
        /// </summary>
        private readonly List<ITikCommand> _adds = [];

        /// <summary>
        /// Interface-template <c>unset</c> commands captured at creation; parameters are read
        /// lazily.
        /// </summary>
        private readonly List<ITikCommand> _unsets = [];

        /// <summary>
        /// Parameters of every captured template <c>set</c>; none may contain <c>passive</c>.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Sets => Snapshot(_sets);

        /// <summary>
        /// Parameters of every captured template <c>add</c>; none may contain <c>passive</c>.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Adds => Snapshot(_adds);

        /// <summary>
        /// Parameters of every captured template <c>unset</c>; clearing passive must use
        /// <c>unset</c> with <c>value-name=passive</c>.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Unsets => Snapshot(_unsets);

        /// <summary>
        /// Tracks template set/add/unset commands, then returns the inner command.
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
        /// Routes a template command into the matching captured list based on its command text.
        /// </summary>
        private void Track(string commandText, ITikCommand cmd)
        {
            if (commandText.Equals("/routing/ospf/interface-template/set", StringComparison.Ordinal))
            {
                _sets.Add(cmd);
            }
            else if (commandText.Equals("/routing/ospf/interface-template/add", StringComparison.Ordinal))
            {
                _adds.Add(cmd);
            }
            else if (commandText.Equals("/routing/ospf/interface-template/unset", StringComparison.Ordinal))
            {
                _unsets.Add(cmd);
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

    /// <summary>
    /// Ops fake that throws the backbone guard exception once the backhaul has been ensured, simulating the OSPF
    /// neighbour leaving Full so later stages must be skipped.
    /// </summary>
    private sealed class AbortingNeighbourOps : RecordingMutationOps
    {
        /// <summary>
        /// Throws the backbone guard exception once the backhaul has been ensured, simulating the
        /// OSPF neighbour dropping from Full right after the backbone mutation so later stages must
        /// be skipped.
        /// </summary>
        public override Task AssertBackboneStillActiveAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            OspfManagementPlaneBaseline baseline,
            CancellationToken cancellationToken = default)
        {
            if (BackhaulEnsured)
            {
                throw new OspfBackboneMutationGuardException(
                    "OSPF neighbour dropped from Full after backbone mutation (prior address=10.16.248.77, router-id=198.18.255.253). Aborting further OSPF mutations.");
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Ops fake for the v7 reconcile and verify path that records backhaul, loopback and site ensure calls
    /// (including passive flags), returns a healthy inventory, baseline and Full neighbour, and treats other
    /// mutations as no-ops.
    /// </summary>
    private class RecordingMutationOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// True once the backhaul ensure ran; subclasses read it to decide when to simulate a
        /// failure.
        /// </summary>
        public bool BackhaulEnsured { get; protected set; }

        /// <summary>
        /// True once the loopback ensure ran; must stay false when the guard aborts after the
        /// backhaul stage.
        /// </summary>
        public bool LoopbackEnsured { get; private set; }

        /// <summary>
        /// True once the site ensure ran; must stay false when the guard aborts after the backhaul
        /// stage.
        /// </summary>
        public bool SiteEnsured { get; private set; }

        /// <summary>
        /// Each backhaul ensure as <c>interface|network|passive=X</c>; asserts the backbone is
        /// always ensured with <c>Passive=false</c>.
        /// </summary>
        public List<string> BackhaulEnsureCalls { get; } = [];

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
        /// Records the ensure (flag and interface/network/passive tuple) so tests can assert it is
        /// requested non-passive.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default)
        {
            BackhaulEnsured = true;
            BackhaulEnsureCalls.Add($"{template.Interface}|{template.NetworkCidr}|passive={template.Passive}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Marks the loopback stage as reached.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            LoopbackEnsured = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Marks the site stage as reached.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            SiteEnsured = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op: the post-ensure site-template read-back is assumed to pass in this scenario.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: legacy interface-template cleanup is not under test in this scenario.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: OSPFv3 removal is not under test in this scenario.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Returns an inventory in which the protected backhaul <c>*1</c> is active and
        /// non-passive, so backbone safety checks start from a healthy baseline. Virtual so <see
        /// cref="FakeInventoryOps"/> can substitute its own inventory.
        /// </summary>
        public virtual Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OspfV7Inventory
            {
                Instances =
                [
                    new OspfInstanceRow
                    {
                        Id = "*inst",
                        Name = "default-v2",
                        RouterId = "10.16.0.11",
                        Version = "2",
                    },
                ],
                Areas =
                [
                    new OspfAreaRow
                    {
                        Id = "*a0",
                        Name = "backbone-v2",
                        AreaId = "0.0.0.0",
                        Instance = "default-v2",
                        Type = "default",
                    },
                    new OspfAreaRow
                    {
                        Id = "*a1",
                        Name = "BR01.POP011.016.Tommy-v2",
                        AreaId = "10.16.0.11",
                        Instance = "default-v2",
                        Type = "stub",
                    },
                ],
                Templates =
                [
                    new OspfInterfaceTemplateRow
                    {
                        Id = "*1",
                        Area = "backbone-v2",
                        Interfaces = "eth-01_BH01.Tommy-Shylock",
                        Networks = "10.16.248.76/30",
                        Type = "ptp",
                        Cost = "10",
                        Passive = false,
                    },
                ],
            });

        /// <summary>
        /// No-op: the router-id in the fake inventory is already correct, so no router-id write is
        /// modelled.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Returns the Tommy router address inventory (backhaul /30, loopback /32, management
        /// addresses).
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(TommyAddresses());

        /// <summary>
        /// Returns the Tommy lab baseline so backbone guards run against realistic management-plane
        /// state.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        /// <summary>
        /// No-op by default (backbone assumed healthy); virtual so a subclass can simulate
        /// adjacency loss.
        /// </summary>
        public virtual Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns one Full neighbour (10.16.248.77, router-id 198.18.255.253) so the
        /// neighbour-based backbone guard has an adjacency to protect.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>(
            [
                new Dictionary<string, string>
                {
                    ["address"] = "10.16.248.77",
                    ["router-id"] = "198.18.255.253",
                    ["state"] = "Full",
                },
            ]);
    }

    /// <summary>
    /// Recording ops fake that returns an injected v7 inventory (for example one with a passive backhaul) to the
    /// verifier instead of the healthy default.
    /// </summary>
    private sealed class FakeInventoryOps : RecordingMutationOps
    {
        /// <summary>
        /// Inventory returned to the verifier; tests supply a deliberately broken one (passive
        /// backhaul).
        /// </summary>
        public required OspfV7Inventory Inventory { get; init; }

        /// <summary>
        /// Returns the injected <see cref="Inventory"/> instead of the healthy default.
        /// </summary>
        public override Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);
    }
}
