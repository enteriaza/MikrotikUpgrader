using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Post-verify backbone guard must find the intended live backhaul even when the plan still
/// carries the logical ROS6 backbone area name, without requiring a stable RouterOS <c>.id</c>.
/// </summary>
public sealed class OspfBackhaulPostVerifyGuardTests
{
    [Fact]
    public async Task ApplyAndVerifyAsync_LogicalBackbonePlan_PostVerifyGuardAcceptsLiveBackboneV2()
    {
        OspfMigrationPlan plan = IntegrationLogicalRos6Plan();
        Assert.Equal("backbone", plan.ActiveBackhauls.Single().AreaName);
        Assert.Equal(OspfFleetNaming.Ros7BackboneAreaName, plan.TargetBackboneAreaName);

        // Live RouterOS only has the remapped area — never area=backbone.
        // Without RemapBackhaulAreaToLiveTarget the final MigrationService assert fails closed.
        TikFakeConnection fake = LiveBackboneV2OnlyConnection(includeUnrelatedTemplate: true);
        OspfMikrotikOperations realOps = CreateOps();
        ApplyVerifySpyOps spy = new(realOps)
        {
            Inventory = LiveRemappedInventory(plan),
            Addresses = IntegrationAddresses(),
        };

        OspfMigrationService service = new(
            spy,
            new NoopPlanStore(),
            new OspfV7Reconciler(spy, NullLogger<OspfV7Reconciler>.Instance),
            new OspfV7MigrationVerifier(spy, NullLogger<OspfV7MigrationVerifier>.Instance),
            NullLogger<OspfMigrationService>.Instance);

        OspfApplyGateResult result = await service.ApplyAndVerifyAsync(
            fake,
            CreateJob(),
            plan.Identity,
            plan,
            CancellationToken.None,
            managementHost: "198.18.240.11");

        Assert.True(result.Succeeded, result.Detail);
        // Production Apply remaps before intermediate asserts.
        Assert.Contains(spy.BackhaulAreasSeenByAssert, a => a == OspfFleetNaming.Ros7BackboneAreaName);
        // Production ApplyAndVerifyAsync still passes the unmapped logical plan to the final guard.
        Assert.Contains(spy.BackhaulAreasSeenByAssert, a => a == "backbone");
        Assert.True(
            spy.FinalAssertUsedLogicalBackbone,
            "Final post-verify assert must exercise the unmapped logical backbone area path.");
        Assert.True(spy.FinalRealAssertCompleted);
    }

    [Fact]
    public async Task AssertBackbone_LogicalBackboneArea_FindsLiveBackboneV2_ExactProductionExport()
    {
        Dictionary<string, string> exportRow = new()
        {
            [".id"] = "*9",
            ["area"] = "backbone-v2",
            ["auth-id"] = "1",
            ["cost"] = "10",
            ["interfaces"] = "eth-01_BH01.Tommy-Shylock",
            ["networks"] = "10.16.248.76/30",
            ["priority"] = "1",
            ["type"] = "ptp",
            ["use-bfd"] = "no",
        };

        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => [new TikFakeReSentence(exportRow), new TikFakeDoneSentence()]);

