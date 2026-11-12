// -----------------------------------------------------------------------------
// File: OspfBackhaulPostVerifyGuardTests.cs
//
// Description:
// Post-verify backbone guard must find the intended live backhaul even when
// the plan still carries the logical ROS6 backbone area name, without
// requiring a stable RouterOS .id.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Post-verify backbone guard must find the intended live backhaul even when the plan still
/// carries the logical ROS6 backbone area name, without requiring a stable RouterOS <c>.id</c>.
/// </summary>
public sealed class OspfBackhaulPostVerifyGuardTests
{
    /// <summary>
    /// Scenario: ApplyAndVerifyAsync runs a plan whose backhaul area is the logical name backbone, and whose target
    /// backbone area is the RouterOS 7 backbone name, against spy operations whose live RouterOS has only the
    /// remapped area and never area=backbone.
    /// Expected: The apply succeeds. Intermediate asserts see the remapped RouterOS 7 backbone area, while the final
    /// post-verify guard still receives the unmapped logical backbone plan and its real assert completes.
    /// Invariant: The final post-verify guard accepts a live backbone-v2 area when the plan uses the logical backbone
    /// name, instead of failing closed.
    /// </summary>
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

    /// <summary>
    /// Scenario: the plan uses the logical v6 backbone area name, while the live production template row sits in 'backbone-v2' (eth-01_BH01.Tommy-Shylock, 10.16.248.76/30, ptp, cost 10).
    /// Expected: AssertBackboneStillActiveAsync completes without throwing.
    /// Invariant: the guard maps the logical backbone area to its live -v2 name and accepts the exact production export row.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live backhaul row reports its subnet in a 'network' property instead of 'networks'.
    /// Expected: the guard accepts the row without throwing.
    /// Invariant: both RouterOS spellings of the matcher property are understood.
    /// </summary>
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

    /// <summary>
    /// Scenario: the only live template is an unrelated ether99 ptp row, so the planned backhaul is absent.
    /// Expected: OspfBackboneMutationGuardException saying the backhaul is 'missing after mutation' and naming eth-01_BH01.Tommy-Shylock.
    /// Invariant: a backhaul that has vanished is detected and identified in the error.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live backhaul row now lists interface eth-99_WRONG.
    /// Expected: OspfBackboneMutationGuardException with 'missing after mutation'.
    /// Invariant: a template on a different interface does not count as the planned backhaul.
    /// </summary>
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

    /// <summary>
    /// Scenario: the only live template in backbone-v2 is a passive 'all' template for 10.16.11.0/24, which is not the designated backhaul identity.
    /// Expected: OspfBackboneMutationGuardException with 'missing after mutation'.
    /// Invariant: sharing the backbone area is not enough; the designated backhaul identity must match.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live backhaul row has gained the passive flag.
    /// Expected: OspfBackboneMutationGuardException whose message mentions 'passive'.
    /// Invariant: a passive backhaul no longer forms adjacencies and is treated as broken.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live backhaul row is disabled=true.
    /// Expected: OspfBackboneMutationGuardException whose message mentions 'disabled'.
    /// Invariant: a disabled backhaul template is reported rather than accepted.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live backhaul row uses 10.16.248.80/30 instead of the planned 10.16.248.76/30.
    /// Expected: OspfBackboneMutationGuardException with 'missing after mutation'.
    /// Invariant: a backhaul on the wrong subnet does not satisfy the guard.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live backhaul row has moved into the site-v2 area.
    /// Expected: OspfBackboneMutationGuardException with 'missing after mutation'.
    /// Invariant: the backhaul must remain in the backbone area.
    /// </summary>
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

    /// <summary>
    /// Creates the real <c>OspfMikrotikOperations</c> with a null logger.
    /// </summary>
    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    /// <summary>
    /// Builds a minimal scheduled job for the Tommy router (BR01.POP011.016.Tommy) used as the context for apply-and-verify runs.
    /// </summary>
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

    /// <summary>
    /// Builds a fake RouterOS connection whose interface-template print returns only the remapped <c>backbone-v2</c> backhaul, optionally alongside an unrelated site template.
    /// <param name="includeUnrelatedTemplate">When <see langword="true"/>, also returns a non-backhaul template that must not satisfy the guard.</param>
    /// </summary>
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

    /// <summary>
    /// Builds a v7 inventory using the live default instance, backbone and site area names, with a management area
    /// range and templates matching the logical plan.
    /// </summary>
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

    /// <summary>
    /// Builds the IP address inventory (backhaul, loopback and management VLAN) matching the Tommy integration plan.
    /// </summary>
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

