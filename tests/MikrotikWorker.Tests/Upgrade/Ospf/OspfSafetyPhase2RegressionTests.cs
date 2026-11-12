// -----------------------------------------------------------------------------
// File: OspfSafetyPhase2RegressionTests.cs
//
// Description:
// Phase-2 safety regressions: management-route observation, loopback recreate
// read-back, protected bulk-disable, mutation sequencing, and fail-closed
// unset/read-back paths.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
    /// <summary>
    /// Scenario: management-plane capture against faked OSPF neighbour and default-route prints.
    /// Expected: baseline records the observed next-hop and outgoing interface from the route/neighbour data.
    /// Invariant: management-plane safety observations must include next-hop and egress interface detail.
    /// </summary>
public sealed class OspfSafetyPhase2RegressionTests
{
    /// <summary>
    /// Scenario: management-plane capture against faked neighbour and default-route prints.
    /// Expected: baseline records next-hop and outgoing-interface observations.
    /// Invariant: management-plane safety capture includes next-hop and egress interface detail.
    /// </summary>
    [Fact]
    public async Task CaptureManagementPlane_ObservesRouteNextHopAndOutgoingInterface()
    {
        ParameterCapturingConnection capturing = new(
            EnrichInventoryPrints(new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(BackhaulRow()), new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/neighbor/print",
                    _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            ["address"] = "10.16.248.77",
                            ["router-id"] = "198.18.255.253",
                            ["state"] = "Full",
                        }),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/ip/route/print",
                    _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            ["dst-address"] = "0.0.0.0/0",
                            ["gateway"] = "10.16.248.77%eth-01_BH01.Tommy-Shylock",
                            ["immediate-gw"] = "10.16.248.77%eth-01_BH01.Tommy-Shylock",
                            ["active"] = "true",
                        }),
                        new TikFakeDoneSentence(),
                    ])));

        OspfManagementPlaneBaseline baseline = await CreateOps()
            .CaptureManagementPlaneBaselineAsync(
                capturing,
                MinimalPlan(),
                "198.18.240.11",
                CancellationToken.None);

        Assert.True(baseline.ManagementRouteObserved);
        Assert.True(baseline.ManagementRouteActive);
        Assert.Equal("0.0.0.0/0", baseline.ManagementRouteDst);
        Assert.Equal("10.16.248.77%eth-01_BH01.Tommy-Shylock", baseline.ManagementRouteGateway);
        Assert.Equal("eth-01_BH01.Tommy-Shylock", baseline.ManagementOutgoingInterface);
        Assert.True(baseline.ManagementRouteDependsOnBackbone);
        Assert.False(baseline.EndToEndManagementConnectivityVerified);
        Assert.Equal("*1", baseline.BackboneTemplateId);
        Assert.False(baseline.BackboneTemplatePassive);
    }

    /// <summary>
    /// Scenario: the backbone neighbour stays Full, but the active default route moves from the backhaul gateway (10.16.248.77 via eth-01_BH01.Tommy-Shylock) to 10.99.0.1 via ether99 after the baseline was captured.
    /// Expected: AssertBackboneStillActiveAsync throws OspfBackboneMutationGuardException whose message mentions 'Management route' and 'not end-to-end'.
    /// Invariant: backbone health is judged by the management route as well as neighbour state.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_DetectsManagementRouteChange_NotJustNeighbourFull()
    {
        int routePrints = 0;
        ParameterCapturingConnection capturing = new(
            EnrichInventoryPrints(new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(BackhaulRow()), new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/neighbor/print",
                    _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            ["address"] = "10.16.248.77",
                            ["router-id"] = "198.18.255.253",
                            ["state"] = "Full",
                        }),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/ip/route/print",
                    _ =>
                    {
                        routePrints++;
                        string gw = routePrints == 1
                            ? "10.16.248.77%eth-01_BH01.Tommy-Shylock"
                            : "10.99.0.1%ether99";
                        return
                        [
                            new TikFakeReSentence(new Dictionary<string, string>
                            {
                                ["dst-address"] = "0.0.0.0/0",
                                ["gateway"] = gw,
                                ["immediate-gw"] = gw,
                                ["active"] = "true",
                            }),
                            new TikFakeDoneSentence(),
                        ];
                    })));

        OspfMikrotikOperations ops = CreateOps();
        OspfMigrationPlan plan = MinimalPlan();
        OspfManagementPlaneBaseline baseline = await ops.CaptureManagementPlaneBaselineAsync(
            capturing,
            plan,
            "198.18.240.11",
            CancellationToken.None);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            ops.AssertBackboneStillActiveAsync(capturing, plan, baseline, CancellationToken.None));

        Assert.Contains("Management route", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not end-to-end", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: the router returns a trap ('input does not match any value of value-name') when the passive flag on the backhaul template is unset.
    /// Expected: EnsureBackhaulTemplateAsync throws.
    /// Invariant: a failed unset of passive on a backhaul aborts the migration instead of being ignored.
    /// </summary>
    [Fact]
    public async Task EnsureBackhaul_UnsetFailure_Aborts()
    {
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(BackhaulRow(passive: "")), new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/unset",
                    static _ =>
                    [
                        new TikFakeTrapSentence("failure: input does not match any value of value-name"),
                        new TikFakeDoneSentence(),
                    ]));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateOps().EnsureBackhaulTemplateAsync(capturing, BackhaulPlan(), CancellationToken.None));
    }

    /// <summary>
    /// Scenario: unset and set both report success, but the read-back still shows the backhaul template as passive.
    /// Expected: OspfBackboneMutationGuardException whose message mentions 'passive'.
    /// Invariant: the backhaul change is only accepted after a read-back confirms it is no longer passive.
    /// </summary>
    [Fact]
    public async Task EnsureBackhaul_ReadBackStillPassive_Aborts()
    {
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(BackhaulRow(passive: "")), new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/unset",
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/set",
                    static _ => [new TikFakeDoneSentence()]));

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().EnsureBackhaulTemplateAsync(capturing, BackhaulPlan(), CancellationToken.None));
        Assert.Contains("passive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: the loopback template to be recreated targets the interface and network of an existing protected backhaul (eth-01_BH01.Tommy-Shylock, 10.16.248.76/30).
    /// Expected: EnsureLoopbackTemplateAsync throws OspfBackboneMutationGuardException.
    /// Invariant: loopback reconciliation must never delete or recreate a protected backhaul template.
    /// </summary>
    [Fact]
    public async Task EnsureLoopback_Recreate_RefusesProtectedBackhaul()
    {
        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ => [new TikFakeReSentence(BackhaulRow()), new TikFakeDoneSentence()]));

        await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().EnsureLoopbackTemplateAsync(
                capturing,
                new OspfPlanLoopbackTemplate
                {
                    AreaName = "backbone-v2",
                    Interface = "eth-01_BH01.Tommy-Shylock",
                    NetworkCidr = "10.16.248.76/30",
                    Passive = true,
                },
                [BackhaulPlan()],
                CancellationToken.None));
    }

    /// <summary>
    /// Scenario: the existing Loopback template is type=ptp; after it is removed the read-back shows a passive type=broadcast replacement.
    /// Expected: exactly one add is issued, it has no 'type' parameter and carries passive.
    /// Invariant: a ptp loopback is recreated passive without a forced type and the result is verified.
    /// </summary>
    [Fact]
    public async Task EnsureLoopback_PtpRecreate_OmitsType_VerifiesPassive()
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
                                new TikFakeReSentence(new Dictionary<string, string>
                                {
                                    [".id"] = "*2",
                                    ["area"] = "backbone-v2",
                                    ["interfaces"] = "Loopback",
                                    ["networks"] = "10.16.0.11/32",
                                    ["type"] = "ptp",
                                    ["passive"] = "",
                                    ["disabled"] = "false",
                                }),
                                new TikFakeDoneSentence(),
                            ];
                        }

                        return
                        [
                            new TikFakeReSentence(new Dictionary<string, string>
                            {
                                [".id"] = "*9",
                                ["area"] = "backbone-v2",
                                ["interfaces"] = "Loopback",
                                ["networks"] = "10.16.0.11/32",
                                ["type"] = "broadcast",
                                ["passive"] = "",
                                ["disabled"] = "false",
                            }),
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
            new OspfPlanLoopbackTemplate
            {
                AreaName = "backbone-v2",
                Interface = "Loopback",
                NetworkCidr = "10.16.0.11/32",
                Passive = true,
            },
            [BackhaulPlan()],
            CancellationToken.None);

        IReadOnlyList<(string Name, string? Value)> add = Assert.Single(capturing.Adds);
        Assert.DoesNotContain(add, p => p.Name.Equals("type", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(add, p => p.Name == "passive" && p.Value == "");
        Assert.Contains(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*2"));
        Assert.DoesNotContain(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*1"));
    }

    /// <summary>
    /// Scenario: the router holds the planned backhaul, loopback and site templates plus a legacy Bridge-VLAN-99_MGMT /27 template (*7).
    /// Expected: exactly one removal, for *7; the backhaul (*1) is never removed.
    /// Invariant: obsolete cleanup removes legacy templates only and leaves the protected backhaul untouched.
    /// </summary>
    [Fact]
    public async Task RemoveObsolete_RemovesLegacy_NotProtectedBackhaul()
    {
        int removeCount = 0;
        List<Dictionary<string, string>> planned =
        [
            BackhaulRow(),
            new Dictionary<string, string>
            {
                [".id"] = "*9",
                ["area"] = "backbone-v2",
                ["interfaces"] = "Loopback",
                ["networks"] = "10.16.0.11/32",
                ["passive"] = "",
                ["disabled"] = "false",
            },
            new Dictionary<string, string>
            {
                [".id"] = "*A",
                ["area"] = "site",
                ["interfaces"] = "all",
                ["networks"] = "10.16.11.0/24",
                ["passive"] = "",
                ["disabled"] = "false",
            },
        ];
        Dictionary<string, string> obsolete = new()
        {
            [".id"] = "*7",
            ["area"] = "site",
            ["interfaces"] = "Bridge-VLAN-99_MGMT",
            ["networks"] = "10.16.11.0/27",
            ["passive"] = "",
            ["disabled"] = "false",
        };

        ParameterCapturingConnection capturing = new(
            EnrichInventoryPrints(
                new TikFakeConnection()
                    .WithResponse(
                        static rows => rows.First() == "/routing/ospf/interface-template/print",
                        _ =>
                        {
                            List<Dictionary<string, string>> view = [..planned];
                            if (removeCount == 0)
                            {
                                view.Add(obsolete);
                            }

                            return
                            [
                                ..view.Select(static row => new TikFakeReSentence(new Dictionary<string, string>(row))),
                                new TikFakeDoneSentence(),
                            ];
                        })
                    .WithResponse(
                        static rows => rows.First() == "/routing/ospf/interface-template/remove",
                        _ =>
                        {
                            removeCount++;
                            return [new TikFakeDoneSentence()];
                        })));

        await CreateOps().RemoveObsoleteTemplatesAsync(
            capturing,
            MinimalPlan(),
            CancellationToken.None);

        Assert.Equal(1, removeCount);
        Assert.DoesNotContain(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*1"));
        Assert.Contains(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*7"));
    }

    /// <summary>
    /// Scenario: the router's templates contain no template for the plan's protected backhaul (only an unrelated ether99 template).
    /// Expected: OspfBackboneMutationGuardException mentioning 'Protected backhaul' and no removals.
    /// Invariant: cleanup refuses to run when a protected backhaul cannot be found.
    /// </summary>
    [Fact]
    public async Task RemoveObsolete_MissingProtectedBackhaul_FailsClosed()
    {
        ParameterCapturingConnection capturing = new(
            EnrichInventoryPrints(
                new TikFakeConnection()
                    .WithResponse(
                        static rows => rows.First() == "/routing/ospf/interface-template/print",
                        _ =>
                        [
                            new TikFakeReSentence(new Dictionary<string, string>
                            {
                                [".id"] = "*7",
                                ["area"] = "backbone-v2",
                                ["interfaces"] = "ether99",
                                ["networks"] = "10.255.255.0/30",
                                ["disabled"] = "false",
                            }),
                            new TikFakeDoneSentence(),
                        ])));

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(
                capturing,
                MinimalPlan(),
                CancellationToken.None));
        Assert.Contains("Protected backhaul", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(capturing.Removes);
    }

    /// <summary>
    /// Scenario: run the reconciler on a minimal plan using an operations fake that records the sequence of calls.
    /// Expected: a baseline is returned and events occur in the order backhaul, assert, loopback, assert, site, assert, remove-obsolete, assert, v3, assert.
    /// Invariant: a backbone safety assertion runs after every mutation stage, including OSPFv3 removal.
    /// </summary>
    [Fact]
    public async Task Reconciler_AssertsAfterEveryMutationStage_IncludingOspfV3()
    {
        SequencingOps ops = new();
        OspfManagementPlaneBaseline baseline = await new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
            .ApplyAsync(
                FakeMikrotikConnections.CreateFullyPopulated(version: "7.23.8"),
                MinimalPlan(),
                managementHost: "198.18.240.11",
                CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.Equal(
            [
                "backhaul",
                "assert-after-backhaul",
                "loopback",
                "assert-after-loopback",
                "site",
                "assert-site",
                "assert-after-sites",
                "remove-obsolete",
                "assert-site",
                "assert-after-remove-obsolete",
                "v3",
                "assert-after-v3",
            ],
            ops.Events);
    }

    /// <summary>
    /// Scenario: the safety assertion fails right after the backhaul stage.
    /// Expected: OspfBackboneMutationGuardException is thrown; the backhaul stage ran but the loopback, site, remove-obsolete and v3 stages did not.
    /// Invariant: a failed backbone assertion stops the migration before any later mutation.
    /// </summary>
    [Fact]
    public async Task Reconciler_SafetyFailure_PreventsLaterStages()
    {
        SequencingOps ops = new() { FailOnAssertAfter = "assert-after-backhaul" };
        await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
                .ApplyAsync(
                    FakeMikrotikConnections.CreateFullyPopulated(version: "7.23.8"),
                    MinimalPlan(),
                    managementHost: "198.18.240.11",
                    CancellationToken.None));

        Assert.Contains(ops.Events, e => e == "backhaul");
        Assert.DoesNotContain(ops.Events, e => e == "loopback");
        Assert.DoesNotContain(ops.Events, e => e == "site");
        Assert.DoesNotContain(ops.Events, e => e == "remove-obsolete");
        Assert.DoesNotContain(ops.Events, e => e == "v3");
    }

    /// <summary>
    /// Creates the real <c>OspfMikrotikOperations</c> with a null logger.
    /// </summary>
    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    /// <summary>
    /// Protected active backhaul (ptp cost 10, /30, <c>Passive=false</c>) that no stage may remove,
    /// passivate or retarget.
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
    /// Plan with backbone and site areas, the protected backhaul, loopback and site matcher, and
    /// one superseded legacy template, enough to drive every mutation stage.
    /// </summary>
    private static OspfMigrationPlan MinimalPlan() => new()
    {
        ImportId = Guid.NewGuid(),
        JobId = "job",
        RouterIp = "198.18.240.11",
        Identity = "BR01.POP011.016.Tommy",
        SourceRouterOsVersion = "7.23.8",
        Architecture = "mipsbe",
        SourceKind = OspfCaptureSourceKind.RouterOsV7,
        InstanceName = "default-v2",
        RouterId = "10.16.0.11",
        TargetInstanceName = OspfFleetNaming.Ros7InstanceName,
        TargetBackboneAreaName = OspfFleetNaming.Ros7BackboneAreaName,
        TargetSiteAreaName = "site",
        LoopbackInterfaceName = "Loopback",
        LoopbackCidr = "10.16.0.11/32",
        DerivedManagementCidr = "10.16.11.0/24",
        Areas =
        [
            new OspfPlanArea
            {
                Name = "backbone-v2",
                AreaId = "0.0.0.0",
                TargetType = "default",
                IsBackbone = true,
            },
            new OspfPlanArea
            {
                Name = "site",
                AreaId = "10.16.0.11",
                TargetType = "stub",
                IsBackbone = false,
                DefaultCost = "1",
            },
        ],
        ActiveBackhauls = [BackhaulPlan()],
        PassiveLoopbacks =
        [
            new OspfPlanLoopbackTemplate
            {
                AreaName = "backbone-v2",
                Interface = "Loopback",
                NetworkCidr = "10.16.0.11/32",
                Passive = true,
            },
        ],
        PassiveSiteNetworks =
        [
            new OspfPlanSiteNetworkTemplate
            {
                AreaName = "site",
                Interfaces = "all",
                NetworkCidr = "10.16.11.0/24",
                Passive = true,
            },
        ],
        SupersededTemplates =
        [
            OspfTemplateSemanticIdentity.FromCaptured(
                "site",
                "Bridge-VLAN-99_MGMT",
                "10.16.11.0/27",
                passive: true,
                new OspfCapturedTemplateSemantics { AreaInstanceName = "default-v2" }),
        ],
        AreaRanges = [],
        ValidationErrors = [],
        NormalisationNotes = [],
        ContentHash = "test",
    };

    /// <summary>
    /// Live row for the protected backhaul (<c>*1</c>); pass a <paramref name="passive"/> value
    /// (empty string = flag set) to model a passivated backhaul.
    /// </summary>
    private static Dictionary<string, string> BackhaulRow(string? passive = null)
    {
        Dictionary<string, string> d = new()
        {
            [".id"] = "*1",
            ["area"] = "backbone-v2",
            ["interfaces"] = "eth-01_BH01.Tommy-Shylock",
            ["networks"] = "10.16.248.76/30",
            ["type"] = "ptp",
            ["cost"] = "10",
            ["disabled"] = "false",
        };
        if (passive is not null)
        {
            d["passive"] = passive;
        }

        return d;
    }

    /// <summary>
    /// Adds fake responses for instance, area, area-range and routing-filter prints so inventory
    /// reads succeed without per-test setup.
    /// </summary>
    private static TikFakeConnection EnrichInventoryPrints(TikFakeConnection fake) =>
        fake
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/instance/print",
                static _ =>
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*i",
                        ["name"] = "default-v2",
                        ["version"] = "2",
                        ["router-id"] = "10.16.0.11",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/print",
                static _ =>
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*ab",
                        ["name"] = "backbone-v2",
                        ["instance"] = "default-v2",
                    }),
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*as",
                        ["name"] = "site",
                        ["instance"] = "default-v2",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/range/print",
                static _ => [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First().StartsWith("/routing/filter", StringComparison.Ordinal),
                static _ => [new TikFakeDoneSentence()]);

    /// <summary>
    /// Connection wrapper that captures interface-template set, add, remove and unset commands so their parameters
    /// can be inspected after the production code has added them.
    /// </summary>
    private sealed class ParameterCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        /// <summary>
        /// Template <c>set</c> commands captured at creation; parameters are read lazily.
        /// </summary>
        private readonly List<ITikCommand> _sets = [];

        /// <summary>
        /// Template <c>add</c> commands captured at creation; parameters are read lazily.
        /// </summary>
        private readonly List<ITikCommand> _adds = [];

        /// <summary>
        /// Template <c>remove</c> commands captured at creation; parameters are read lazily.
        /// </summary>
        private readonly List<ITikCommand> _removes = [];

        /// <summary>
        /// Template <c>unset</c> commands captured at creation; parameters are read lazily.
        /// </summary>
        private readonly List<ITikCommand> _unsets = [];

        /// <summary>
        /// Parameters of every captured template <c>set</c>.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Sets => Snapshot(_sets);

        /// <summary>
        /// Parameters of every captured template <c>add</c>; the loopback recreate must omit
        /// <c>type</c>.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Adds => Snapshot(_adds);

        /// <summary>
        /// Parameters of every captured template <c>remove</c>; asserts legacy rows are removed and
        /// the protected backhaul never is.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Removes => Snapshot(_removes);

        /// <summary>
        /// Parameters of every captured template <c>unset</c>.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Unsets => Snapshot(_unsets);

        /// <summary>
        /// Tracks template set/add/remove/unset commands, then returns the inner command.
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
            else if (commandText.Equals("/routing/ospf/interface-template/remove", StringComparison.Ordinal))
            {
                _removes.Add(cmd);
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
    /// Ops fake that logs each mutation stage and the backbone assertion owed after it, and can throw at a chosen
    /// checkpoint to simulate a safety abort.
    /// </summary>
    private sealed class SequencingOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Ordered log of stages and the backbone assertions that follow each; asserts the guard
        /// runs after every mutation, including OSPFv3 removal, and that later stages are skipped
        /// after a failure.
        /// </summary>
        public List<string> Events { get; } = [];

        /// <summary>
        /// Label of the backbone assertion that should throw (e.g. <c>assert-after-backhaul</c>),
        /// modelling a safety failure at a chosen point.
        /// </summary>
        public string? FailOnAssertAfter { get; init; }

        /// <summary>
        /// Label of the assertion owed for the most recent mutation, emitted by the next backbone
        /// assertion.
        /// </summary>
        private string? _lastAssertLabel;

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
        /// Records <c>backhaul</c> and arms the <c>assert-after-backhaul</c> checkpoint.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default)
        {
            Events.Add("backhaul");
            _lastAssertLabel = "assert-after-backhaul";
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>loopback</c> and arms the <c>assert-after-loopback</c> checkpoint.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Events.Add("loopback");
            _lastAssertLabel = "assert-after-loopback";
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>site</c> and arms the <c>assert-after-sites</c> checkpoint.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Events.Add("site");
            _lastAssertLabel = "assert-after-sites";
            return Task.CompletedTask;
        }


        /// <summary>
        /// Records <c>assert-site</c>; the site matcher is verified before and after obsolete
        /// cleanup.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default)
        {
            Events.Add("assert-site");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>remove-obsolete</c> and arms the <c>assert-after-remove-obsolete</c>
        /// checkpoint.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default)
        {
            Events.Add("remove-obsolete");
            _lastAssertLabel = "assert-after-remove-obsolete";
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>v3</c> and arms the <c>assert-after-v3</c> checkpoint, so OSPFv3 removal is
        /// also covered by the backbone guard.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default)
        {
            Events.Add("v3");
            _lastAssertLabel = "assert-after-v3";
            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns an inventory matching <c>MinimalPlan()</c> so identity resolution succeeds.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.InventoryMatchingPlan(MinimalPlan()));

        /// <summary>
        /// No-op: the router-id in the fake inventory is already correct, so no router-id write is
        /// modelled.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
        /// Records the label armed by the preceding mutation and throws when it matches <see
        /// cref="FailOnAssertAfter"/>, simulating a safety abort at that stage.
        /// </summary>
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default)
        {
            string label = _lastAssertLabel ?? "assert";
            Events.Add(label);
            if (FailOnAssertAfter is not null && label == FailOnAssertAfter)
            {
                throw new OspfBackboneMutationGuardException("forced safety abort");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns no neighbours; the adjacency guard is not exercised by this scenario.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
    }
}
