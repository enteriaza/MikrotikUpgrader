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
    /// <summary>
    /// Scenario: capture status is Complete and every OSPF collection is empty (no instances, areas, templates or ranges).
    /// Expected: residual evaluation reports clean with InventoryIncomplete=false.
    /// Invariant: a deliberately empty but fully captured inventory is not treated as incomplete or residual OSPFv3.
    /// </summary>
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

    /// <summary>
    /// Scenario: a default-constructed inventory with no capture-status flags set.
    /// Expected: evaluation is not clean, InventoryIncomplete is true, and a residual of kind <c>inventory</c> is present.
    /// Invariant: missing capture flags fail closed as incomplete inventory rather than as a clean dual-protocol-free device.
    /// </summary>
    [Fact]
    public void ResidualDetector_DefaultEmptyInventory_IsIncompleteFailure()
    {
        OspfV7Inventory inventory = new();
        OspfV3ResidualDetector.Result result = OspfV3ResidualDetector.Evaluate(inventory);
        Assert.False(result.IsClean);
        Assert.True(result.InventoryIncomplete);
        Assert.Contains(result.Residuals, r => r.Kind == "inventory");
    }

    /// <summary>
    /// Scenario: exactly one of the four capture flags (instances, areas, templates, ranges) is false.
    /// Expected: evaluation fails incomplete and the detail names the missing collection token.
    /// Invariant: any single missing collection flag is sufficient to fail closed as incomplete inventory.
    /// </summary>
    /// <param name="instances">Whether instances were captured.</param>
    /// <param name="areas">Whether areas were captured.</param>
    /// <param name="templates">Whether interface-templates were captured.</param>
    /// <param name="ranges">Whether area-ranges were captured.</param>
    /// <param name="expectedToken">Substring expected in the incomplete-inventory detail.</param>
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

    /// <summary>
    /// Scenario: capture is complete, instances are empty, but an area still references <c>default-v3</c>.
    /// Expected: not clean, not incomplete, and a residual area <c>*a3</c> is reported.
    /// Invariant: orphaned OSPFv3 areas are residual even when no OSPFv3 instance row remains.
    /// </summary>
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

    /// <summary>
    /// Scenario: capture is complete, instances/areas empty, but a template still binds to a gone v3 area with an IPv6 prefix.
    /// Expected: not clean and a residual template <c>*tOrphan</c> is reported.
    /// Invariant: templates whose area is not owned by a remaining OSPFv2 instance are residual OSPFv3 debris.
    /// </summary>
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

    /// <summary>
    /// Scenario: capture is complete, instances/areas empty, but an area-range still references a gone v3 area.
    /// Expected: not clean and a residual range <c>*rOrphan</c> is reported.
    /// Invariant: orphaned area-ranges are residual even when their owning area and instance rows are already gone.
    /// </summary>
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

    /// <summary>
    /// Scenario: after OSPFv3 removal the re-read inventory is CaptureStatus.Incomplete.
    /// Expected: apply throws mentioning incomplete inventory; events include <c>v3</c> but never <c>area</c> or <c>backhaul</c>.
    /// Invariant: incomplete post-removal inventory fail-closes before any area ensure or backhaul mutation.
    /// </summary>
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

    /// <summary>
    /// Scenario: inventory rows are populated enough for live identity, but AreasCaptured is false.
    /// Expected: verify fails and errors mention incomplete OSPF inventory.
    /// Invariant: CaptureStatus alone gates residual/verify even when area/template rows happen to be present.
    /// </summary>
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

    /// <summary>
    /// Scenario: production <see cref="OspfMikrotikOperations.ReadV7InventoryAsync"/> hits a trap on area print while other prints succeed.
    /// Expected: throws InvalidOperationException mentioning areas and incomplete inventory.
    /// Invariant: a failed required collection query surfaces as incomplete capture, not a partial silent inventory.
    /// </summary>
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

    /// <summary>
    /// Scenario: interface-template print throws FormatException during mapping.
    /// Expected: throws InvalidOperationException mentioning interface-templates and incomplete inventory, with FormatException as inner.
    /// Invariant: parse failures inside a required collection are wrapped as incomplete capture, not swallowed.
    /// </summary>
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

    /// <summary>
    /// Scenario: every OSPF print returns only done (empty collections) and system resource reports 7.x.
    /// Expected: CaptureStatus.IsComplete, all collections empty, and residual detector reports clean.
    /// Invariant: successful empty prints are a complete capture of a dual-protocol-free device, not incompleteness.
    /// </summary>
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

    /// <summary>
    /// Registers empty done-only responses for OSPF instance/area/range/template and routing-filter prints, plus a 7.23.8 system resource row.
    /// </summary>
    /// <param name="fake">Fake connection to enrich.</param>
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

    /// <summary>
    /// Minimal Tommy-like migration plan targeting fleet <c>default-v2</c> naming with one backbone backhaul and no OSPFv3 removals.
    /// </summary>
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

    /// <summary>
    /// Reconciler ops fake that switches inventory to an incomplete residual after <see cref="RemoveOspfV3Async"/>
    /// and records ordering events (<c>v3</c>, <c>area</c>, <c>backhaul</c>, <c>rename</c>) so tests can prove fail-closed ordering.
    /// </summary>
    private sealed class IncompleteAfterV3Ops : IOspfMikrotikOperations
    {
        /// <summary>
        /// Ordered apply-phase event tokens recorded by mutating ops (<c>v3</c>, <c>area</c>, <c>backhaul</c>, <c>rename</c>).
        /// </summary>
        public List<string> Events { get; } = [];

        /// <summary>
        /// Inventory returned by <see cref="ReadV7InventoryAsync"/> after OSPFv3 removal; typically CaptureStatus.Incomplete.
        /// </summary>
        public required OspfV7Inventory ResidualAfterV3 { get; init; }

        /// <summary>
        /// Whether <see cref="RemoveOspfV3Async"/> has already run, flipping subsequent inventory reads to <see cref="ResidualAfterV3"/>.
        /// </summary>
        private bool _afterV3;

        /// <summary>
        /// Pre-removal Tommy-matching inventory used until OSPFv3 removal flips the read to <see cref="ResidualAfterV3"/>.
        /// </summary>
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

        /// <summary>
        /// Intentionally unsupported; capture is not part of the reconciler apply path under test.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="request">Capture request (unused).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfCaptureSnapshot> CaptureAsync(
            ITikConnection connection,
            OspfCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op stub for filter wipe.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Reports zero routing filters.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// No-op stub for redistribution clear.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task ClearOspfRedistributionAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for instance filter-reference clear.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task ClearOspfInstanceFilterReferencesAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Records an <c>area</c> event so tests can assert area ensure never ran after incomplete inventory.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="area">Area plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureOspfAreaAsync(
            ITikConnection connection,
            string instanceName,
            OspfPlanArea area,
            CancellationToken cancellationToken = default)
        {
            Events.Add("area");
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op stub for area-range ensure.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="range">Area-range plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureOspfAreaRangeAsync(
            ITikConnection connection,
            OspfPlanAreaRange range,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Records a <c>backhaul</c> event so tests can assert backhaul ensure never ran after incomplete inventory.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Backhaul template (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureBackhaulTemplateAsync(
            ITikConnection connection,
            OspfPlanBackhaulTemplate template,
            CancellationToken cancellationToken = default)
        {
            Events.Add("backhaul");
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op stub for loopback template ensure.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Loopback template (ignored).</param>
        /// <param name="protectedBackhauls">Protected backhauls (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureLoopbackTemplateAsync(
            ITikConnection connection,
            OspfPlanLoopbackTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for site-network template ensure.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Site-network template (ignored).</param>
        /// <param name="protectedBackhauls">Protected backhauls (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureSiteNetworkTemplateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for site-network state assertion.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Site-network template (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task AssertSiteNetworkTemplateStateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for obsolete-template cleanup.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="plan">Migration plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task RemoveObsoleteTemplatesAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Records a <c>v3</c> event and flips subsequent inventory reads to <see cref="ResidualAfterV3"/>.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="plan">Migration plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task RemoveOspfV3Async(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default)
        {
            Events.Add("v3");
            _afterV3 = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns <see cref="Inventory"/> before OSPFv3 removal and <see cref="ResidualAfterV3"/> afterwards.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_afterV3 ? ResidualAfterV3 : Inventory);

        /// <summary>
        /// No-op stub for router-id set.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="routerId">Router-id value (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task SetInstanceRouterIdAsync(
            ITikConnection connection,
            string instanceName,
            string routerId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns a Tommy loopback <c>/32</c> so live-identity checks can resolve.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        /// <summary>
        /// Returns the Tommy management-plane baseline for the supplied host.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="plan">Migration plan (ignored).</param>
        /// <param name="managementHost">Management host stamped into the baseline.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            string managementHost,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        /// <summary>
        /// No-op stub for backbone liveness assertion.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="plan">Migration plan (ignored).</param>
        /// <param name="baseline">Management-plane baseline (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <param name="requireFleetCost">Whether fleet cost is required (ignored).</param>
        public Task AssertBackboneStillActiveAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            OspfManagementPlaneBaseline baseline,
            CancellationToken cancellationToken = default,
            bool requireFleetCost = true) =>
            Task.CompletedTask;

        /// <summary>
        /// Records a <c>rename</c> event so tests can assert instance rename ordering relative to fail-closed gates.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="currentName">Current instance name (ignored).</param>
        /// <param name="targetName">Target instance name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureOspfInstanceNameAsync(
            ITikConnection connection,
            string currentName,
            string targetName,
            CancellationToken cancellationToken = default)
        {
            Events.Add("rename");
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op stub for unused routing-id cleanup.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="routingIdName">Routing-id name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task TryRemoveUnusedRoutingIdAsync(
            ITikConnection connection,
            string? routingIdName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns an empty neighbour list.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
    }

    /// <summary>
    /// Verify-only <see cref="IOspfMikrotikOperations"/> fake that returns a caller-supplied inventory; mutation APIs are no-ops
    /// and capture is intentionally unsupported.
    /// </summary>
    private sealed class VerifierOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Canned inventory returned by <see cref="ReadV7InventoryAsync"/> (complete or incomplete as the scenario requires).
        /// </summary>
        public required OspfV7Inventory Inventory { get; init; }

        /// <summary>
        /// Intentionally unsupported; capture is never invoked on the verify path under test.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="request">Capture request (unused).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfCaptureSnapshot> CaptureAsync(
            ITikConnection connection,
            OspfCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op stub for filter wipe.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Reports zero routing filters.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// No-op stub for redistribution clear.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task ClearOspfRedistributionAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for instance filter-reference clear.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task ClearOspfInstanceFilterReferencesAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for area ensure.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="area">Area plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureOspfAreaAsync(
            ITikConnection connection,
            string instanceName,
            OspfPlanArea area,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for area-range ensure.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="range">Area-range plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureOspfAreaRangeAsync(
            ITikConnection connection,
            OspfPlanAreaRange range,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for backhaul template ensure.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Backhaul template (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureBackhaulTemplateAsync(
            ITikConnection connection,
            OspfPlanBackhaulTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for loopback template ensure.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Loopback template (ignored).</param>
        /// <param name="protectedBackhauls">Protected backhauls (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureLoopbackTemplateAsync(
            ITikConnection connection,
            OspfPlanLoopbackTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for site-network template ensure.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Site-network template (ignored).</param>
        /// <param name="protectedBackhauls">Protected backhauls (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureSiteNetworkTemplateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for site-network state assertion.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="template">Site-network template (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task AssertSiteNetworkTemplateStateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for obsolete-template cleanup.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="plan">Migration plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task RemoveObsoleteTemplatesAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for OSPFv3 removal.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="plan">Migration plan (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task RemoveOspfV3Async(
            ITikConnection connection,
            OspfMigrationPlan plan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns the canned <see cref="Inventory"/>.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        /// <summary>
        /// No-op stub for router-id set.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="instanceName">Instance name (ignored).</param>
        /// <param name="routerId">Router-id value (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task SetInstanceRouterIdAsync(
            ITikConnection connection,
            string instanceName,
            string routerId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns a Tommy loopback <c>/32</c> so live-identity checks can resolve.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        /// <summary>
        /// Returns the Tommy management-plane baseline for the supplied host.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="plan">Migration plan (ignored).</param>
        /// <param name="managementHost">Management host stamped into the baseline.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            string managementHost,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        /// <summary>
        /// No-op stub for backbone liveness assertion.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="plan">Migration plan (ignored).</param>
        /// <param name="baseline">Management-plane baseline (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        /// <param name="requireFleetCost">Whether fleet cost is required (ignored).</param>
        public Task AssertBackboneStillActiveAsync(
            ITikConnection connection,
            OspfMigrationPlan plan,
            OspfManagementPlaneBaseline baseline,
            CancellationToken cancellationToken = default,
            bool requireFleetCost = true) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for instance rename.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="currentName">Current instance name (ignored).</param>
        /// <param name="targetName">Target instance name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task EnsureOspfInstanceNameAsync(
            ITikConnection connection,
            string currentName,
            string targetName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op stub for unused routing-id cleanup.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="routingIdName">Routing-id name (ignored).</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task TryRemoveUnusedRoutingIdAsync(
            ITikConnection connection,
            string? routingIdName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns an empty neighbour list.
        /// </summary>
        /// <param name="connection">Unused RouterOS connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
    }
}
