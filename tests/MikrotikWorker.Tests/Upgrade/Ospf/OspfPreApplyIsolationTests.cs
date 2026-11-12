using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
public sealed class OspfPreApplyIsolationTests
{
    [Fact]
    public async Task AddressDriftOntoOtherInterface_RejectsPlan_WithoutApplying()
    {
        OspfMigrationPlan plan = BuildPlan(narrowBackhaul: true);
        RecordingOps ops = new()
        {
            Addresses =
            [
                new OspfIpAddressRow { Address = "10.255.0.1/32", Network = "10.255.0.1", Interface = "lo" },
                new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
                new OspfIpAddressRow { Address = "10.10.1.1/24", Network = "10.10.1.0", Interface = "bridge-local" },
                // Drift after simulated reboot: peer /30 host now also present on vlan10.
                new OspfIpAddressRow { Address = "10.0.0.2/30", Network = "10.0.0.0", Interface = "vlan10" },
            ],
        };

        OspfMigrationService service = new(
            ops,
            new NoopPlanStore(),
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance),
            new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance),
            NullLogger<OspfMigrationService>.Instance);

        OspfApplyGateResult result = await service.ApplyAndVerifyAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            "AP01",
            plan,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeOutcome.OspfPlanValidationFailed, result.FailureOutcome);
        Assert.Contains("vlan10", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(ops.ApplyStarted);
    }

    [Fact]
    public async Task IncompleteAddressInventory_FailsClosed()
    {
        OspfMigrationPlan plan = BuildPlan(narrowBackhaul: true);
        RecordingOps ops = new() { ThrowOnAddressRead = true };
        OspfMigrationService service = new(
            ops,
            new NoopPlanStore(),
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance),
            new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance),
            NullLogger<OspfMigrationService>.Instance);

        OspfApplyGateResult result = await service.ApplyAndVerifyAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            "AP01",
            plan,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(UpgradeOutcome.OspfMigrationApplyFailed, result.FailureOutcome);
        Assert.False(ops.ApplyStarted);
    }

    [Fact]
    public async Task StableInventory_AllowsApplyToProceed()
    {
        OspfMigrationPlan plan = BuildPlan(narrowBackhaul: true);
        RecordingOps ops = new()
        {
            Addresses =
            [
                new OspfIpAddressRow { Address = "10.255.0.1/32", Network = "10.255.0.1", Interface = "lo" },
                new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
                new OspfIpAddressRow { Address = "10.10.1.1/24", Network = "10.10.1.0", Interface = "bridge-local" },
            ],
            Inventory = HappyInventory(),
        };

        OspfMigrationService service = new(
            ops,
            new NoopPlanStore(),
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance),
            new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance),
            NullLogger<OspfMigrationService>.Instance);

        OspfApplyGateResult result = await service.ApplyAndVerifyAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            "AP01",
            plan,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
        Assert.True(ops.ApplyStarted);
    }

    private static OspfMigrationPlan BuildPlan(bool narrowBackhaul = true)
    {
        // Fleet invariant requires exact /30 backhauls; isolation tests use /30 only.
        _ = narrowBackhaul;
        const string cidr = "10.0.0.0/30";
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "7.23.8",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.255.0.1", Version = "2" }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Type = "default" },
                new OspfAreaRow { Name = "site", AreaId = "10.255.0.1", Type = "stub" },
            ],
            AreaRanges = [new OspfAreaRangeRow { Area = "site", Range = "10.10.0.0/16" }],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "ether1",
                    Networks = cidr,
                    Type = "ptp",
                    Cost = "10",
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Networks = "10.255.0.1/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "site",
                    Networks = "10.10.0.0/16",
                    Passive = true,
                },
            ],
            IpAddresses =
            [
                new OspfIpAddressRow
                {
                    Address = "10.255.0.1/32",
                    Network = "10.255.0.1",
                    Interface = "lo",
                },
                new OspfIpAddressRow
                {
                    Address = "10.0.0.1/30",
                    Network = "10.0.0.0",
                    Interface = "ether1",
                },
                new OspfIpAddressRow
                {
                    Address = "10.10.1.1/24",
                    Network = "10.10.1.0",
                    Interface = "bridge-local",
                },
            ],
        };

        OspfPlanValidationResult built = OspfMigrationPlanner.Build(snapshot);
        Assert.True(built.Succeeded, string.Join("; ", built.Plan.ValidationErrors));
        return built.Plan;
    }

    private static OspfV7Inventory HappyInventory()
    {
        OspfMigrationPlan plan = BuildPlan();
        OspfV7Inventory inv = OspfTestBaselines.InventoryMatchingPlan(plan);
        return new OspfV7Inventory
        {
            Instances = inv.Instances,
            Areas = inv.Areas.Select(a => new OspfAreaRow
            {
                Id = a.Id,
                Name = a.Name,
                AreaId = a.AreaId,
                Type = a.Type,
                Instance = a.Instance,
                DefaultCost = a.DefaultCost,
                NoSummaries = false,
            }).ToList(),
            AreaRanges =
            [
                new OspfAreaRangeRow { Id = "*r", Area = plan.TargetSiteAreaName, Range = "10.10.0.0/16" },
                new OspfAreaRangeRow { Id = "*rm", Area = plan.TargetSiteAreaName, Range = "10.10.1.0/24", Comment = "MGMT" },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*b",
                    Area = plan.TargetBackboneAreaName,
                    Interfaces = "ether1",
                    Networks = "10.0.0.0/30",
                    Type = "ptp",
                    Cost = "10",
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*l",
                    Area = plan.TargetBackboneAreaName,
                    Networks = "10.255.0.1/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*s",
                    Area = plan.TargetSiteAreaName,
                    Interfaces = "all",
                    Networks = plan.PassiveSiteNetworks[0].NetworkCidr,
                    Passive = true,
                },
            ],
        };
    }

    private static MikrotikScheduledJob CreateJob() => new()
    {
        ImportId = Guid.NewGuid(),
        SourceFileName = "iso.csv",
        RowNumber = 1,
        RouterName = "AP01",
        RouterIp = IPAddress.Parse("10.0.0.1"),
        DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
        DateStopUtc = DateTime.UtcNow.AddHours(1),
    };

    private sealed class NoopPlanStore : IOspfMigrationPlanStore
    {
        public Task SaveAsync(OspfMigrationPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OspfMigrationPlan?> TryLoadAsync(MikrotikScheduledJob job, string identity, CancellationToken cancellationToken) =>
            Task.FromResult<OspfMigrationPlan?>(null);

        public void Delete(MikrotikScheduledJob job, System.Net.IPAddress? routerIp = null)
        {
        }

        public void CleanupOrphansOnStartup()
        {
        }
    }

    private sealed class RecordingOps : IOspfMikrotikOperations
    {
        public IReadOnlyList<OspfIpAddressRow> Addresses { get; init; } = [];

        public OspfV7Inventory Inventory { get; init; } = new();

        public bool ThrowOnAddressRead { get; init; }

        public bool ApplyStarted { get; private set; }

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnAddressRead)
            {
                throw new IOException("address print failed");
            }

            return Task.FromResult(Addresses);
        }

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            ApplyStarted = true;
            return Task.CompletedTask;
        }

        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
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
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Minimal(managementHost));
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);


        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
