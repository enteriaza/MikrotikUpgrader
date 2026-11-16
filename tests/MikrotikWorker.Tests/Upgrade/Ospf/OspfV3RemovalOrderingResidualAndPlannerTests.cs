// -----------------------------------------------------------------------------
// File: OspfV3RemovalOrderingResidualAndPlannerTests.cs
//
// Description:
// Production-path regressions for mandatory OSPFv3 removal ordering, residual
// verification, planner OSPFv2 target selection, instance-name verify, and
// conservative /routing/id cleanup.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Tests.Infrastructure.Fakes;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Ordering, residual, planner and verification regressions for the OSPF remediation.
/// </summary>
public sealed class OspfV3RemovalOrderingResidualAndPlannerTests
{
    [Fact]
    public void ResidualDetector_FlagsOrphanedAreaTemplateAndRange()
    {
        OspfV7Inventory inventory = new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances =
            [
                new OspfInstanceRow { Id = "*v2", Name = "default-v2", Version = "2", RouterId = "10.16.0.11" },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*a2",
                    Name = "backbone-v2",
                    AreaId = "0.0.0.0",
                    Instance = "default-v2",
                },
                new OspfAreaRow
                {
                    Id = "*aOrphan",
                    Name = "backbone-v3",
                    AreaId = "0.0.0.0",
                    Instance = "default-v3",
                },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*t2",
                    Area = "backbone-v2",
                    Interfaces = "ether1",
                    Networks = "10.16.248.0/30",
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*tOrphan",
                    Area = "backbone-v3",
                    Networks = "2001:db8::/64",
                },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow { Id = "*rOrphan", Area = "backbone-v3", Range = "2001:db8::/32" },
            ],
        };

        OspfV3ResidualDetector.Result result = OspfV3ResidualDetector.Evaluate(inventory);
        Assert.False(result.IsClean);
        Assert.Contains(result.Residuals, r => r.Kind == "area" && r.Id == "*aOrphan");
        Assert.Contains(result.Residuals, r => r.Kind == "template" && r.Id == "*tOrphan");
        Assert.Contains(result.Residuals, r => r.Kind == "range" && r.Id == "*rOrphan");
        Assert.DoesNotContain(result.Residuals, r => r.Id == "*t2");
    }

    [Fact]
    public void ResidualDetector_FlagsRemainingV3Instance()
    {
        OspfV7Inventory inventory = new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances =
            [
                new OspfInstanceRow { Id = "*v2", Name = "default-v2", Version = "2" },
                new OspfInstanceRow { Id = "*v3", Name = "default-v3", Version = "3" },
            ],
            Areas =
            [
                new OspfAreaRow { Id = "*a2", Name = "backbone-v2", AreaId = "0.0.0.0", Instance = "default-v2" },
            ],
        };

        OspfV3ResidualDetector.Result result = OspfV3ResidualDetector.Evaluate(inventory);
        Assert.False(result.IsClean);
        Assert.Contains(result.Residuals, r => r.Kind == "instance" && r.Id == "*v3");
    }

    [Fact]
    public void ResidualDetector_CleanDualProtocolFreeInventory()
    {
        OspfV7Inventory inventory = new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances =
            [
                new OspfInstanceRow { Id = "*v2", Name = "default-v2", Version = "2", RouterId = "10.16.0.11" },
            ],
            Areas =
            [
                new OspfAreaRow { Id = "*a2", Name = "backbone-v2", AreaId = "0.0.0.0", Instance = "default-v2" },
                new OspfAreaRow
                {
                    Id = "*s2",
                    Name = "BR01.POP011.016.Tommy-v2",
                    AreaId = "10.16.0.11",
                    Instance = "default-v2",
                },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*t2",
                    Area = "backbone-v2",
                    Interfaces = "ether1",
                    Networks = "10.16.248.0/30",
                },
            ],
        };

        Assert.True(OspfV3ResidualDetector.Evaluate(inventory).IsClean);
    }

    [Fact]
    public void Planner_SoleOspfV3_IsNotOspfV2Target_AndIsQueuedForRemoval()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.Empty,
            JobId = "job",
            RouterIp = IPAddress.Parse("10.16.0.11"),
            Identity = "BR01.POP011.016.Tommy",
            RouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances =
            [
                new OspfInstanceRow { Name = "default-v3", RouterId = "10.16.0.11", Version = "3" },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Name = "backbone-v3",
                    AreaId = "0.0.0.0",
                    Instance = "default-v3",
                    Type = "default",
                },
            ],
            OspfV3Objects =
            [
                new OspfV3ObjectRef
                {
                    Path = "/routing/ospf/instance",
                    Id = "*v3",
                    Name = "default-v3",
                    Kind = "instance-v3",
                },
            ],
            IpAddresses = OspfTestBaselines.LoopbackSlash32("10.16.0.11", "Loopback"),
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.False(result.Plan.IsValid);
        Assert.Contains(
            result.Plan.ValidationErrors,
            e => e.Contains("OSPFv3", StringComparison.OrdinalIgnoreCase)
                 && e.Contains("OSPFv2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Plan.OspfV3ToRemove, o => o.Name == "default-v3");
    }

    [Fact]
    public void Planner_DualProtocol_SelectsOspfV2_AndQueuesOspfV3()
    {
        OspfCaptureSnapshot snapshot = BaselineDualProtocol(
            v2Name: "default",
            v3Name: "default-v3",
            v3AreaName: "backbone-v3");

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Plan.IsValid, string.Join("; ", result.Plan.ValidationErrors));
        Assert.Equal("default-v2", result.Plan.TargetInstanceName);
        Assert.Contains(result.Plan.OspfV3ToRemove, o => o.Name == "default-v3");
        Assert.DoesNotContain(result.Plan.OspfV3ToRemove, o => o.Name == "default");
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("default", StringComparison.Ordinal)
                 && n.Contains("default-v2", StringComparison.Ordinal));
    }

    [Fact]
    public void Planner_AmbiguousMultipleOspfV2_FailsValidation()
    {
        OspfCaptureSnapshot baseline = BaselineDualProtocol(
            v2Name: "legacy-a",
            v3Name: "default-v3",
            v3AreaName: "backbone-v3");
        // Two OSPFv2 instances with no unique backbone owner (backbone instance blank) → fail closed.
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = baseline.ImportId,
            JobId = baseline.JobId,
            RouterIp = baseline.RouterIp,
            Identity = baseline.Identity,
            RouterOsVersion = baseline.RouterOsVersion,
            Architecture = baseline.Architecture,
            SourceKind = baseline.SourceKind,
            Instances =
            [
                new OspfInstanceRow { Name = "legacy-a", RouterId = "10.16.0.11", Version = "2" },
                new OspfInstanceRow { Name = "legacy-b", RouterId = "10.16.0.12", Version = "2" },
                new OspfInstanceRow { Name = "default-v3", RouterId = "10.16.0.11", Version = "3" },
            ],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Instance = null, Type = "default" },
                new OspfAreaRow
                {
                    Name = "BR01.POP011.016.Tommy",
                    AreaId = "10.16.0.11",
                    Instance = null,
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
            InterfaceTemplates = baseline.InterfaceTemplates,
            AreaRanges = baseline.AreaRanges,
            OspfV3Objects = baseline.OspfV3Objects,
            IpAddresses = baseline.IpAddresses,
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.False(result.Plan.IsValid);
        Assert.Contains(
            result.Plan.ValidationErrors,
            e => e.Contains("Multiple OSPFv2", StringComparison.OrdinalIgnoreCase)
                 || e.Contains("unambiguously", StringComparison.OrdinalIgnoreCase)
                 || e.Contains("cannot select the owning instance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reconciler_RemovesOspfV3_BeforeAreaEnsure_AndRefusesResidual()
    {
        OrderingOps ops = new() { ResidualAfterV3 = OrphanResidualInventory() };
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
                .ApplyAsync(
                    FakeMikrotikConnections.CreateFullyPopulated(version: "7.23.8"),
                    MinimalPlan(),
                    managementHost: "198.18.240.11",
                    CancellationToken.None));

        Assert.Contains("OSPFv3 residual", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ops.Events, e => e == "v3");
        Assert.DoesNotContain(ops.Events, e => e == "area");
        Assert.DoesNotContain(ops.Events, e => e == "backhaul");
    }

    [Fact]
    public async Task Reconciler_SameNamedV3Area_FailsClosed_BeforeMutation()
    {
        OrderingOps ops = new()
        {
            Inventory = CollidingNameInventory(),
        };

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
                .ApplyAsync(
                    FakeMikrotikConnections.CreateFullyPopulated(version: "7.23.8"),
                    MinimalPlan(),
                    managementHost: "198.18.240.11",
                    CancellationToken.None));

        Assert.Contains("OSPFv3 removal selection failed before mutation", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ops.Events, e => e == "rename");
        Assert.DoesNotContain(ops.Events, e => e == "v3");
        Assert.DoesNotContain(ops.Events, e => e == "area");
    }

    [Fact]
    public async Task Verifier_RequiresDefaultV2Name_AndLiteralRid()
    {
        OspfMigrationPlan plan = MinimalPlan();
        VerifierOps okOps = new() { Inventory = CompleteInventory(plan) };
        OspfVerificationResult ok = await new OspfV7MigrationVerifier(
                okOps,
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.True(ok.Succeeded, string.Join("; ", ok.Errors));

        OspfV7Inventory legacyInventory = CompleteInventory(plan);
        legacyInventory = new OspfV7Inventory
        {
           
            CaptureStatus = OspfInventoryCaptureStatus.Complete,Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*1",
                    Name = "default",
                    RouterId = plan.RouterId,
                    Version = "2",
                },
            ],
            Areas = legacyInventory.Areas
                .Select(a => new OspfAreaRow
                {
                    Id = a.Id,
                    Name = a.Name,
                    AreaId = a.AreaId,
                    Instance = "default",
                    Type = a.Type,
                    DefaultCost = a.DefaultCost,
                    NoSummaries = a.NoSummaries,
                })
                .ToList(),
            AreaRanges = legacyInventory.AreaRanges,
            Templates = legacyInventory.Templates,
        };
        OspfVerificationResult legacy = await new OspfV7MigrationVerifier(
                new VerifierOps { Inventory = legacyInventory },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.False(legacy.Succeeded);
        Assert.Contains(legacy.Errors, e => e.Contains("default-v2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verifier_Fails_OnResidualOspfV3Template()
    {
        OspfMigrationPlan plan = MinimalPlan();
        OspfV7Inventory inventory = CompleteInventory(plan);
        inventory = new OspfV7Inventory
        {
           
            CaptureStatus = OspfInventoryCaptureStatus.Complete,Instances = inventory.Instances,
            Areas = inventory.Areas,
            AreaRanges = inventory.AreaRanges,
            Templates =
            [
                ..inventory.Templates,
                new OspfInterfaceTemplateRow
                {
                    Id = "*tOrphan",
                    Area = "missing-v3-area",
                    Networks = "2001:db8::/64",
                },
            ],
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(
                new VerifierOps { Inventory = inventory },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("OSPFv3 residual", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TryRemoveUnusedRoutingId_RetainsWhenNonOspfConsumersCannotBeRuledOut()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/instance/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*1",
                        ["name"] = "default-v2",
                        ["router-id"] = "10.16.0.11",
                        ["version"] = "2",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/routing/id/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*rid",
                        ["name"] = "main",
                        ["id"] = "10.16.0.11",
                    }),
                    new TikFakeDoneSentence(),
                ]);
        CommandRecordingConnection connection = new(fake);
        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);

        await ops.TryRemoveUnusedRoutingIdAsync(connection, "main", CancellationToken.None);

        Assert.DoesNotContain(
            connection.Commands,
            c => c.Contains("/routing/id/remove", StringComparison.Ordinal));
        Assert.Contains(
            connection.Commands,
            c => c.Contains("/routing/ospf/instance/print", StringComparison.Ordinal));
    }

    private static OspfCaptureSnapshot BaselineDualProtocol(string v2Name, string v3Name, string v3AreaName) =>
        new()
        {
            ImportId = Guid.Empty,
            JobId = "job",
            RouterIp = IPAddress.Parse("10.16.0.11"),
            Identity = "BR01.POP011.016.Tommy",
            RouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances =
            [
                new OspfInstanceRow { Name = v2Name, RouterId = "10.16.0.11", Version = "2" },
                new OspfInstanceRow { Name = v3Name, RouterId = "10.16.0.11", Version = "3" },
            ],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Instance = v2Name, Type = "default" },
                new OspfAreaRow
                {
                    Name = "BR01.POP011.016.Tommy",
                    AreaId = "10.16.0.11",
                    Instance = v2Name,
                    Type = "stub",
                    DefaultCost = "1",
                },
                new OspfAreaRow { Name = v3AreaName, AreaId = "0.0.0.0", Instance = v3Name, Type = "default" },
            ],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "ether1",
                    Networks = "10.16.248.0/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "Loopback",
                    Networks = "10.16.0.11/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "BR01.POP011.016.Tommy",
                    Interfaces = "all",
                    Networks = "10.16.11.0/24",
                    Passive = true,
                },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow { Area = "BR01.POP011.016.Tommy", Range = "10.16.11.0/24", Comment = "MGMT" },
            ],
            OspfV3Objects =
            [
                new OspfV3ObjectRef
                {
                    Path = "/routing/ospf/instance",
                    Id = "*v3",
                    Name = v3Name,
                    Kind = "instance-v3",
                },
            ],
            IpAddresses =
            [
                ..OspfTestBaselines.LoopbackSlash32("10.16.0.11", "Loopback"),
                new OspfIpAddressRow
                {
                    Address = "10.16.248.1/30",
                    Network = "10.16.248.0",
                    Interface = "ether1",
                },
                new OspfIpAddressRow
                {
                    Address = "10.16.11.1/24",
                    Network = "10.16.11.0",
                    Interface = "Bridge-VLAN-199_MGMT",
                },
            ],
        };

    private static OspfV7Inventory CompleteInventory(OspfMigrationPlan plan)
    {
        OspfV7Inventory baseInventory = OspfTestBaselines.InventoryMatchingPlan(plan);
        return new OspfV7Inventory
        {
           
            CaptureStatus = OspfInventoryCaptureStatus.Complete,Instances = baseInventory.Instances,
            Areas = baseInventory.Areas,
            AreaRanges =
            [
                new OspfAreaRangeRow
                {
                    Id = "*r",
                    Area = plan.TargetSiteAreaName,
                    Range = "10.16.11.0/24",
                    Comment = "MGMT",
                },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*b",
                    Area = plan.TargetBackboneAreaName,
                    Interfaces = "ether1",
                    Networks = "10.16.248.0/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*l",
                    Area = plan.TargetBackboneAreaName,
                    Interfaces = "Loopback",
                    Networks = "10.16.0.11/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*s",
                    Area = plan.TargetSiteAreaName,
                    Interfaces = "all",
                    Networks = "10.16.11.0/24",
                    Passive = true,
                },
            ],
        };
    }

    private static OspfMigrationPlan MinimalPlan() => new()
    {
        ImportId = Guid.Empty,
        JobId = "job",
        RouterIp = "10.16.0.11",
        Identity = "BR01.POP011.016.Tommy",
        SourceRouterOsVersion = "7.23.8",
        Architecture = "arm",
        SourceKind = OspfCaptureSourceKind.RouterOsV7,
        ContentHash = "hash",
        InstanceName = "default-v2",
        RouterId = "10.16.0.11",
        TargetInstanceName = OspfFleetNaming.Ros7InstanceName,
        TargetBackboneAreaName = OspfFleetNaming.Ros7BackboneAreaName,
        TargetSiteAreaName = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
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
                Name = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
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
                AreaName = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
                RangeCidr = "10.16.11.0/24",
                Comment = "MGMT",
            },
        ],
        ActiveBackhauls =
        [
            new OspfPlanBackhaulTemplate
            {
                AreaName = "backbone-v2",
                Interface = "ether1",
                NetworkCidr = "10.16.248.0/30",
                Cost = 10,
                Type = "ptp",
                Passive = false,
            },
        ],
        PassiveLoopbacks =
        [
            new OspfPlanLoopbackTemplate
            {
                AreaName = "backbone-v2",
                NetworkCidr = "10.16.0.11/32",
                Interface = "Loopback",
                Passive = true,
            },
        ],
        PassiveSiteNetworks =
        [
            new OspfPlanSiteNetworkTemplate
            {
                AreaName = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
                Interfaces = "all",
                NetworkCidr = "10.16.11.0/24",
                Passive = true,
            },
        ],
    };

    private static OspfV7Inventory OrphanResidualInventory()
    {
        OspfV7Inventory clean = OspfTestBaselines.InventoryMatchingPlan(MinimalPlan());
        return new OspfV7Inventory
        {
           
            CaptureStatus = OspfInventoryCaptureStatus.Complete,Instances = clean.Instances,
            Areas = clean.Areas,
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*tOrphan",
                    Area = "gone-v3-area",
                    Networks = "2001:db8::/64",
                },
            ],
        };
    }

    private static OspfV7Inventory CollidingNameInventory() =>
        new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*v2",
                    Name = "default-v2",
                    RouterId = "10.16.0.11",
                    Version = "2",
                },
                new OspfInstanceRow { Id = "*v3", Name = "default-v3", RouterId = "10.16.0.11", Version = "3" },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*a2",
                    Name = "backbone",
                    AreaId = "0.0.0.0",
                    Instance = "default-v2",
                },
                new OspfAreaRow
                {
                    Id = "*a3",
                    Name = "backbone",
                    AreaId = "0.0.0.0",
                    Instance = "default-v3",
                },
                new OspfAreaRow
                {
                    Id = "*s2",
                    Name = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
                    AreaId = "10.16.0.11",
                    Instance = "default-v2",
                    Type = "stub",
                },
            ],
        };

    private sealed class OrderingOps : IOspfMikrotikOperations
    {
        public List<string> Events { get; } = [];
        public OspfV7Inventory Inventory { get; init; } = OspfTestBaselines.InventoryMatchingPlan(
            new OspfMigrationPlan
            {
                ImportId = Guid.Empty,
                JobId = "job",
                RouterIp = "10.16.0.11",
                Identity = "BR01.POP011.016.Tommy",
                SourceRouterOsVersion = "7.23.8",
                Architecture = "arm",
                SourceKind = OspfCaptureSourceKind.RouterOsV7,
                ContentHash = "hash",
                InstanceName = "default-v2",
                RouterId = "10.16.0.11",
                TargetInstanceName = "default-v2",
                TargetBackboneAreaName = "backbone-v2",
                TargetSiteAreaName = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
                Areas =
                [
                    new OspfPlanArea { Name = "backbone-v2", AreaId = "0.0.0.0", IsBackbone = true, TargetType = "default" },
                    new OspfPlanArea
                    {
                        Name = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
                        AreaId = "10.16.0.11",
                        IsBackbone = false,
                        TargetType = "stub",
                        DefaultCost = "1",
                    },
                ],
                ActiveBackhauls =
                [
                    new OspfPlanBackhaulTemplate
                    {
                        AreaName = "backbone-v2",
                        Interface = "ether1",
                        NetworkCidr = "10.16.248.0/30",
                        Cost = 10,
                        Type = "ptp",
                        Passive = false,
                    },
                ],
            });

        public OspfV7Inventory? ResidualAfterV3 { get; init; }
        private bool _afterV3;

        public Task<OspfCaptureSnapshot> CaptureAsync(
            ITikConnection connection,
            OspfCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

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

        public Task EnsureOspfAreaAsync(
            ITikConnection connection,
            string instanceName,
            OspfPlanArea area,
            CancellationToken cancellationToken = default)
        {
            Events.Add("area");
            return Task.CompletedTask;
        }

        public Task EnsureOspfAreaRangeAsync(
            ITikConnection connection,
            OspfPlanAreaRange range,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureBackhaulTemplateAsync(
            ITikConnection connection,
            OspfPlanBackhaulTemplate template,
            CancellationToken cancellationToken = default)
        {
            Events.Add("backhaul");
            return Task.CompletedTask;
        }

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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveOspfV3Async(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default)
        {
            Events.Add("v3");
            _afterV3 = true;
            return Task.CompletedTask;
        }

        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_afterV3 && ResidualAfterV3 is not null ? ResidualAfterV3 : Inventory);

        public Task SetInstanceRouterIdAsync(
            ITikConnection connection,
            string instanceName,
            string routerId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            string managementHost,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        public Task AssertBackboneStillActiveAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            OspfManagementPlaneBaseline baseline,
            CancellationToken cancellationToken = default,
            bool requireFleetCost = true) =>
            Task.CompletedTask;

        public Task EnsureOspfInstanceNameAsync(
            ITikConnection connection,
            string currentName,
            string targetName,
            CancellationToken cancellationToken = default)
        {
            Events.Add("rename");
            return Task.CompletedTask;
        }

        public Task TryRemoveUnusedRoutingIdAsync(
            ITikConnection connection,
            string? routingIdName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
    }

    private sealed class VerifierOps : IOspfMikrotikOperations
    {
        public required OspfV7Inventory Inventory { get; init; }

        public Task<OspfCaptureSnapshot> CaptureAsync(
            ITikConnection connection,
            OspfCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

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
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveOspfV3Async(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        public Task SetInstanceRouterIdAsync(
            ITikConnection connection,
            string instanceName,
            string routerId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            string managementHost,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        public Task AssertBackboneStillActiveAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            OspfManagementPlaneBaseline baseline,
            CancellationToken cancellationToken = default,
            bool requireFleetCost = true) =>
            Task.CompletedTask;

        public Task EnsureOspfInstanceNameAsync(
            ITikConnection connection,
            string currentName,
            string targetName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task TryRemoveUnusedRoutingIdAsync(
            ITikConnection connection,
            string? routingIdName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
    }
}