        await CreateOps().AssertBackboneStillActiveAsync(
            fake,
            LogicalRos6TommyPlan(),
            TemplateOnlyBaseline(),
            CancellationToken.None);
    }

    [Fact]
    public async Task AssertBackbone_AcceptsNetworkAliasProperty()
    {
        Dictionary<string, string> row = ProductionBackhaulRow();
        row.Remove("networks");
        row["network"] = "10.16.248.76/30";

        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => [new TikFakeReSentence(row), new TikFakeDoneSentence()]);

        await CreateOps().AssertBackboneStillActiveAsync(
            fake,
            LogicalRos6TommyPlan(),
            TemplateOnlyBaseline(),
            CancellationToken.None);
    }

    [Fact]
    public async Task AssertBackbone_MissingBackhaul_FailsClosed()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ =>
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*2",
                        ["area"] = "backbone-v2",
                        ["interfaces"] = "ether99",
                        ["networks"] = "10.255.255.0/30",
                        ["type"] = "ptp",
                        ["cost"] = "10",
                        ["disabled"] = "false",
                    }),
                    new TikFakeDoneSentence(),
                ]);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                fake,
                LogicalRos6TommyPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("eth-01_BH01.Tommy-Shylock", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssertBackbone_WrongInterface_FailsClosed()
    {
        Dictionary<string, string> row = ProductionBackhaulRow();
        row["interfaces"] = "eth-99_WRONG";

        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => [new TikFakeReSentence(row), new TikFakeDoneSentence()]);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                fake,
                LogicalRos6TommyPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssertBackbone_UnrelatedTemplateCannotSatisfyGuard()
    {
        // Same area as the remapped backbone, but not the designated backhaul identity.
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ =>
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*U",
                        ["area"] = "backbone-v2",
                        ["interfaces"] = "all",
                        ["networks"] = "10.16.11.0/24",
                        ["passive"] = "",
                        ["disabled"] = "false",
                    }),
                    new TikFakeDoneSentence(),
                ]);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                fake,
                LogicalRos6TommyPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssertBackbone_PassiveBackhaul_FailsClosed()
    {
        Dictionary<string, string> row = ProductionBackhaulRow();
        row["passive"] = "";

        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => [new TikFakeReSentence(row), new TikFakeDoneSentence()]);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                fake,
                LogicalRos6TommyPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None));

        Assert.Contains("passive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssertBackbone_DisabledBackhaul_FailsClosed()
    {
        Dictionary<string, string> row = ProductionBackhaulRow();
        row["disabled"] = "true";

        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => [new TikFakeReSentence(row), new TikFakeDoneSentence()]);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                fake,
                LogicalRos6TommyPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssertBackbone_WrongNetwork_FailsClosed()
    {
        Dictionary<string, string> row = ProductionBackhaulRow();
        row["networks"] = "10.16.248.80/30";

        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => [new TikFakeReSentence(row), new TikFakeDoneSentence()]);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                fake,
                LogicalRos6TommyPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssertBackbone_WrongLiveArea_FailsClosed()
    {
        Dictionary<string, string> row = ProductionBackhaulRow();
        row["area"] = "site-v2";

        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => [new TikFakeReSentence(row), new TikFakeDoneSentence()]);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                fake,
                LogicalRos6TommyPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    private static MikrotikScheduledJob CreateJob() => new()
    {
        ImportId = Guid.NewGuid(),
        SourceFileName = "backhaul-guard.csv",
        RowNumber = 1,
        RouterName = "BR01.POP011.016.Tommy",
        RouterIp = IPAddress.Parse("198.18.240.11"),
        DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
        DateStopUtc = DateTime.UtcNow.AddHours(1),
    };

    private static TikFakeConnection LiveBackboneV2OnlyConnection(bool includeUnrelatedTemplate)
    {
        List<Dictionary<string, string>> templates =
        [
            new()
            {
                [".id"] = "*9",
                ["area"] = "backbone-v2",
                ["auth-id"] = "1",
                ["cost"] = "10",
                ["interfaces"] = "eth-01_BH01.Tommy-Shylock",
                ["networks"] = "10.16.248.76/30",
                ["priority"] = "1",
                ["type"] = "ptp",
                ["use-bfd"] = "no",
                ["disabled"] = "false",
            },
        ];
        if (includeUnrelatedTemplate)
        {
            templates.Add(new Dictionary<string, string>
            {
                [".id"] = "*U",
                ["area"] = "BR01.POP011.016.Tommy-v2",
                ["interfaces"] = "all",
                ["networks"] = "10.16.11.0/24",
                ["passive"] = "",
                ["disabled"] = "false",
            });
        }

        return new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ =>
                [
                    ..templates.Select(static t => new TikFakeReSentence(new Dictionary<string, string>(t))),
                    new TikFakeDoneSentence(),
                ]);
    }

    private static OspfV7Inventory LiveRemappedInventory(OspfMigrationPlan logicalPlan)
    {
        string site = logicalPlan.TargetSiteAreaName;
        return new OspfV7Inventory
        {
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*i",
                    Name = OspfFleetNaming.Ros7InstanceName,
                    RouterId = logicalPlan.RouterId,
                    Version = "2",
                },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*ab",
                    Name = OspfFleetNaming.Ros7BackboneAreaName,
                    AreaId = "0.0.0.0",
                    Instance = OspfFleetNaming.Ros7InstanceName,
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Id = "*as",
                    Name = site,
                    AreaId = logicalPlan.RouterId,
                    Instance = OspfFleetNaming.Ros7InstanceName,
                    Type = "stub",
                    DefaultCost = "1",
                    NoSummaries = false,
                },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow
                {
                    Id = "*r",
                    Area = site,
                    Range = "10.16.11.0/24",
                    Comment = "MGMT",
                    Advertise = true,
                },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*9",
                    Area = OspfFleetNaming.Ros7BackboneAreaName,
                    Interfaces = "eth-01_BH01.Tommy-Shylock",
                    Networks = "10.16.248.76/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                    Disabled = false,
                    Priority = "1",
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*L",
                    Area = OspfFleetNaming.Ros7BackboneAreaName,
                    Interfaces = "Loopback",
                    Networks = "10.16.0.11/32",
                    Passive = true,
                    Disabled = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*S",
                    Area = site,
                    Interfaces = "all",
                    Networks = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
                    Passive = true,
                    Disabled = false,
                },
            ],
        };
    }

    private static IReadOnlyList<OspfIpAddressRow> IntegrationAddresses() =>
    [
        new() { Address = "10.16.248.78/30", Network = "10.16.248.76", Interface = "eth-01_BH01.Tommy-Shylock" },
        new() { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
        new() { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "VLAN-99_MGMT_wlan" },
    ];

    /// <summary>
    /// Full ROS6-capture-shaped plan for the production ApplyAndVerify path.
    /// </summary>
    private static OspfMigrationPlan IntegrationLogicalRos6Plan() => new()
    {
        ImportId = Guid.NewGuid(),
        JobId = "job",
        RouterIp = "198.18.240.11",
        Identity = "BR01.POP011.016.Tommy",
        SourceRouterOsVersion = "6.49.18",
        Architecture = "mipsbe",
        SourceKind = OspfCaptureSourceKind.RouterOsV6,
        InstanceName = "default",
        RouterId = "10.16.0.11",
        TargetInstanceName = OspfFleetNaming.Ros7InstanceName,
        TargetBackboneAreaName = OspfFleetNaming.Ros7BackboneAreaName,
        TargetSiteAreaName = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
        LoopbackInterfaceName = "Loopback",
        LoopbackCidr = "10.16.0.11/32",
        DerivedManagementCidr = "10.16.11.0/24",
        Areas =
        [
            new OspfPlanArea
            {
                Name = "backbone",
                AreaId = "0.0.0.0",
                TargetType = "default",
                IsBackbone = true,
            },
            new OspfPlanArea
            {
                Name = "BR01.POP011.016.Tommy",
                AreaId = "10.16.0.11",
                TargetType = "stub",
                IsBackbone = false,
                DefaultCost = "1",
            },
        ],
        AreaRanges =
        [
            new OspfPlanAreaRange
            {
                AreaName = "BR01.POP011.016.Tommy",
                RangeCidr = "10.16.11.0/24",
                Comment = "MGMT",
                Advertise = true,
            },
        ],
        ActiveBackhauls =
        [
            new OspfPlanBackhaulTemplate
            {
                AreaName = "backbone",
                Interface = "eth-01_BH01.Tommy-Shylock",
                NetworkCidr = "10.16.248.76/30",
                Cost = 10,
                Type = "ptp",
                Passive = false,
            },
        ],
        PassiveLoopbacks =
        [
            new OspfPlanLoopbackTemplate
            {
                AreaName = "backbone",
                Interface = "Loopback",
                NetworkCidr = "10.16.0.11/32",
                Passive = true,
            },
        ],
        PassiveSiteNetworks =
        [
            new OspfPlanSiteNetworkTemplate
            {
                AreaName = "BR01.POP011.016.Tommy",
                Interfaces = "all",
                NetworkCidr = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
                Passive = true,
            },
        ],
        ValidationErrors = [],
        NormalisationNotes = [],
        ContentHash = "test",
    };

    /// <summary>
    /// ROS6-capture-shaped plan: ActiveBackhauls still say <c>backbone</c>, target is <c>backbone-v2</c>.
    /// </summary>
    private static OspfMigrationPlan LogicalRos6TommyPlan() => new()
    {
        ImportId = Guid.NewGuid(),
        JobId = "job",
        RouterIp = "198.18.240.11",
        Identity = "BR01.POP011.016.Tommy",
        SourceRouterOsVersion = "6.49.18",
        Architecture = "mipsbe",
        SourceKind = OspfCaptureSourceKind.RouterOsV6,
        InstanceName = "default",
        RouterId = "10.16.0.11",
        TargetInstanceName = OspfFleetNaming.Ros7InstanceName,
        TargetBackboneAreaName = OspfFleetNaming.Ros7BackboneAreaName,
        TargetSiteAreaName = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
        LoopbackInterfaceName = "Loopback",
        LoopbackCidr = "10.16.0.11/32",
        DerivedManagementCidr = "10.16.11.0/24",
        Areas =
        [
            new OspfPlanArea
            {
                Name = "backbone",
                AreaId = "0.0.0.0",
                TargetType = "default",
                IsBackbone = true,
            },
            new OspfPlanArea
            {
                Name = "BR01.POP011.016.Tommy",
                AreaId = "10.16.0.11",
                TargetType = "stub",
                IsBackbone = false,
                DefaultCost = "1",
            },
        ],
        ActiveBackhauls =
        [
            new OspfPlanBackhaulTemplate
            {
                AreaName = "backbone",
                Interface = "eth-01_BH01.Tommy-Shylock",
                NetworkCidr = "10.16.248.76/30",
                Cost = 10,
                Type = "ptp",
                Passive = false,
            },
        ],
        PassiveLoopbacks = [],
        PassiveSiteNetworks = [],
        AreaRanges = [],
        ValidationErrors = [],
        NormalisationNotes = [],
        ContentHash = "test",
    };

    private static Dictionary<string, string> ProductionBackhaulRow() => new()
    {
        [".id"] = "*1",
        ["area"] = "backbone-v2",
        ["auth-id"] = "1",
        ["cost"] = "10",
        ["interfaces"] = "eth-01_BH01.Tommy-Shylock",
        ["networks"] = "10.16.248.76/30",
        ["priority"] = "1",
        ["type"] = "ptp",
        ["use-bfd"] = "no",
        ["disabled"] = "false",
    };

    private static OspfManagementPlaneBaseline TemplateOnlyBaseline() => new()
    {
        ManagementHost = "198.18.240.11",
        BackboneInterface = "eth-01_BH01.Tommy-Shylock",
        BackboneTemplateId = "*1",
        BackboneArea = "backbone-v2",
        BackboneNetwork = "10.16.248.76/30",
        BackboneWasActive = true,
        BackboneTemplatePassive = false,
        BackboneTemplateDisabled = false,
        NeighbourAddress = null,
        NeighbourRouterId = null,
        NeighbourState = null,
        NeighbourWasFull = false,
        ManagementRouteDst = null,
        ManagementRouteGateway = null,
        ManagementRouteImmediateGw = null,
        ManagementOutgoingInterface = null,
        ManagementRouteActive = false,
        ManagementRouteObserved = false,
        ManagementRouteDependsOnBackbone = false,
    };

    private sealed class NoopPlanStore : IOspfMigrationPlanStore
    {
        public Task SaveAsync(OspfMigrationPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OspfMigrationPlan?> TryLoadAsync(
            MikrotikScheduledJob job,
            string identity,
            CancellationToken cancellationToken) =>
            Task.FromResult<OspfMigrationPlan?>(null);

        public void Delete(MikrotikScheduledJob job, IPAddress? routerIp = null)
        {
        }

        public void CleanupOrphansOnStartup()
        {
        }
    }

    /// <summary>
    /// Production reconciler/verifier path with in-memory inventory, while the final
    /// <see cref="IOspfMikrotikOperations.AssertBackboneStillActiveAsync"/> delegates to the real
    /// production guard against the fake RouterOS connection.
    /// </summary>
    private sealed class ApplyVerifySpyOps(OspfMikrotikOperations real) : IOspfMikrotikOperations
    {
        public required OspfV7Inventory Inventory { get; init; }
        public required IReadOnlyList<OspfIpAddressRow> Addresses { get; init; }
        public List<string> BackhaulAreasSeenByAssert { get; } = [];
        public bool FinalAssertUsedLogicalBackbone { get; private set; }
        public bool FinalRealAssertCompleted { get; private set; }
        private bool _applyCompleted;

        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Addresses);

        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            string managementHost,
            CancellationToken cancellationToken = default)
        {
            // Skip neighbour/route legs so the final assert focuses on template remapping.
            OspfManagementPlaneBaseline baseline = TemplateOnlyBaseline();
            return Task.FromResult(new OspfManagementPlaneBaseline
            {
                ManagementHost = managementHost,
                BackboneInterface = baseline.BackboneInterface,
                BackboneTemplateId = baseline.BackboneTemplateId,
                BackboneArea = baseline.BackboneArea,
                BackboneNetwork = baseline.BackboneNetwork,
                BackboneWasActive = baseline.BackboneWasActive,
                BackboneTemplatePassive = baseline.BackboneTemplatePassive,
                BackboneTemplateDisabled = baseline.BackboneTemplateDisabled,
                NeighbourAddress = baseline.NeighbourAddress,
                NeighbourRouterId = baseline.NeighbourRouterId,
                NeighbourState = baseline.NeighbourState,
                NeighbourWasFull = false,
                ManagementRouteDst = baseline.ManagementRouteDst,
                ManagementRouteGateway = baseline.ManagementRouteGateway,
                ManagementRouteImmediateGw = baseline.ManagementRouteImmediateGw,
                ManagementOutgoingInterface = baseline.ManagementOutgoingInterface,
                ManagementRouteActive = false,
                ManagementRouteObserved = false,
                ManagementRouteDependsOnBackbone = false,
            });
        }

        public async Task AssertBackboneStillActiveAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            OspfManagementPlaneBaseline baseline,
            CancellationToken cancellationToken = default)
        {
            foreach (OspfPlanBackhaulTemplate bh in plan.ActiveBackhauls)
            {
                BackhaulAreasSeenByAssert.Add(bh.AreaName);
            }

            // After Apply returns, MigrationService calls assert with the logical plan.
            if (_applyCompleted
                && plan.ActiveBackhauls.Any(b =>
                    string.Equals(b.AreaName, "backbone", StringComparison.OrdinalIgnoreCase)))
            {
                FinalAssertUsedLogicalBackbone = true;
                await real.AssertBackboneStillActiveAsync(connection, plan, baseline, cancellationToken)
                    .ConfigureAwait(false);
                FinalRealAssertCompleted = true;
                return;
            }

            // Intermediate Apply asserts already remapped — accept without re-hitting the fake print.
        }

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearOspfRedistributionAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearOspfInstanceFilterReferencesAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetInstanceRouterIdAsync(
            ITikConnection connection,
            string instanceName,
            string routerId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureOspfAreaAsync(
            ITikConnection connection,
            string instanceName,
            OspfPlanArea area,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureOspfAreaRangeAsync(
            ITikConnection connection,
            OspfPlanAreaRange range,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureBackhaulTemplateAsync(
            ITikConnection connection,
            OspfPlanBackhaulTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureLoopbackTemplateAsync(
            ITikConnection connection,
            OspfPlanLoopbackTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureSiteNetworkTemplateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AssertSiteNetworkTemplateStateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveObsoleteTemplatesAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default)
        {
            _applyCompleted = true;
            return Task.CompletedTask;
        }

        public Task RemoveOspfV3Async(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default)
        {
            _applyCompleted = true;
            return Task.CompletedTask;
        }

        public Task<int> CountRoutingFiltersAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        public Task<OspfCaptureSnapshot> CaptureAsync(
            ITikConnection connection,
            OspfCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
