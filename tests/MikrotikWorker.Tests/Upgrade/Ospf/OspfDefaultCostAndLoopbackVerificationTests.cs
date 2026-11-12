// -----------------------------------------------------------------------------
// File: OspfDefaultCostAndLoopbackVerificationTests.cs
//
// Description:
// Tests for default OSPF cost handling and loopback verification
// postconditions.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Tests for default OSPF cost and loopback verification postconditions.
/// </summary>
public sealed class OspfDefaultCostAndLoopbackVerificationTests
{
    /// <summary>
    /// Scenario: a v6 snapshot whose site area has default-cost 20, then one whose default-cost is the non-numeric 'nope'.
    /// Expected: the first plan succeeds with DefaultCost set to the fleet standard and the original '20' kept in SourceDefaultCost; the second fails to build.
    /// Invariant: valid costs are normalised to the fleet value without losing provenance, and unparseable costs are rejected.
    /// </summary>
    [Fact]
    public void Planner_NormalisesValidDefaultCostToFleetStandard_AndRejectsInvalid()
    {
        OspfCaptureSnapshot ok = BaselineV6(defaultCost: "20");
        OspfPlanValidationResult preserved = OspfMigrationPlanner.Build(ok);
        Assert.True(preserved.Succeeded);
        OspfPlanArea site = Assert.Single(preserved.Plan.Areas, a => a.Name == "site");
        Assert.Equal(OspfFleetNaming.SiteDefaultCost, site.DefaultCost);
        Assert.Equal("20", site.SourceDefaultCost);

        OspfCaptureSnapshot bad = BaselineV6(defaultCost: "nope");
        Assert.False(OspfMigrationPlanner.Build(bad).Succeeded);
    }

    /// <summary>
    /// Scenario: a v6 snapshot whose site area has no default-cost.
    /// Expected: the plan succeeds and the site area receives the fleet default cost.
    /// Invariant: every planned site area carries a default cost even when the source router had none.
    /// </summary>
    [Fact]
    public void Planner_AppliesFleetDefaultCost_WhenSourceAbsent()
    {
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(BaselineV6(defaultCost: null));
        Assert.True(result.Succeeded);
        Assert.Equal(
            OspfFleetNaming.SiteDefaultCost,
            Assert.Single(result.Plan.Areas, a => a.Name == "site").DefaultCost);
    }

