using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Phase-2 safety regressions: management-route observation, loopback recreate read-back,
/// protected bulk-disable, mutation sequencing, and fail-closed unset/read-back paths.
/// </summary>
public sealed class OspfSafetyPhase2RegressionTests
{
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
                "assert-after-sites",
                "remove-obsolete",
                "assert-after-remove-obsolete",
                "v3",
                "assert-after-v3",
            ],
            ops.Events);
    }

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

    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    private static OspfPlanBackhaulTemplate BackhaulPlan() => new()
    {
        AreaName = "backbone-v2",
        Interface = "eth-01_BH01.Tommy-Shylock",
        NetworkCidr = "10.16.248.76/30",
        Cost = 10,
        Type = "ptp",
        Passive = false,
    };

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
        Areas =
        [
            new OspfPlanArea
            {
                Name = "backbone-v2",
                AreaId = "0.0.0.0",
                TargetType = "default",
                IsBackbone = true,
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

    private sealed class ParameterCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        private readonly List<ITikCommand> _sets = [];
        private readonly List<ITikCommand> _adds = [];
        private readonly List<ITikCommand> _removes = [];
        private readonly List<ITikCommand> _unsets = [];

        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Sets => Snapshot(_sets);
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Adds => Snapshot(_adds);
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Removes => Snapshot(_removes);
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Unsets => Snapshot(_unsets);

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

        private static IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Snapshot(List<ITikCommand> commands) =>
            commands
                .Select(static c => (IReadOnlyList<(string Name, string? Value)>)c.Parameters
                    .Select(static p => (p.Name, p.Value))
                    .ToList())
                .ToList();
    }

    private sealed class SequencingOps : IOspfMikrotikOperations
    {
        public List<string> Events { get; } = [];
        public string? FailOnAssertAfter { get; init; }
        private string? _lastAssertLabel;

        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default)
        {
            Events.Add("backhaul");
            _lastAssertLabel = "assert-after-backhaul";
            return Task.CompletedTask;
        }

        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Events.Add("loopback");
            _lastAssertLabel = "assert-after-loopback";
            return Task.CompletedTask;
        }

        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Events.Add("site");
            _lastAssertLabel = "assert-after-sites";
            return Task.CompletedTask;
        }

        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default)
        {
            Events.Add("remove-obsolete");
            _lastAssertLabel = "assert-after-remove-obsolete";
            return Task.CompletedTask;
        }

        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default)
        {
            Events.Add("v3");
            _lastAssertLabel = "assert-after-v3";
            return Task.CompletedTask;
        }

        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OspfV7Inventory());
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OspfIpAddressRow>>([]);
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

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

        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
    }
}
