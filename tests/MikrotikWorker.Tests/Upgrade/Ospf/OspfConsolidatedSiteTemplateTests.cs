using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
public sealed class OspfConsolidatedSiteTemplateTests
{
    [Fact]
    public void RecoveryPlan_MatchesAuthoritativeThreeTemplateShape()
    {
        OspfMigrationPlan plan = BuildTommyRecoveryPlan();
        Assert.True(plan.IsValid, string.Join("; ", plan.ValidationErrors));

        Assert.Equal("default-v2", plan.InstanceName);
        Assert.Equal("10.16.0.11", plan.RouterId);
        Assert.Single(plan.ActiveBackhauls);
        Assert.False(plan.ActiveBackhauls[0].Passive);
        Assert.Equal(10, plan.ActiveBackhauls[0].Cost);
        Assert.Equal("ptp", plan.ActiveBackhauls[0].Type);
        Assert.Equal("eth-01_BH01.Tommy-Shylock", plan.ActiveBackhauls[0].Interface);

        Assert.Single(plan.PassiveLoopbacks);
        Assert.True(plan.PassiveLoopbacks[0].Passive);
        Assert.Equal("10.16.0.11/32", plan.PassiveLoopbacks[0].NetworkCidr);
        Assert.Equal("Loopback", plan.PassiveLoopbacks[0].Interface);

        OspfPlanSiteNetworkTemplate site = Assert.Single(plan.PassiveSiteNetworks);
        Assert.Equal("all", site.Interfaces);
        Assert.True(site.Passive);
        Assert.Equal("10.16.11.0/24,100.64.0.0/10,172.16.0.0/12", site.NetworkCidr);

        OspfPlanArea siteArea = Assert.Single(plan.Areas, a => !a.IsBackbone);
        Assert.Equal("stub", siteArea.TargetType);
        Assert.False(siteArea.NoSummaries);
        Assert.Equal("1", siteArea.DefaultCost);

        Assert.Equal(3, plan.AreaRanges.Count);
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "10.16.11.0/24" && r.Comment == "MGMT");
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "100.99.76.16/28");
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "172.26.197.4/30");
    }

    [Fact]
    public async Task Verifier_AcceptsAuthoritativeInventory_RejectsLegacyIfaceBound()
    {
        OspfMigrationPlan plan = BuildTommyRecoveryPlan();
        FakeOps okOps = new() { Inventory = AuthoritativeInventory(plan) };
        OspfVerificationResult ok = await new OspfV7MigrationVerifier(okOps, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.True(ok.Succeeded, ok.Detail);

        OspfV7Inventory dirty = AuthoritativeInventory(plan);
        dirty = new OspfV7Inventory
        {
            Instances = dirty.Instances,
            Areas = dirty.Areas,
            AreaRanges = dirty.AreaRanges,
            Templates =
            [
                ..dirty.Templates,
                new OspfInterfaceTemplateRow
                {
                    Area = "BR01.POP011.016.Tommy-v2",
                    Interfaces = "Bridge-VLAN-99_MGMT",
                    Networks = "10.16.11.0/27",
                    Type = "ptp",
                    Passive = true,
                    Disabled = false,
                },
            ],
        };

        FakeOps dirtyOps = new() { Inventory = dirty };
        OspfVerificationResult bad = await new OspfV7MigrationVerifier(dirtyOps, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Errors,
            e => e.Contains("Unexpected template remains", StringComparison.OrdinalIgnoreCase)
                 || e.Contains("legacy interface-bound", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verifier_FailsMissingNetworkMatcher_AndActiveSite()
    {
        OspfMigrationPlan plan = BuildTommyRecoveryPlan();
        OspfV7Inventory missingNet = AuthoritativeInventory(plan);
        missingNet = WithSiteNetworks(missingNet, "10.16.11.0/24,100.64.0.0/10"); // missing 172.16/12
        OspfVerificationResult missing = await new OspfV7MigrationVerifier(
                new FakeOps { Inventory = missingNet },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.False(missing.Succeeded);

        OspfV7Inventory activeSite = AuthoritativeInventory(plan);
        activeSite = new OspfV7Inventory
        {
            Instances = activeSite.Instances,
            Areas = activeSite.Areas,
            AreaRanges = activeSite.AreaRanges,
            Templates = activeSite.Templates
                .Select(t =>
                    OspfSiteMatcherConsolidation.IsAllInterfaces(t.Interfaces)
                        ? new OspfInterfaceTemplateRow
                        {
                            Area = t.Area,
                            Interfaces = t.Interfaces,
                            Networks = t.Networks,
                            Passive = false,
                            Disabled = false,
                        }
                        : t)
                .ToList(),
        };
        OspfVerificationResult active = await new OspfV7MigrationVerifier(
                new FakeOps { Inventory = activeSite },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.False(active.Succeeded);
    }

    [Fact]
    public async Task RemoveObsolete_RemovesLegacyPassiveIfaceBound_NotBackhaul()
    {
        OspfMigrationPlan basePlan = BuildTommyRecoveryPlan();
        OspfMigrationPlan plan = WithSuperseded(
            basePlan,
            OspfTemplateSemanticIdentity.FromCaptured(
                basePlan.PassiveSiteNetworks[0].AreaName,
                "Bridge-VLAN-99_MGMT",
                "10.16.11.0/27",
                passive: true,
                new OspfCapturedTemplateSemantics { AreaInstanceName = "default-v2" }));
        int removeCount = 0;
        List<Dictionary<string, string>> planned =
        [
            new Dictionary<string, string>
            {
                [".id"] = "*1",
                ["area"] = "backbone-v2",
                ["interfaces"] = "eth-01_BH01.Tommy-Shylock",
                ["networks"] = "10.16.248.76/30",
                ["type"] = "ptp",
                ["cost"] = "10",
                ["disabled"] = "false",
            },
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
                ["area"] = "BR01.POP011.016.Tommy-v2",
                ["interfaces"] = "all",
                ["networks"] = plan.PassiveSiteNetworks[0].NetworkCidr,
                ["passive"] = "",
                ["disabled"] = "false",
            },
        ];
        Dictionary<string, string> obsolete = new()
        {
            [".id"] = "*3",
            ["area"] = "BR01.POP011.016.Tommy-v2",
            ["interfaces"] = "Bridge-VLAN-99_MGMT",
            ["networks"] = "10.16.11.0/27",
            ["passive"] = "",
            ["disabled"] = "false",
            ["cost"] = "1",
            ["priority"] = "128",
            ["use-bfd"] = "false",
        };

        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
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
                            ["name"] = "BR01.POP011.016.Tommy-v2",
                            ["instance"] = "default-v2",
                        }),
                        new TikFakeDoneSentence(),
                    ])
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
                    }));

        await new OspfMikrotikOperations(NullLogger<OspfMikrotikOperations>.Instance)
            .RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(1, removeCount);
        Assert.DoesNotContain(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*1"));
        Assert.Contains(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*3"));
    }

    [Fact]
    public async Task Verifier_FailsWhenDisabledLegacyTemplateRemains()
    {
        OspfMigrationPlan plan = BuildTommyRecoveryPlan();
        OspfV7Inventory inventory = AuthoritativeInventory(plan);
        inventory = new OspfV7Inventory
        {
            Instances = inventory.Instances,
            Areas = inventory.Areas,
            AreaRanges = inventory.AreaRanges,
            Templates =
            [
                ..inventory.Templates,
                new OspfInterfaceTemplateRow
                {
                    Area = "BR01.POP011.016.Tommy-v2",
                    Interfaces = "Bridge-VLAN-99_MGMT",
                    Networks = "10.16.11.0/27",
                    Passive = true,
                    Disabled = true,
                },
            ],
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(
                new FakeOps { Inventory = inventory },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            e => e.Contains("Disabled legacy template remains", StringComparison.OrdinalIgnoreCase)
                 || e.Contains("expected exactly 3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Planner_ClearsNoSummariesFromV6SourceArea()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "6.49.23",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            Instances = [new OspfInstanceRow { Name = "default", RouterId = "10.255.0.1", IsDefault = true }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Type = "default" },
                new OspfAreaRow
                {
                    Name = "site",
                    AreaId = "10.255.0.1",
                    Type = "stub",
                    DefaultCost = "1",
                    NoSummaries = true,
                },
            ],
            AreaRanges = [new OspfAreaRangeRow { Area = "site", Range = "10.10.0.0/16" }],
            Networks =
            [
                new OspfNetworkRow { Area = "backbone", Network = "10.255.0.1/32" },
                new OspfNetworkRow { Area = "backbone", Network = "10.0.0.0/30" },
                new OspfNetworkRow { Area = "site", Network = "10.10.0.0/16" },
                new OspfNetworkRow { Area = "site", Network = "100.99.0.0/16" },
                new OspfNetworkRow { Area = "site", Network = "172.26.0.0/16" },
            ],
            Interfaces =
            [
                new OspfInterfaceRow { Interface = "ether1", NetworkType = "ptp", Cost = "10", Passive = false },
            ],
            IpAddresses =
            [
                new OspfIpAddressRow { Address = "10.255.0.1/32", Network = "10.255.0.1", Interface = "lo" },
                new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
                new OspfIpAddressRow { Address = "10.10.1.1/24", Network = "10.10.1.0", Interface = "bridge-local" },
            ],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        OspfPlanArea site = Assert.Single(result.Plan.Areas, a => a.Name == "site");
        Assert.False(site.NoSummaries);
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("no-summaries", StringComparison.OrdinalIgnoreCase)
                 && n.Contains("clearing", StringComparison.OrdinalIgnoreCase));
        OspfPlanSiteNetworkTemplate matcher = Assert.Single(result.Plan.PassiveSiteNetworks);
        Assert.Equal("all", matcher.Interfaces);
        Assert.Equal("10.10.0.0/16,100.64.0.0/10,172.16.0.0/12", matcher.NetworkCidr);
    }

    private static OspfMigrationPlan WithSuperseded(
        OspfMigrationPlan plan,
        params OspfSupersededTemplateIdentity[] extra) =>
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
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            SupersededTemplates = plan.SupersededTemplates.Concat(extra).ToList(),
            RemoveAllRoutingFilters = plan.RemoveAllRoutingFilters,
            ClearAllRedistribution = plan.ClearAllRedistribution,
            RemoveAllOspfV3 = plan.RemoveAllOspfV3,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = plan.NormalisationNotes,
            ValidationErrors = plan.ValidationErrors,
        };

    private static OspfMigrationPlan BuildTommyRecoveryPlan()
    {
        string path = Path.Combine(Path.GetTempPath(), "tommy-consol-" + Guid.NewGuid().ToString("N") + ".rsc");
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
        try
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
                [
                    new OspfIpAddressRow { Address = "10.16.248.78/30", Network = "10.16.248.76", Interface = "eth-01_BH01.Tommy-Shylock" },
                    new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                    new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "VLAN-99_MGMT_wlan" },
                    new OspfIpAddressRow { Address = "198.18.240.11/24", Network = "198.18.240.0", Interface = "eth-01_BH01.Tommy-Shylock" },
                ],
                path);
            Assert.True(built.Succeeded, string.Join("; ", built.Plan.ValidationErrors));
            return built.Plan;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static OspfV7Inventory AuthoritativeInventory(OspfMigrationPlan plan)
    {
        OspfV7Inventory baseInv = OspfTestBaselines.InventoryMatchingPlan(plan);
        string backbone = string.IsNullOrWhiteSpace(plan.TargetBackboneAreaName)
            ? OspfFleetNaming.Ros7BackboneAreaName
            : plan.TargetBackboneAreaName;
        string site = string.IsNullOrWhiteSpace(plan.TargetSiteAreaName)
            ? OspfFleetNaming.Ros7SiteAreaName(plan.Identity)
            : plan.TargetSiteAreaName;

        return new OspfV7Inventory
        {
            Instances = baseInv.Instances,
            Areas = baseInv.Areas,
            AreaRanges = plan.AreaRanges.Select(r => new OspfAreaRangeRow
            {
                Area = plan.Areas.FirstOrDefault(a => a.Name == r.AreaName)?.IsBackbone == true
                    ? backbone
                    : site,
                Range = r.RangeCidr,
                Comment = r.Comment,
                Advertise = true,
            }).ToList(),
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = backbone,
                    Interfaces = plan.ActiveBackhauls[0].Interface,
                    Networks = plan.ActiveBackhauls[0].NetworkCidr,
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = backbone,
                    Interfaces = plan.PassiveLoopbacks[0].Interface,
                    Networks = plan.PassiveLoopbacks[0].NetworkCidr,
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = site,
                    Interfaces = plan.PassiveSiteNetworks[0].Interfaces,
                    Networks = plan.PassiveSiteNetworks[0].NetworkCidr,
                    Passive = true,
                },
            ],
        };
    }

    private static OspfV7Inventory WithSiteNetworks(OspfV7Inventory source, string networks) =>
        new()
        {
            Instances = source.Instances,
            Areas = source.Areas,
            AreaRanges = source.AreaRanges,
            Templates = source.Templates
                .Select(t =>
                    OspfSiteMatcherConsolidation.IsAllInterfaces(t.Interfaces)
                        ? new OspfInterfaceTemplateRow
                        {
                            Area = t.Area,
                            Interfaces = t.Interfaces,
                            Networks = networks,
                            Passive = t.Passive,
                            Disabled = t.Disabled,
                        }
                        : t)
                .ToList(),
        };

    private sealed class FakeOps : IOspfMikrotikOperations
    {
        public required OspfV7Inventory Inventory { get; init; }

        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;


        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OspfIpAddressRow>>(
            [
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "VLAN-99_MGMT_wlan" },
            ]);
    }

    private sealed class ParameterCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        private readonly List<ITikCommand> _removes = [];

        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Removes =>
            _removes
                .Select(static c => (IReadOnlyList<(string Name, string? Value)>)c.Parameters
                    .Select(static p => (p.Name, p.Value))
                    .ToList())
                .ToList();

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
            if (commandText.Equals("/routing/ospf/interface-template/remove", StringComparison.Ordinal))
            {
                _removes.Add(cmd);
            }
        }
    }
}