    /// <summary>
    /// Builds the raw RouterOS row for the production backhaul template on <c>backbone-v2</c> (eth-01_BH01.Tommy-Shylock, 10.16.248.76/30), which tests mutate to simulate faults.
    /// </summary>
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

    /// <summary>
    /// Builds a management-plane baseline that only tracks the backbone template, with no neighbour or management route legs, so the guard focuses on template matching.
    /// </summary>
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

    /// <summary>
    /// Plan store fake that discards saves, reports no stored plan, and ignores deletes and orphan cleanup.
    /// </summary>
    private sealed class NoopPlanStore : IOspfMigrationPlanStore
    {
        /// <summary>
        /// Discards the plan; persistence is irrelevant to these tests.
        /// </summary>
        public Task SaveAsync(OspfMigrationPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Always reports that no plan is stored.
        /// </summary>
        public Task<OspfMigrationPlan?> TryLoadAsync(
            MikrotikScheduledJob job,
            string identity,
            CancellationToken cancellationToken) =>
            Task.FromResult<OspfMigrationPlan?>(null);

        /// <summary>
        /// No-op; there is nothing persisted to delete.
        /// </summary>
        public void Delete(MikrotikScheduledJob job, IPAddress? routerIp = null)
        {
        }

        /// <summary>
        /// No-op; there are no orphaned plan files to clean up.
        /// </summary>
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
        /// <summary>
        /// In-memory v7 inventory returned by <see cref="ReadV7InventoryAsync"/>, already remapped to live target names.
        /// </summary>
        public required OspfV7Inventory Inventory { get; init; }
        /// <summary>
        /// IP address inventory returned by <see cref="ReadIpAddressInventoryAsync"/>.
        /// </summary>
        public required IReadOnlyList<OspfIpAddressRow> Addresses { get; init; }
        /// <summary>
        /// Backhaul area names observed on every <see cref="AssertBackboneStillActiveAsync"/> call, in order.
        /// </summary>
        public List<string> BackhaulAreasSeenByAssert { get; } = [];
        /// <summary>
        /// Whether the final post-apply assert received the unmapped logical <c>backbone</c> plan.
        /// </summary>
        public bool FinalAssertUsedLogicalBackbone { get; private set; }
        /// <summary>
        /// Whether the real backbone guard ran to completion on the final assert without throwing.
        /// </summary>
        public bool FinalRealAssertCompleted { get; private set; }
        /// <summary>
        /// Set once the obsolete-template/v3 removal steps ran, marking the following assert as the final post-verify guard.
        /// </summary>
        private bool _applyCompleted;

        /// <summary>
        /// Returns the preconfigured in-memory inventory instead of reading from the connection.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        /// <summary>
        /// Returns the preconfigured IP address rows instead of reading from the connection.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Addresses);

        /// <summary>
        /// Returns a template-only baseline for the given management host, skipping neighbour and route legs.
        /// </summary>
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

        /// <summary>
        /// Records the backhaul areas seen; after apply completes and the plan still uses the logical <c>backbone</c> area, delegates to the real guard, otherwise accepts without checking.
        /// </summary>
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

        /// <summary>
        /// No-op; routing filter removal is not under test.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op; redistribution clearing is not under test.
        /// </summary>
        public Task ClearOspfRedistributionAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; instance filter reference clearing is not under test.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; router-id changes are not under test.
        /// </summary>
        public Task SetInstanceRouterIdAsync(
            ITikConnection connection,
            string instanceName,
            string routerId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; area creation is not under test.
        /// </summary>
        public Task EnsureOspfAreaAsync(
            ITikConnection connection,
            string instanceName,
            OspfPlanArea area,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; area range creation is not under test.
        /// </summary>
        public Task EnsureOspfAreaRangeAsync(
            ITikConnection connection,
            OspfPlanAreaRange range,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; backhaul template creation is not under test.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(
            ITikConnection connection,
            OspfPlanBackhaulTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; loopback template creation is not under test.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(
            ITikConnection connection,
            OspfPlanLoopbackTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; site network template creation is not under test.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; site network template state assertion is not under test.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Marks apply as completed so the next backbone assert is treated as the final post-verify guard.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default)
        {
            _applyCompleted = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Marks apply as completed so the next backbone assert is treated as the final post-verify guard.
        /// </summary>
        public Task RemoveOspfV3Async(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default)
        {
            _applyCompleted = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Reports zero remaining routing filters.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// Reports no OSPF neighbours.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        /// <summary>
        /// Not supported; capture is not part of the apply-and-verify path.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(
            ITikConnection connection,
            OspfCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
