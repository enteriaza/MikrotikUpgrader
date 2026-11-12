// -----------------------------------------------------------------------------
// File: OspfPreApplyIsolationTests.cs
//
// Description:
// Tests for pre-apply backhaul isolation / backbone invariant checks.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Tests for pre-apply backhaul isolation and backbone invariants.
/// </summary>
public sealed class OspfPreApplyIsolationTests
{
    /// <summary>
    /// Scenario: the backhaul peer /30 host (10.0.0.2/30) has drifted onto vlan10 after a simulated reboot, so a narrowed template would activate OSPF on vlan10 as well.
    /// Expected: ApplyAndVerifyAsync fails with OspfPlanValidationFailed, the detail names vlan10, and no apply step starts.
    /// Invariant: the pre-apply address re-read must reject a drifted plan before any router change is made.
    /// </summary>
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

    /// <summary>
    /// Scenario: reading the router's IP address inventory throws just before apply.
    /// Expected: the gate fails with OspfMigrationApplyFailed and the apply never starts.
    /// Invariant: without a complete address inventory isolation cannot be proven, so apply is refused.
    /// </summary>
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

    /// <summary>
    /// Scenario: the address inventory matches the plan (lo, ether1 /30 and bridge-local only) and post-apply inventory is healthy.
    /// Expected: ApplyAndVerifyAsync succeeds and the apply is started.
    /// Invariant: the isolation gate blocks only drifted inventories and lets a clean plan through.
    /// </summary>
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

    /// <summary>
    /// Fleet invariant requires exact /30 backhauls; isolation tests use /30 only.
    /// </summary>
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

    /// <summary>
    /// Builds a v7 inventory matching the base plan, with site areas that publish summaries, a management range and
    /// the planned templates.
    /// </summary>
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

    /// <summary>
    /// Builds a minimal scheduled job for router AP01 (10.0.0.1) used as the apply-and-verify context.
    /// </summary>
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
        public Task<OspfMigrationPlan?> TryLoadAsync(MikrotikScheduledJob job, string identity, CancellationToken cancellationToken) =>
            Task.FromResult<OspfMigrationPlan?>(null);

        /// <summary>
        /// No-op; there is nothing persisted to delete.
        /// </summary>
        public void Delete(MikrotikScheduledJob job, System.Net.IPAddress? routerIp = null)
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
    /// Ops fake that serves configurable addresses and inventory (optionally throwing on address read) and records
    /// when the first mutating step starts.
    /// </summary>
    private sealed class RecordingOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// IP address rows returned by <see cref="ReadIpAddressInventoryAsync"/> during pre-apply isolation checks.
        /// </summary>
        public IReadOnlyList<OspfIpAddressRow> Addresses { get; init; } = [];

        /// <summary>
        /// v7 inventory returned by <see cref="ReadV7InventoryAsync"/> for post-apply verification.
        /// </summary>
        public OspfV7Inventory Inventory { get; init; } = new();

        /// <summary>
        /// When <see langword="true"/>, <see cref="ReadIpAddressInventoryAsync"/> throws to simulate an incomplete address inventory.
        /// </summary>
        public bool ThrowOnAddressRead { get; init; }

        /// <summary>
        /// Set once the first mutating step (routing filter removal) runs; stays <see langword="false"/> when isolation checks reject the plan before apply.
        /// </summary>
        public bool ApplyStarted { get; private set; }

        /// <summary>
        /// Returns <see cref="Addresses"/>, or throws <see cref="IOException"/> when <see cref="ThrowOnAddressRead"/> is set.
        /// </summary>
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

        /// <summary>
        /// Marks <see cref="ApplyStarted"/>, since this is the first mutating step of the apply phase.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            ApplyStarted = true;
            return Task.CompletedTask;
        }

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
        /// No-op; router-id changes are not under test.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
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
        /// Returns the preconfigured <see cref="Inventory"/>.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        /// <summary>
        /// Reports zero routing filters.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// Not supported; capture is not part of the apply-and-verify path.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
