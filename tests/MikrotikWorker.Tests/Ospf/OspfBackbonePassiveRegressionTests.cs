using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Critical regression: EnsureBackhaul must never emit a RouterOS flag enable for <c>passive</c>
/// (the lab failure was <c>set *1 … passive</c> which destroyed the backbone adjacency).
/// </summary>
public sealed class OspfBackbonePassiveRegressionTests
{
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

    [Fact]
    public async Task EnsureSite_CannotSelectBackboneTemplateForUpdate()
    {
        ParameterCapturingConnection capturing = CreateCapturing(
            printRows: [BackhaulRow("*1", passive: null)],
            allowAdd: true);

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
                    new OspfAreaRow { Name = "backbone-v2", AreaId = "0.0.0.0", Type = "default" },
                    new OspfAreaRow
                    {
                        Name = "BR01.POP011.016.Tommy-v2",
                        AreaId = "10.16.0.11",
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

    [Fact]
    public void AreaRange_EnsureUsesPrefixProperty_NotLegacyRange()
    {
        string source = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "MikrotikWorker", "Upgrade", "Ospf", "OspfMikrotikOperations.cs"));
        Assert.Contains("add.AddParameter(\"prefix\", range.RangeCidr)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("add.AddParameter(\"range\", range.RangeCidr)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSummaries_IsClearedViaUnset_NotSetNo()
    {
        string source = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "MikrotikWorker", "Upgrade", "Ospf", "OspfMikrotikOperations.cs"));
        Assert.Contains("\"no-summaries\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddParameter(\"no-summaries\", \"no\")", source, StringComparison.Ordinal);
    }

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

    private static OspfMikrotikOperations CreateOps() => new(NullLogger<OspfMikrotikOperations>.Instance);

    private static OspfPlanBackhaulTemplate TommyBackhaul() => new()
    {
        AreaName = "backbone-v2",
        Interface = "eth-01_BH01.Tommy-Shylock",
        NetworkCidr = "10.16.248.76/30",
        Cost = 10,
        Type = "ptp",
        Passive = false,
    };

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

    private static IReadOnlyList<OspfIpAddressRow> TommyAddresses() =>
    [
        new() { Address = "10.16.248.78/30", Network = "10.16.248.76", Interface = "eth-01_BH01.Tommy-Shylock" },
        new() { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
        new() { Address = "198.18.240.11/24", Network = "198.18.240.0", Interface = "eth-01_BH01.Tommy-Shylock" },
    ];

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

    private sealed class ParameterCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        private readonly List<ITikCommand> _sets = [];
        private readonly List<ITikCommand> _adds = [];
        private readonly List<ITikCommand> _unsets = [];

        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Sets => Snapshot(_sets);
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Adds => Snapshot(_adds);
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

    private sealed class AbortingNeighbourOps : RecordingMutationOps
    {
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

    private class RecordingMutationOps : IOspfMikrotikOperations
    {
        public bool BackhaulEnsured { get; protected set; }
        public bool LoopbackEnsured { get; private set; }
        public bool SiteEnsured { get; private set; }
        public List<string> BackhaulEnsureCalls { get; } = [];

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
            BackhaulEnsured = true;
            BackhaulEnsureCalls.Add($"{template.Interface}|{template.NetworkCidr}|passive={template.Passive}");
            return Task.CompletedTask;
        }
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            LoopbackEnsured = true;
            return Task.CompletedTask;
        }
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            SiteEnsured = true;
            return Task.CompletedTask;
        }
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public virtual Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OspfV7Inventory
            {
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
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(TommyAddresses());
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));
        public virtual Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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

    private sealed class FakeInventoryOps : RecordingMutationOps
    {
        public required OspfV7Inventory Inventory { get; init; }

        public override Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);
    }
}
