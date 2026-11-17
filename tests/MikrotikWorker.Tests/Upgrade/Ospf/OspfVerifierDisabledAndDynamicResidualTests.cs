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
    /// <summary>
    /// Scenario: planned inventory plus one disabled unplanned OSPFv2 template on a site interface.
    /// Expected: verification succeeds and no error mentions the disabled unplanned leftover.
    /// Invariant: disabled OSPFv2 leftovers are excluded from final equality and do not fail verify.
    /// </summary>
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

    /// <summary>
    /// Scenario: planned inventory plus an enabled unplanned more-specific under the management summary.
    /// Expected: verification fails with an Unexpected error naming 10.16.3.0/27.
    /// Invariant: enabled residuals under fleet aggregates must be cleaned, not preserved unmanaged.
    /// </summary>
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

    /// <summary>
    /// Scenario: unplanned template whose admin state cannot be resolved (Unknown).
    /// Expected: verification fails mentioning unknown disabled-state.
    /// Invariant: ambiguous disabled/enabled state fails closed rather than treating the row as disabled.
    /// </summary>
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

    /// <summary>
    /// Scenario: successful verify with an extra disabled template present.
    /// Expected: success with no template-count or disabled-leftovers equality errors.
    /// Invariant: disabled rows must not inflate eligible inventory counts used for equality.
    /// </summary>
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

    /// <summary>
    /// Scenario: enabled passive interfaces=dynamic public prefix outside fleet aggregates.
    /// Expected: verification succeeds.
    /// Invariant: non-conflicting dynamic public residuals are preserved unmanaged through verify.
    /// </summary>
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

    /// <summary>
    /// Scenario: two distinct enabled dynamic public prefixes on the backbone area.
    /// Expected: verification succeeds with both present.
    /// Invariant: each dynamic residual is classified independently; one does not poison the other.
    /// </summary>
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

    /// <summary>
    /// Scenario: disabled dynamic public-prefix template alongside an otherwise clean inventory.
    /// Expected: verification succeeds.
    /// Invariant: disabled dynamic rows are excluded from preserved/legacy/unexpected decisions.
    /// </summary>
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

    /// <summary>
    /// Scenario: cleanup classifier sees planned templates plus an enabled dynamic public prefix.
    /// Expected: the dynamic row is the sole PreservedUnmanaged entry; Unknown and KnownLegacy empty.
    /// Invariant: cleanup must retain non-conflicting dynamic public templates rather than remove them.
    /// </summary>
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

    /// <summary>
    /// Scenario: dynamic template whose prefix sits under a fleet CGNAT aggregate (100.99.0.0/16).
    /// Expected: classified KnownLegacy (not PreservedUnmanaged).
    /// Invariant: dynamic under fleet aggregates is removable legacy, not preserved unmanaged.
    /// </summary>
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

    /// <summary>
    /// Scenario: superseded provenance for 102.66.242.0/24 dynamic, live residual is 102.66.243.0/24.
    /// Expected: ClassifyUnplannedResidual returns PreservedUnmanaged (no provenance match).
    /// Invariant: similar-but-distinct prefixes must not inherit superseded provenance by adjacency.
    /// </summary>
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

    /// <summary>
    /// Scenario: logical plan carries superseded dynamic 100.99.0.0/16; RemapPlan binds live *-v2 names.
    /// Expected: remapped superseded entry keeps interfaces/network and uses live area/instance names.
    /// Invariant: live-identity remapping must preserve dynamic superseded provenance fields.
    /// </summary>
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

    /// <summary>
    /// Scenario: inventory with an enabled OSPFv2 instance and a fully disabled OSPFv3 instance/area/template.
    /// Expected: V3 removal selection succeeds and includes the disabled v3 instance, area, and template ids.
    /// Invariant: disabled OSPFv3 objects remain mandatory removal candidates (unlike disabled OSPFv2 leftovers).
    /// </summary>
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

    /// <summary>
    /// Runs <see cref="OspfV7MigrationVerifier.VerifyAsync"/> against a fully populated fake connection
    /// using <see cref="FakeOps"/> seeded with the supplied inventory.
    /// </summary>
    /// <param name="plan">Migration plan under verification.</param>
    /// <param name="inventory">Live v7 inventory the fake ops will return.</param>
    private static async Task<OspfVerificationResult> VerifyAsync(
        OspfMigrationPlan plan,
        OspfV7Inventory inventory)
    {
        FakeOps ops = new(inventory, plan);
        return await new OspfV7MigrationVerifier(ops, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
    }

    /// <summary>
    /// Builds a dual-management-prefix Valskerm-shaped plan (two backhauls, loopback, consolidated site).
    /// When <paramref name="logicalNames"/> is true, uses pre-rename logical area/instance names for remap tests.
    /// </summary>
    /// <param name="logicalNames">
    /// True for logical names (backbone/default); false for live *-v2 names used by most verify scenarios.
    /// </param>
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

    /// <summary>
    /// Shallow-clones a plan while replacing <see cref="OspfMigrationPlan.SupersededTemplates"/>.
    /// </summary>
    /// <param name="plan">Source plan whose non-superseded fields are copied.</param>
    /// <param name="superseded">Superseded-template provenance list for the clone.</param>
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

    /// <summary>
    /// Builds a complete v7 inventory that exactly covers the planned instance, areas, ranges, and templates.
    /// </summary>
    /// <param name="plan">Plan whose target names and template matchers seed the inventory.</param>
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

    /// <summary>
    /// Returns a copy of <paramref name="source"/> with <paramref name="extra"/> appended to Templates.
    /// </summary>
    /// <param name="source">Inventory to clone.</param>
    /// <param name="extra">Additional interface template row under test.</param>
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

    /// <summary>
    /// Cleanup-classifier inventory row matching the planned backhaul at <paramref name="index"/>.
    /// </summary>
    /// <param name="plan">Plan supplying backhaul matchers.</param>
    /// <param name="index">Zero-based index into <see cref="OspfMigrationPlan.ActiveBackhauls"/>.</param>
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

    /// <summary>
    /// Cleanup-classifier inventory row matching the planned passive loopback.
    /// </summary>
    /// <param name="plan">Plan supplying loopback matchers.</param>
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

    /// <summary>
    /// Cleanup-classifier inventory row matching the planned consolidated site template.
    /// </summary>
    /// <param name="plan">Plan supplying site-network matchers.</param>
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

    /// <summary>
    /// Minimal <see cref="IOspfMikrotikOperations"/> for verify-only tests: returns the seeded inventory and
    /// fixed IP addresses covering the plan's backhauls, loopback, and dual management prefixes.
    /// Mutation and capture APIs are no-ops or throw <see cref="NotSupportedException"/>; this fake does not
    /// model live RouterOS writes or management-plane baselines.
    /// </summary>
    private sealed class FakeOps(OspfV7Inventory inventory, OspfMigrationPlan plan) : IOspfMikrotikOperations
    {
        /// <summary>
        /// Returns the seeded v7 inventory regardless of connection.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(inventory);

        /// <summary>
        /// Returns fixed IP rows for the plan's two backhauls, loopback, and Bridge-VLAN-99 dual mgmt prefixes.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
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

        /// <summary>
        /// Reports zero routing filters so filter leftover checks cannot fail unrelated to these scenarios.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="cancellationToken">Cancellation token (ignored).</param>
        public Task<int> CountRoutingFiltersAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// Always throws: live capture is outside the verify-only contract of this fake.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="request">Unused capture request.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task<OspfCaptureSnapshot> CaptureAsync(
            ITikConnection connection,
            OspfCaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op: filter removal is not exercised by these verify scenarios.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task RemoveAllRoutingFiltersAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: redistribution clearing is not under test here.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="instanceName">Unused instance name.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task ClearOspfRedistributionAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: instance filter-reference clearing is not under test here.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="instanceName">Unused instance name.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task ClearOspfInstanceFilterReferencesAsync(
            ITikConnection connection,
            string instanceName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: area ensure is assumed already satisfied by the seeded inventory.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="instanceName">Unused.</param>
        /// <param name="area">Unused planned area.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task EnsureOspfAreaAsync(
            ITikConnection connection,
            string instanceName,
            OspfPlanArea area,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: area-range ensure is assumed already satisfied by the seeded inventory.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="range">Unused planned range.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task EnsureOspfAreaRangeAsync(
            ITikConnection connection,
            OspfPlanAreaRange range,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: backhaul ensure is not invoked on the verify path under test.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="template">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task EnsureBackhaulTemplateAsync(
            ITikConnection connection,
            OspfPlanBackhaulTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: loopback ensure is not invoked on the verify path under test.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="template">Unused.</param>
        /// <param name="protectedBackhauls">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task EnsureLoopbackTemplateAsync(
            ITikConnection connection,
            OspfPlanLoopbackTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: site-network ensure is not invoked on the verify path under test.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="template">Unused.</param>
        /// <param name="protectedBackhauls">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task EnsureSiteNetworkTemplateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: post-ensure site assertion is assumed to pass for these scenarios.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="template">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task AssertSiteNetworkTemplateStateAsync(
            ITikConnection connection,
            OspfPlanSiteNetworkTemplate template,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: obsolete-template removal is outside verify-only coverage.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="migrationPlan">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task RemoveObsoleteTemplatesAsync(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Always throws: management-plane baseline capture is not modeled by this fake.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="migrationPlan">Unused.</param>
        /// <param name="managementHost">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            string managementHost,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op: backbone activity assertion is treated as satisfied.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="migrationPlan">Unused.</param>
        /// <param name="baseline">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        /// <param name="requireFleetCost">Unused fleet-cost flag.</param>
        public Task AssertBackboneStillActiveAsync(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            OspfManagementPlaneBaseline baseline,
            CancellationToken cancellationToken = default,
            bool requireFleetCost = true) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: instance rename/rebind is not exercised by these verify scenarios.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="currentName">Unused.</param>
        /// <param name="targetName">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task EnsureOspfInstanceNameAsync(
            ITikConnection connection,
            string currentName,
            string targetName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: unused routing-id cleanup is not under test here.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="routingIdName">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task TryRemoveUnusedRoutingIdAsync(
            ITikConnection connection,
            string? routingIdName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns an empty neighbour list so neighbour-based checks stay out of scope.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        /// <summary>
        /// No-op: OSPFv3 removal is not invoked on the verify path under test.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="migrationPlan">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task RemoveOspfV3Async(
            ITikConnection connection,
            OspfMigrationPlan migrationPlan,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: router-id set is not exercised by these scenarios.
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="instanceName">Unused.</param>
        /// <param name="routerId">Unused.</param>
        /// <param name="cancellationToken">Unused.</param>
        public Task SetInstanceRouterIdAsync(
            ITikConnection connection,
            string instanceName,
            string routerId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
