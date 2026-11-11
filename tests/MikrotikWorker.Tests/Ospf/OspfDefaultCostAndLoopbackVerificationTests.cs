using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Ospf;

public sealed class OspfDefaultCostAndLoopbackVerificationTests
{
    [Fact]
    public void Planner_PreservesValidDefaultCost_AndRejectsInvalid()
    {
        OspfCaptureSnapshot ok = BaselineV6(defaultCost: "20");
        OspfPlanValidationResult preserved = OspfMigrationPlanner.Build(ok);
        Assert.True(preserved.Succeeded);
        Assert.Equal("20", Assert.Single(preserved.Plan.Areas, a => a.Name == "site").DefaultCost);

        OspfCaptureSnapshot bad = BaselineV6(defaultCost: "nope");
        Assert.False(OspfMigrationPlanner.Build(bad).Succeeded);
    }

    [Fact]
    public void Planner_LeavesDefaultCostUnset_WhenSourceAbsent()
    {
        OspfPlanValidationResult result = OspfMigrationPlanner.Build(BaselineV6(defaultCost: null));
        Assert.True(result.Succeeded);
        Assert.Null(Assert.Single(result.Plan.Areas, a => a.Name == "site").DefaultCost);
    }

    [Fact]
    public async Task Verifier_DetectsStaleDefaultCost_WhenPlanRequiresUnset()
    {
        OspfMigrationPlan plan = ValidPlan(defaultCost: null);
        FakeOps ops = new()
        {
            Inventory = InventoryWithArea(defaultCost: "99"),
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("default-cost", StringComparison.OrdinalIgnoreCase)
            && e.Contains("unset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verifier_AcceptsMatchingDefaultCost()
    {
        OspfMigrationPlan plan = ValidPlan(defaultCost: "20");
        FakeOps ops = new()
        {
            Inventory = InventoryWithArea(defaultCost: "20"),
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public async Task Verifier_RejectsLoopbackStillMarkedPtp()
    {
        OspfMigrationPlan plan = ValidPlan(defaultCost: null);
        OspfV7Inventory inventory = InventoryWithArea(
            defaultCost: null,
            loopbackType: "ptp");

        FakeOps ops = new() { Inventory = inventory };
        OspfVerificationResult result = await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("must not be PTP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verifier_AcceptsPassiveLoopbackWithoutType()
    {
        OspfMigrationPlan plan = ValidPlan(defaultCost: null);
        FakeOps ops = new() { Inventory = InventoryWithArea(defaultCost: null) };
        OspfVerificationResult result = await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.True(result.Succeeded, result.Detail);
    }

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
                    AreaId = "10.0.0.1",
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
            ],
        };

    private static OspfMigrationPlan ValidPlan(string? defaultCost)
    {
        OspfPlanValidationResult built = OspfMigrationPlanner.Build(BaselineV6(defaultCost));
        Assert.True(built.Succeeded, string.Join("; ", built.Plan.ValidationErrors));
        return built.Plan;
    }

    private static OspfV7Inventory InventoryWithArea(string? defaultCost, string? loopbackType = null) =>
        new()
        {
            Instances = [new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.255.0.1", Version = "2" }],
            Areas =
            [
                new OspfAreaRow { Id = "*a0", Name = "backbone", AreaId = "0.0.0.0", Type = "default", Instance = "default-v2" },
                new OspfAreaRow
                {
                    Id = "*a1",
                    Name = "site",
                    AreaId = "10.0.0.1",
                    Type = "stub",
                    Instance = "default-v2",
                    DefaultCost = defaultCost,
                    NoSummaries = false,
                },
            ],
            AreaRanges = [new OspfAreaRangeRow { Id = "*r", Area = "site", Range = "10.10.0.0/16" }],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*b",
                    Area = "backbone",
                    Interfaces = "ether1",
                    Networks = "10.0.0.0/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*l",
                    Area = "backbone",
                    Networks = "10.255.0.1/32",
                    Type = loopbackType,
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*s",
                    Area = "site",
                    Interfaces = "all",
                    Networks = "10.10.0.0/16",
                    Passive = true,
                },
            ],
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

        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
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
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Minimal(managementHost));
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);


        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OspfIpAddressRow>>([]);
    }
}
