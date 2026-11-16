// -----------------------------------------------------------------------------
// File: OspfVerifierDisabledAndDynamicResidualTests.cs
//
// Description:
// Final verification must exclude disabled OSPFv2 templates from equality/count,
// retain non-conflicting passive interfaces=dynamic residuals as preserved
// unmanaged, and still fail closed on enabled unexpected / unknown state.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Final-verifier regressions for disabled OSPFv2 exclusion and dynamic-template disposition.
/// </summary>
public sealed class OspfVerifierDisabledAndDynamicResidualTests
{
    [Fact]
    public async Task Verify_DisabledUnplannedOspfV2Template_DoesNotFail()
    {
        OspfMigrationPlan plan = BuildPlan();
        OspfV7Inventory inventory = BaseInventory(plan);
        inventory = WithExtraTemplate(
            inventory,
            new OspfInterfaceTemplateRow
            {
                Id = "*dis",
                Area = plan.TargetSiteAreaName,
                Interfaces = "Bridge-VLAN-197_MGMT",
                Networks = "10.16.5.32/27",
                Type = "ptp",
                Passive = true,
                Disabled = true,
                AdminState = OspfAdminState.Disabled,
            });

        OspfVerificationResult result = await VerifyAsync(plan, inventory);
        Assert.True(result.Succeeded, result.Detail);
        Assert.DoesNotContain(
            result.Errors,
            e => e.Contains("Disabled unplanned", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verify_EnabledUnplannedOspfV2Template_StillFails()
    {
        OspfMigrationPlan plan = BuildPlan();
        // More-specific under the planned interfaces=all management summary — must be cleaned,
        // not preserved as unmanaged.
        OspfV7Inventory inventory = WithExtraTemplate(
            BaseInventory(plan),
            new OspfInterfaceTemplateRow
            {
                Id = "*u",
                Area = plan.TargetSiteAreaName,
                Interfaces = "Bridge-VLAN-99_MGMT",
                Networks = "10.16.3.0/27",
                Type = "ptp",
                Passive = true,
                Disabled = false,
                AdminState = OspfAdminState.Enabled,
            });

        OspfVerificationResult result = await VerifyAsync(plan, inventory);
        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            e => e.Contains("Unexpected", StringComparison.OrdinalIgnoreCase)
                 && e.Contains("10.16.3.0/27", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_UnknownDisabledState_FailsClosed()
    {
        OspfMigrationPlan plan = BuildPlan();
        OspfV7Inventory inventory = WithExtraTemplate(
            BaseInventory(plan),
            new OspfInterfaceTemplateRow
            {
                Id = "*unk",
                Area = plan.TargetSiteAreaName,
                Interfaces = "Bridge-VLAN-197_MGMT",
                Networks = "10.16.5.32/27",
                Passive = true,
                Disabled = false,
                AdminState = OspfAdminState.Unknown,
            });

        OspfVerificationResult result = await VerifyAsync(plan, inventory);
        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            e => e.Contains("unknown disabled-state", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verify_DisabledTemplates_DoNotAffectEligibleCountOrEquality()
    {
        OspfMigrationPlan plan = BuildPlan();
        OspfV7Inventory inventory = WithExtraTemplate(
            BaseInventory(plan),
            new OspfInterfaceTemplateRow
            {
                Id = "*dis",
                Area = plan.TargetSiteAreaName,
                Interfaces = "Bridge-VLAN-197_MGMT",
                Networks = "10.16.5.32/27",
                Passive = true,
                Disabled = true,
                AdminState = OspfAdminState.Disabled,
            });

        OspfVerificationResult result = await VerifyAsync(plan, inventory);
        Assert.True(result.Succeeded, result.Detail);
        Assert.DoesNotContain(
            result.Errors,
            e => e.Contains("Template inventory count", StringComparison.OrdinalIgnoreCase)
                 || e.Contains("disabled leftovers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verify_EnabledDynamicPublicPrefix_IsPreservedUnmanaged()
    {
        OspfMigrationPlan plan = BuildPlan();
        OspfV7Inventory inventory = WithExtraTemplate(
            BaseInventory(plan),
            new OspfInterfaceTemplateRow
            {
                Id = "*dyn",
                Area = plan.TargetBackboneAreaName,
                Interfaces = "dynamic",
                Networks = "102.66.242.0/24",
                Type = "ptp",
                Passive = true,
                Disabled = false,
                AdminState = OspfAdminState.Enabled,
            });

        OspfVerificationResult result = await VerifyAsync(plan, inventory);
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public async Task Verify_SecondDistinctDynamicPrefix_IsIndependentlyPreserved()
    {
        OspfMigrationPlan plan = BuildPlan();
        OspfV7Inventory inventory = BaseInventory(plan);
        inventory = WithExtraTemplate(
            inventory,
            new OspfInterfaceTemplateRow
            {
                Id = "*d1",
                Area = plan.TargetBackboneAreaName,
                Interfaces = "dynamic",
                Networks = "102.66.242.0/24",
                Type = "ptp",
                Passive = true,
            });
        inventory = WithExtraTemplate(
            inventory,
            new OspfInterfaceTemplateRow
            {
                Id = "*d2",
                Area = plan.TargetBackboneAreaName,
                Interfaces = "dynamic",
                Networks = "203.0.113.0/24",
                Type = "ptp",
                Passive = true,
            });

        OspfVerificationResult result = await VerifyAsync(plan, inventory);
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public async Task Verify_DisabledDynamicTemplate_IsExcludedFromDecisions()
    {
        OspfMigrationPlan plan = BuildPlan();
        OspfV7Inventory inventory = WithExtraTemplate(
            BaseInventory(plan),
            new OspfInterfaceTemplateRow
            {
                Id = "*ddyn",
                Area = plan.TargetBackboneAreaName,
                Interfaces = "dynamic",
                Networks = "102.66.242.0/24",
                Type = "ptp",
                Passive = true,
                Disabled = true,
                AdminState = OspfAdminState.Disabled,
            });

        OspfVerificationResult result = await VerifyAsync(plan, inventory);
        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public void Cleanup_EnabledDynamicPublicPrefix_IsPreservedUnmanaged()
    {
        OspfMigrationPlan plan = BuildPlan();
        List<OspfTemplateInventoryRow> rows =
        [
            PlannedBackhaulRow(plan, 0),
            PlannedBackhaulRow(plan, 1),
            PlannedLoopbackRow(plan),
            PlannedSiteRow(plan),
            new OspfTemplateInventoryRow
            {
                Id = "*dyn",
                Area = plan.TargetBackboneAreaName,
                AreaInstanceName = plan.TargetInstanceName,
                Interfaces = "dynamic",
                Networks = "102.66.242.0/24",
                Type = "ptp",
                Passive = true,
            },
        ];

        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, rows);
        Assert.Empty(report.Unknown);
        Assert.Empty(report.KnownLegacy);
        Assert.Equal("*dyn", Assert.Single(report.PreservedUnmanaged).Row.Id);
    }

    [Fact]
    public void Cleanup_DynamicUnderFleetAggregate_IsKnownLegacyWithProvenance()
    {
        OspfMigrationPlan plan = BuildPlan();
        List<OspfTemplateInventoryRow> rows =
        [
            PlannedBackhaulRow(plan, 0),
            PlannedBackhaulRow(plan, 1),
            PlannedLoopbackRow(plan),
            PlannedSiteRow(plan),
            new OspfTemplateInventoryRow
            {
                Id = "*cgn",
                Area = plan.TargetSiteAreaName,
                AreaInstanceName = plan.TargetInstanceName,
                Interfaces = "dynamic",
                Networks = "100.99.0.0/16",
                Passive = true,
            },
        ];

        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, rows);
        Assert.Equal("*cgn", Assert.Single(report.KnownLegacy).Row.Id);
        Assert.Empty(report.PreservedUnmanaged);
    }

    [Fact]
    public void ClassifyUnplannedResidual_SimilarButDistinctDynamicPrefix_DoesNotMatch()
    {
        OspfMigrationPlan plan = BuildPlan();
        OspfSupersededTemplateIdentity provenance = new()
        {
            AreaName = plan.TargetBackboneAreaName,
            AreaInstanceName = plan.TargetInstanceName,
            Interfaces = "dynamic",
            NetworkCidr = "102.66.242.0/24",
            Passive = true,
            Disabled = false,
        };
        plan = CloneWithSuperseded(plan, [provenance]);

        OspfTemplateInventoryRow similar = new()
        {
            Id = "*sim",
            Area = plan.TargetBackboneAreaName,
            AreaInstanceName = plan.TargetInstanceName,
            Interfaces = "dynamic",
            Networks = "102.66.243.0/24",
            Passive = true,
        };

        OspfTemplateClassification classification = OspfTemplateCleanupClassifier.ClassifyUnplannedResidual(
            similar,
            plan,
            out _);
        Assert.Equal(OspfTemplateClassification.PreservedUnmanaged, classification);
    }

    [Fact]
    public void RemapPlan_PreservesDynamicSupersededProvenance()
    {
        OspfMigrationPlan logical = BuildPlan(logicalNames: true);
        OspfSupersededTemplateIdentity provenance = new()
        {
            AreaName = "backbone",
            AreaInstanceName = "default",
            Interfaces = "dynamic",
            NetworkCidr = "100.99.0.0/16",
            Passive = true,
            Disabled = false,
        };
        logical = CloneWithSuperseded(logical, [provenance]);

        OspfLiveIdentityBinding binding = new()
        {
            LogicalInstanceName = "default",
            LiveInstanceName = "default-v2",
            LiveInstanceId = "*i",
            DerivedLoopbackRouterId = "10.16.0.3",
            LiveRouterId = "10.16.0.3",
            LoopbackInterfaceName = "Loopback",
            Areas = new Dictionary<string, OspfLiveAreaBinding>(StringComparer.Ordinal)
            {
                ["backbone"] = new()
                {
                    LogicalName = "backbone",
                    LiveName = "backbone-v2",
                    AreaId = "0.0.0.0",
                    LiveObjectId = "*ab",
                },
                [logical.Identity] = new()
                {
                    LogicalName = logical.Identity,
                    LiveName = logical.TargetSiteAreaName,
                    AreaId = "10.16.0.3",
                    LiveObjectId = "*as",
                },
            },
        };

        OspfMigrationPlan remapped = OspfLiveIdentityResolver.RemapPlan(logical, binding);
        OspfSupersededTemplateIdentity kept = Assert.Single(remapped.SupersededTemplates);
        Assert.Equal("backbone-v2", kept.AreaName);
        Assert.Equal("dynamic", kept.Interfaces);
        Assert.Equal("100.99.0.0/16", kept.NetworkCidr);
        Assert.Equal("default-v2", kept.AreaInstanceName);
    }

    [Fact]
    public void DisabledOspfV3_RemainsEligibleForMandatoryRemovalSelection()
    {
        OspfV7Inventory inventory = new()
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*v2",
                    Name = "default-v2",
                    Version = "2",
                    RouterId = "10.16.0.3",
                    Disabled = false,
                    AdminState = OspfAdminState.Enabled,
                },
                new OspfInstanceRow
                {
                    Id = "*v3",
                    Name = "legacy-v3",
                    Version = "3",
                    Disabled = true,
                    AdminState = OspfAdminState.Disabled,
                },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*a3",
                    Name = "v3-area",
                    Instance = "legacy-v3",
                    AreaId = "0.0.0.1",
                    Disabled = true,
                    AdminState = OspfAdminState.Disabled,
                },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*t3",
                    Area = "v3-area",
                    Interfaces = "ether1",
                    Networks = "2001:db8::/64",
                    Disabled = true,
                    AdminState = OspfAdminState.Disabled,
                },
            ],
        };

        OspfV3RemovalSelection.Result selection = OspfV3RemovalSelection.Build(inventory);
        Assert.True(selection.Succeeded, selection.FailureDetail);
        Assert.Contains("*v3", selection.InstanceIds);
        Assert.Contains("*a3", selection.AreaIds);
        Assert.Contains("*t3", selection.TemplateIds);
    }

    private static async Task<OspfVerificationResult> VerifyAsync(
        OspfMigrationPlan plan,
        OspfV7Inventory inventory)
    {
        FakeOps ops = new(inventory, plan);
        return await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
    }

    private static OspfMigrationPlan BuildPlan(bool logicalNames = false)
    {
        string backbone = logicalNames ? "backbone" : "backbone-v2";
        string site = logicalNames ? "BR01.POP003.016.Valskerm" : "BR01.POP003.016.Valskerm-v2";
        string instance = logicalNames ? "default" : "default-v2";
        return new OspfMigrationPlan
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = "10.16.0.3",
            Identity = "BR01.POP003.016.Valskerm",
            SourceRouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            InstanceName = instance,
            RouterId = "10.16.0.3",
            TargetInstanceName = "default-v2",
            TargetBackboneAreaName = "backbone-v2",
            TargetSiteAreaName = "BR01.POP003.016.Valskerm-v2",
            LoopbackInterfaceName = "Loopback",
            LoopbackCidr = "10.16.0.3/32",
            DerivedManagementCidr = "10.16.3.0/24",
            DerivedManagementCidrs = ["10.16.3.0/24", "10.16.103.0/24"],
            Areas =
            [
                new OspfPlanArea
                {
                    Name = backbone,
                    AreaId = "0.0.0.0",
                    TargetType = "default",
                    IsBackbone = true,
                },
                new OspfPlanArea
                {
                    Name = site,
                    AreaId = "10.16.0.3",
                    TargetType = "stub",
                    IsBackbone = false,
                    DefaultCost = "1",
                },
            ],
            AreaRanges =
            [
                new OspfPlanAreaRange
                {
                    AreaName = site,
                    RangeCidr = "10.16.3.0/24",
                    Comment = "MGMT",
                    Advertise = true,
                },
                new OspfPlanAreaRange
                {
                    AreaName = site,
                    RangeCidr = "10.16.103.0/24",
                    Comment = "MGMT",
                    Advertise = true,
                },
            ],
            ActiveBackhauls =
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = backbone,
                    Interface = "sfpp-01_BH01.Valskerm-RustenburgKantoor_sfpp-02 [monitor:1200]",
                    NetworkCidr = "10.16.248.8/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
                new OspfPlanBackhaulTemplate
                {
                    AreaName = backbone,
                    Interface = "eth-01_BH02.Valskerm-Shylock [monitor:980]",
                    NetworkCidr = "10.16.248.12/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
            ],
            PassiveLoopbacks =
            [
                new OspfPlanLoopbackTemplate
                {
                    AreaName = backbone,
                    Interface = "Loopback",
                    NetworkCidr = "10.16.0.3/32",
                    Passive = true,
                },
            ],
            PassiveSiteNetworks =
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = site,
                    Interfaces = "all",
                    NetworkCidr = "10.16.103.0/24,10.16.3.0/24,100.64.0.0/10",
                    Passive = true,
                },
            ],
            ValidationErrors = [],
            NormalisationNotes = [],
            ContentHash = "verifier-disabled-dynamic",
        };
    }

    private static OspfMigrationPlan CloneWithSuperseded(
        OspfMigrationPlan plan,
        IReadOnlyList<OspfSupersededTemplateIdentity> superseded) =>
        new()
        {
            ImportId = plan.ImportId,
            JobId = plan.JobId,
            RouterIp = plan.RouterIp,
            Identity = plan.Identity,
            SourceRouterOsVersion = plan.SourceRouterOsVersion,
            Architecture = plan.Architecture,
            SourceKind = plan.SourceKind,
            InstanceName = plan.InstanceName,
            RouterId = plan.RouterId,
            TargetInstanceName = plan.TargetInstanceName,
            TargetBackboneAreaName = plan.TargetBackboneAreaName,
            TargetSiteAreaName = plan.TargetSiteAreaName,
            LoopbackInterfaceName = plan.LoopbackInterfaceName,
            LoopbackCidr = plan.LoopbackCidr,
            DerivedManagementCidr = plan.DerivedManagementCidr,
            DerivedManagementCidrs = plan.DerivedManagementCidrs,
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            SupersededTemplates = superseded,
            ValidationErrors = plan.ValidationErrors,
            NormalisationNotes = plan.NormalisationNotes,
            ContentHash = plan.ContentHash,
        };

    private static OspfV7Inventory BaseInventory(OspfMigrationPlan plan)
    {
        string backbone = plan.TargetBackboneAreaName;
        string site = plan.TargetSiteAreaName;
        return new OspfV7Inventory
        {
            CaptureStatus = OspfInventoryCaptureStatus.Complete,
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*i",
                    Name = plan.TargetInstanceName,
                    RouterId = plan.RouterId,
                    Version = "2",
                },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*ab",
                    Name = backbone,
                    AreaId = "0.0.0.0",
                    Instance = plan.TargetInstanceName,
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Id = "*as",
                    Name = site,
                    AreaId = plan.RouterId,
                    Instance = plan.TargetInstanceName,
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
            AreaRanges = plan.AreaRanges
                .Select(r => new OspfAreaRangeRow
                {
                    Area = site,
                    Range = r.RangeCidr,
                    Comment = r.Comment,
                    Advertise = true,
                })
                .ToList(),
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Id = "*bh1",
                    Area = backbone,
                    Interfaces = plan.ActiveBackhauls[0].Interface,
                    Networks = plan.ActiveBackhauls[0].NetworkCidr,
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*bh2",
                    Area = backbone,
                    Interfaces = plan.ActiveBackhauls[1].Interface,
                    Networks = plan.ActiveBackhauls[1].NetworkCidr,
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*lo",
                    Area = backbone,
                    Interfaces = plan.PassiveLoopbacks[0].Interface,
                    Networks = plan.PassiveLoopbacks[0].NetworkCidr,
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Id = "*site",
                    Area = site,
                    Interfaces = plan.PassiveSiteNetworks[0].Interfaces,
                    Networks = plan.PassiveSiteNetworks[0].NetworkCidr,
                    Passive = true,
                },
            ],
        };
    }

