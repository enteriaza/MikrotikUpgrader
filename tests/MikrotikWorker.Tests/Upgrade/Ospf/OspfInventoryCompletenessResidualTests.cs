// -----------------------------------------------------------------------------
// File: OspfInventoryCompletenessResidualTests.cs
//
// Description:
// Fail-closed residual verification when OSPF inventory capture is incomplete,
// empty-but-complete, or partially populated.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Tests.Infrastructure.Fakes;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Inventory-completeness gates for OSPFv3 residual detection and apply/verify.
/// </summary>
public sealed class OspfInventoryCompletenessResidualTests
{
    [Fact]
    public void ResidualDetector_CompleteEmptyInventory_IsClean()
    {
        OspfV7Inventory inventory = new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances = [],
            Areas = [],
            Templates = [],
            AreaRanges = [],
        };

        OspfV3ResidualDetector.Result result = OspfV3ResidualDetector.Evaluate(inventory);
        Assert.True(result.IsClean);
        Assert.False(result.InventoryIncomplete);
    }

    [Fact]
    public void ResidualDetector_DefaultEmptyInventory_IsIncompleteFailure()
    {
        OspfV7Inventory inventory = new();
        OspfV3ResidualDetector.Result result = OspfV3ResidualDetector.Evaluate(inventory);
        Assert.False(result.IsClean);
        Assert.True(result.InventoryIncomplete);
        Assert.Contains(result.Residuals, r => r.Kind == "inventory");
    }

    [Theory]
    [InlineData(false, true, true, true, "instances")]
    [InlineData(true, false, true, true, "areas")]
    [InlineData(true, true, false, true, "interface-templates")]
    [InlineData(true, true, true, false, "area-ranges")]
    public void ResidualDetector_MissingCollectionFlag_Fails(
        bool instances,
        bool areas,
        bool templates,
        bool ranges,
        string expectedToken)
    {
        OspfV7Inventory inventory = new()
        {
            CaptureStatus = new OspfInventoryCaptureStatus
            {
                InstancesCaptured = instances,
                AreasCaptured = areas,
                TemplatesCaptured = templates,
                RangesCaptured = ranges,
            },
        };

        OspfV3ResidualDetector.Result result = OspfV3ResidualDetector.Evaluate(inventory);
        Assert.False(result.IsClean);
        Assert.True(result.InventoryIncomplete);
        Assert.Contains(expectedToken, result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResidualDetector_EmptyInstances_WithOspfV3Area_IsResidual()
    {
        OspfV7Inventory inventory = new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances = [],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*a3",
                    Name = "backbone-v3",
                    AreaId = "0.0.0.0",
                    Instance = "default-v3",
                },
            ],
        };

        OspfV3ResidualDetector.Result result = OspfV3ResidualDetector.Evaluate(inventory);
        Assert.False(result.IsClean);
        Assert.False(result.InventoryIncomplete);
        Assert.Contains(result.Residuals, r => r.Kind == "area" && r.Id == "*a3");
    }

    [Fact]
    public void ResidualDetector_EmptyInstances_WithOrphanedTemplate_IsResidual()
    {
        OspfV7Inventory inventory = new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances = [],
            Areas = [],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*tOrphan",
                    Area = "gone-v3",
                    Networks = "2001:db8::/64",
                },
            ],
        };

        OspfV3ResidualDetector.Result result = OspfV3ResidualDetector.Evaluate(inventory);
        Assert.False(result.IsClean);
        Assert.Contains(result.Residuals, r => r.Kind == "template" && r.Id == "*tOrphan");
    }

    [Fact]
    public void ResidualDetector_EmptyInstances_WithOrphanedRange_IsResidual()
    {
        OspfV7Inventory inventory = new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances = [],
            Areas = [],
            AreaRanges =
            [
                new OspfAreaRangeRow { Id = "*rOrphan", Area = "gone-v3", Range = "2001:db8::/32" },
            ],
        };

        OspfV3ResidualDetector.Result result = OspfV3ResidualDetector.Evaluate(inventory);
        Assert.False(result.IsClean);
        Assert.Contains(result.Residuals, r => r.Kind == "range" && r.Id == "*rOrphan");
    }

    [Fact]
    public async Task Reconciler_IncompleteInventoryAfterRemoval_BlocksAreaEnsure()
    {
        IncompleteAfterV3Ops ops = new()
        {
            ResidualAfterV3 = new OspfV7Inventory
            {
               
            CaptureStatus = OspfInventoryCaptureStatus.Incomplete,
                Instances = [],
                Areas = [],
                Templates = [],
                AreaRanges = [],
            },
        };

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
                .ApplyAsync(
                    FakeMikrotikConnections.CreateFullyPopulated(version: "7.23.8"),
                    MinimalPlan(),
                    managementHost: "198.18.240.11",
                    CancellationToken.None));

        Assert.Contains("Incomplete OSPF inventory", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ops.Events, e => e == "v3");
        Assert.DoesNotContain(ops.Events, e => e == "area");
        Assert.DoesNotContain(ops.Events, e => e == "backhaul");
    }

    [Fact]
    public async Task Verifier_IncompleteInventory_Fails()
    {
        OspfMigrationPlan plan = MinimalPlan();
        // Keep area/template rows so live identity can resolve; CaptureStatus alone must fail residual.
        OspfV7Inventory completeShape = OspfTestBaselines.InventoryMatchingPlan(plan);
        OspfV7Inventory incomplete = new()
        {
            CaptureStatus = new OspfInventoryCaptureStatus
            {
                InstancesCaptured = true,
                AreasCaptured = false,
                TemplatesCaptured = true,
                RangesCaptured = true,
            },
            Instances = completeShape.Instances,
            Areas = completeShape.Areas,
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
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow
                {
                    Id = "*r",
                    Area = plan.TargetSiteAreaName,
                    Range = "10.16.11.0/24",
                },
            ],
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(
                new VerifierOps { Inventory = incomplete },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Incomplete OSPF inventory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadV7Inventory_FailedAreaQuery_ThrowsIncompleteCapture()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/instance/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/print",
                _ => throw new InvalidOperationException("simulated area print trap"))
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/range/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/filter/rule/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/filter/select-rule/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["version"] = "7.23.8" }),
                    new TikFakeDoneSentence(),
                ]);

        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ops.ReadV7InventoryAsync(fake, CancellationToken.None));

        Assert.Contains("areas", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incomplete inventory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadV7Inventory_ParsingFailure_ThrowsIncompleteCapture()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/instance/print",
                [
                    // Force map failure by throwing via a trap-style handler that returns a row then
                    // relies on CaptureRequiredCollectionAsync wrapping map exceptions — use a
                    // connection that throws during ExecuteList for templates.
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/range/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => throw new FormatException("simulated template parse failure"))
            .WithResponse(
                static rows => rows.First() == "/routing/filter/rule/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/filter/select-rule/print",
                [new TikFakeDoneSentence()]);

        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ops.ReadV7InventoryAsync(fake, CancellationToken.None));

        Assert.Contains("interface-templates", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incomplete inventory", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public async Task ReadV7Inventory_SuccessfulEmptyCollections_SetsCompleteCapture()
    {
        TikFakeConnection fake = EnrichMinimalPrints(new TikFakeConnection());
        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);
        OspfV7Inventory inventory = await ops.ReadV7InventoryAsync(fake, CancellationToken.None);

        Assert.True(inventory.CaptureStatus.IsComplete);
        Assert.Empty(inventory.Instances);
        Assert.Empty(inventory.Areas);
        Assert.Empty(inventory.Templates);
        Assert.Empty(inventory.AreaRanges);
        Assert.True(OspfV3ResidualDetector.Evaluate(inventory).IsClean);
    }

    private static TikFakeConnection EnrichMinimalPrints(TikFakeConnection fake) =>
        fake
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/instance/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/range/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/filter/rule/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/filter/select-rule/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["version"] = "7.23.8" }),
                    new TikFakeDoneSentence(),
                ]);

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
    };

    private sealed class IncompleteAfterV3Ops : IOspfMikrotikOperations
    {
        public List<string> Events { get; } = [];
        public required OspfV7Inventory ResidualAfterV3 { get; init; }
        private bool _afterV3;

        public OspfV7Inventory Inventory { get; } = OspfTestBaselines.InventoryMatchingPlan(
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
            Task.FromResult(_afterV3 ? ResidualAfterV3 : Inventory);

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