    /// <summary>
    /// Scenario: the plan requires the fleet default cost but the post-apply inventory still shows default-cost 99 on the site area.
    /// Expected: verification fails with an error mentioning default-cost.
    /// Invariant: post-apply verification must catch a site area that kept a stale default cost.
    /// </summary>
    [Fact]
    public async Task Verifier_DetectsStaleDefaultCost_WhenPlanRequiresFleetCost()
    {
        OspfMigrationPlan plan = ValidPlan(defaultCost: null);
        FakeOps ops = new()
        {
            Inventory = InventoryWithArea(defaultCost: "99"),
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("default-cost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: the plan and the post-apply inventory both use the fleet default cost.
    /// Expected: verification succeeds.
    /// Invariant: a correctly applied default cost is not reported as drift.
    /// </summary>
    [Fact]
    public async Task Verifier_AcceptsMatchingDefaultCost()
    {
        OspfMigrationPlan plan = ValidPlan(defaultCost: OspfFleetNaming.SiteDefaultCost);
        FakeOps ops = new()
        {
            Inventory = InventoryWithArea(defaultCost: OspfFleetNaming.SiteDefaultCost),
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
    }

    /// <summary>
    /// Scenario: the post-apply inventory leaves the loopback template with type=ptp.
    /// Expected: verification fails with an error saying the loopback 'must not be PTP'.
    /// Invariant: a loopback template that is still point-to-point is treated as an incomplete migration.
    /// </summary>
    [Fact]
    public async Task Verifier_RejectsLoopbackStillMarkedPtp()
    {
        OspfMigrationPlan plan = ValidPlan(defaultCost: OspfFleetNaming.SiteDefaultCost);
        OspfV7Inventory inventory = InventoryWithArea(
            defaultCost: OspfFleetNaming.SiteDefaultCost,
            loopbackType: "ptp");

        FakeOps ops = new() { Inventory = inventory };
        OspfVerificationResult result = await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("must not be PTP", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: the post-apply inventory has a passive loopback template with no type set.
    /// Expected: verification succeeds.
    /// Invariant: a passive, type-less loopback template is the correct end state and must not be flagged.
    /// </summary>
    [Fact]
    public async Task Verifier_AcceptsPassiveLoopbackWithoutType()
    {
        OspfMigrationPlan plan = ValidPlan(defaultCost: OspfFleetNaming.SiteDefaultCost);
        FakeOps ops = new() { Inventory = InventoryWithArea(defaultCost: OspfFleetNaming.SiteDefaultCost) };
        OspfVerificationResult result = await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.True(result.Succeeded, result.Detail);
    }

    /// <summary>
    /// Builds a RouterOS 6 capture snapshot with backbone and stub site areas, networks and interfaces, using the
    /// supplied site area default cost.
    /// </summary>
    private static OspfCaptureSnapshot BaselineV6(string? defaultCost) =>
        new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "6.49.23",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            Instances = [new OspfInstanceRow { Name = "default", RouterId = "10.255.0.1" }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Type = "default" },
                new OspfAreaRow
                {
                    Name = "site",
                    AreaId = "10.255.0.1",
                    Type = "stub",
                    DefaultCost = defaultCost,
                    InjectSummaryLsas = "yes",
                },
            ],
            Networks =
            [
                new OspfNetworkRow { Area = "backbone", Network = "10.255.0.1/32" },
                new OspfNetworkRow { Area = "backbone", Network = "10.0.0.0/30" },
                new OspfNetworkRow { Area = "site", Network = "10.10.0.0/16" },
            ],
            Interfaces =
            [
                new OspfInterfaceRow { Interface = "ether1", NetworkType = "ptp", Cost = "10" },
            ],
            IpAddresses =
            [
                new OspfIpAddressRow { Address = "10.255.0.1/32", Network = "10.255.0.1", Interface = "lo" },
                new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
                new OspfIpAddressRow { Address = "10.10.1.1/24", Network = "10.10.1.0", Interface = "bridge-local" },
            ],
        };

    /// <summary>
    /// Builds a valid plan from <see cref="BaselineV6"/> and asserts planning succeeded.
    /// <param name="defaultCost">Source site-area default cost fed into the planner; <see langword="null"/> to use the fleet default.</param>
    /// </summary>
    private static OspfMigrationPlan ValidPlan(string? defaultCost)
    {
        OspfPlanValidationResult built = OspfMigrationPlanner.Build(BaselineV6(defaultCost));
        Assert.True(built.Succeeded, string.Join("; ", built.Plan.ValidationErrors));
        return built.Plan;
    }

    /// <summary>
    /// Builds a v7 inventory for the default-v2 instance with backbone and stub site areas, area ranges and
    /// templates, using the given default cost and optional loopback template type.
    /// </summary>
    private static OspfV7Inventory InventoryWithArea(string? defaultCost, string? loopbackType = null)
    {
        OspfMigrationPlan plan = ValidPlan(defaultCost);
        const string site = "AP01-v2";
        const string backbone = "backbone-v2";
        return new OspfV7Inventory
        {
            Instances = [new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.255.0.1", Version = "2" }],
            Areas =
            [
                new OspfAreaRow { Id = "*a0", Name = backbone, AreaId = "0.0.0.0", Type = "default", Instance = "default-v2" },
                new OspfAreaRow
                {
                    Id = "*a1",
                    Name = site,
                    AreaId = "10.255.0.1",
                    Type = "stub",
                    Instance = "default-v2",
                    DefaultCost = defaultCost,
                    NoSummaries = false,
                },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow { Id = "*r", Area = site, Range = "10.10.0.0/16" },
                new OspfAreaRangeRow { Id = "*rm", Area = site, Range = "10.10.1.0/24", Comment = "MGMT" },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*b",
                    Area = backbone,
                    Interfaces = "ether1",
                    Networks = "10.0.0.0/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*l",
                    Area = backbone,
                    Networks = "10.255.0.1/32",
                    Type = loopbackType,
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*s",
                    Area = site,
                    Interfaces = "all",
                    Networks = plan.PassiveSiteNetworks[0].NetworkCidr,
                    Passive = true,
                },
            ],
        };
    }

    /// <summary>
    /// Ops fake that returns a preconfigured v7 inventory and a single loopback address for post-apply
    /// verification; all other operations are no-ops or unsupported.
    /// </summary>
    private sealed class FakeOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Inventory returned by <see cref="ReadV7InventoryAsync"/> for post-apply verification.
        /// </summary>
        public required OspfV7Inventory Inventory { get; init; }

        /// <summary>
        /// Returns the preconfigured inventory instead of reading from the connection.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        /// <summary>
        /// Reports zero routing filters.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// Not supported; capture is not part of the verification path under test.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op; routing filter removal is not under test.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; redistribution clearing is not under test.
        /// </summary>
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; instance filter reference clearing is not under test.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; area creation is not under test.
        /// </summary>
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; area range creation is not under test.
        /// </summary>
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; backhaul template creation is not under test.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; loopback template creation is not under test.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; site network template creation is not under test.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; site network template state assertion is not under test.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        /// <summary>
        /// Returns a minimal baseline for the given management host.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Minimal(managementHost));
        /// <summary>
        /// No-op; the backbone guard is not under test.
        /// </summary>
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <summary>
        /// Reports no OSPF neighbours.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);


        /// <summary>
        /// No-op; obsolete template removal is not under test.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; OSPFv3 removal is not under test.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; router-id changes are not under test.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns a single loopback /32 address (10.255.0.1 on <c>lo</c>).
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.255.0.1", "lo"));
    }
}