    private static OspfV7Inventory WithExtraTemplate(
        OspfV7Inventory source,
        OspfInterfaceTemplateRow extra) =>
        new()
        {
            CaptureStatus = source.CaptureStatus,
            Instances = source.Instances,
            Areas = source.Areas,
            AreaRanges = source.AreaRanges,
            Templates = [.. source.Templates, extra],
        };

    private static OspfTemplateInventoryRow PlannedBackhaulRow(OspfMigrationPlan plan, int index) =>
        new()
        {
            Id = "*bh" + (index + 1),
            Area = plan.TargetBackboneAreaName,
            AreaInstanceName = plan.TargetInstanceName,
            Interfaces = plan.ActiveBackhauls[index].Interface,
            Networks = plan.ActiveBackhauls[index].NetworkCidr,
            Type = "ptp",
            Cost = "10",
            Passive = false,
        };

    private static OspfTemplateInventoryRow PlannedLoopbackRow(OspfMigrationPlan plan) =>
        new()
        {
            Id = "*lo",
            Area = plan.TargetBackboneAreaName,
            AreaInstanceName = plan.TargetInstanceName,
            Interfaces = plan.PassiveLoopbacks[0].Interface,
            Networks = plan.PassiveLoopbacks[0].NetworkCidr,
            Passive = true,
        };

    private static OspfTemplateInventoryRow PlannedSiteRow(OspfMigrationPlan plan) =>
        new()
        {
            Id = "*site",
            Area = plan.TargetSiteAreaName,
            AreaInstanceName = plan.TargetInstanceName,
            Interfaces = plan.PassiveSiteNetworks[0].Interfaces,
            Networks = plan.PassiveSiteNetworks[0].NetworkCidr,
            Passive = true,
        };

    private sealed class FakeOps(OspfV7Inventory inventory, OspfMigrationPlan plan) : IOspfMikrotikOperations
    {
        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(inventory);

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OspfIpAddressRow>>(
            [
                new()
                {
                    Address = "10.16.248.10/30",
                    Network = "10.16.248.8",
                    Interface = plan.ActiveBackhauls[0].Interface,
                },
                new()
                {
                    Address = "10.16.248.14/30",
                    Network = "10.16.248.12",
                    Interface = plan.ActiveBackhauls[1].Interface,
                },
                new() { Address = "10.16.0.3/32", Network = "10.16.0.3", Interface = "Loopback" },
                new()
                {
                    Address = "10.16.3.1/24",
                    Network = "10.16.3.0",
                    Interface = "Bridge-VLAN-99_MGMT",
                },
                new()
                {
                    Address = "10.16.103.1/24",
                    Network = "10.16.103.0",
                    Interface = "Bridge-VLAN-99_MGMT",
                },
            ]);

        public Task<int> CountRoutingFiltersAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<OspfCaptureSnapshot> CaptureAsync(
            ITikConnection connection,
            OspfCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAllRoutingFiltersAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
            OspfMigrationPlan migrationPlan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            string managementHost,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AssertBackboneStillActiveAsync(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
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

        public Task RemoveOspfV3Async(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetInstanceRouterIdAsync(
            ITikConnection connection,
            string instanceName,
            string routerId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
