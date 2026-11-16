// -----------------------------------------------------------------------------
// File: OspfValskermBackhaulVerifyTests.cs
//
// Description:
// Regression for BR01.POP003.016.Valskerm false-positive backhaul verification:
// live templates remain on area=backbone while TargetBackboneAreaName is
// backbone-v2 after RemapPlan area-id fallback binding.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Production Valskerm backhaul post-mutation verification regressions.
/// </summary>
public sealed class OspfValskermBackhaulVerifyTests
{
    private const string ValskermSfpp =
        "sfpp-01_BH01.Valskerm-RustenburgKantoor_sfpp-02 [monitor:1200]";

    private const string ValskermEth =
        "eth-01_BH02.Valskerm-Shylock [monitor:980]";

    private const string SimilarButDistinctSfpp =
        "sfpp-01_BH01.Valskerm-RustenburgKantoor_sfpp-02 [monitor:1201]";

    /// <summary>
    /// Scenario: live-remapped plan keeps AreaName=backbone; live export has the exact Valskerm
    /// sfpp template (spaces, brackets, monitor:1200) on area=backbone with 10.16.248.8/30.
    /// Expected: assert accepts both backhauls.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_LiveRemappedBackbone_ValskermExactMonitorName_Passes()
    {
        TikFakeConnection fake = ValskermLiveTemplatesConnection();
        await CreateOps().AssertBackboneStillActiveAsync(
            fake,
            LiveRemappedValskermPlan(),
            TemplateOnlyBaseline(),
            CancellationToken.None,
            requireFleetCost: false);
    }

    /// <summary>
    /// Scenario: unmapped logical plan (AreaName=backbone, Target=backbone-v2) against live rows
    /// already on backbone-v2 with the exact Valskerm interface names.
    /// Expected: post-verify path still accepts.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_LogicalPlan_LiveBackboneV2_ValskermNames_Passes()
    {
        TikFakeConnection fake = ValskermLiveTemplatesConnection(area: "backbone-v2");
        await CreateOps().AssertBackboneStillActiveAsync(
            fake,
            LogicalValskermPlan(),
            TemplateOnlyBaseline(),
            CancellationToken.None,
            requireFleetCost: false);
    }

    /// <summary>
    /// Scenario: API returns networks via legacy <c>network</c> spelling for the Valskerm sfpp row.
    /// Expected: mapping/assert accepts the row.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_NetworkAliasProperty_Passes()
    {
        List<Dictionary<string, string>> rows = ValskermTemplateRows(area: "backbone");
        rows[0].Remove("networks");
        rows[0]["network"] = "10.16.248.8/30";

        TikFakeConnection fake = ConnectionFromTemplates(rows);
        await CreateOps().AssertBackboneStillActiveAsync(
            fake,
            LiveRemappedValskermPlan(),
            TemplateOnlyBaseline(),
            CancellationToken.None,
            requireFleetCost: false);
    }

    /// <summary>
    /// Scenario: sfpp template genuinely absent; only the second backhaul remains.
    /// Expected: missing after mutation naming the sfpp interface.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_MissingSfpp_FailsClosed()
    {
        List<Dictionary<string, string>> rows = ValskermTemplateRows(area: "backbone");
        rows.RemoveAt(0);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                ConnectionFromTemplates(rows),
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ValskermSfpp, ex.Message, StringComparison.Ordinal);
        Assert.Contains("10.16.248.8/30", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: sfpp template disabled=yes.
    /// Expected: disabled failure.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_Disabled_FailsClosed()
    {
        List<Dictionary<string, string>> rows = ValskermTemplateRows(area: "backbone");
        rows[0]["disabled"] = "yes";

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                ConnectionFromTemplates(rows),
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: malformed disabled value on the matching template.
    /// Expected: unknown/malformed disabled fail-closed.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_MalformedDisabled_FailsClosed()
    {
        List<Dictionary<string, string>> rows = ValskermTemplateRows(area: "backbone");
        rows[0]["disabled"] = "maybe";

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                ConnectionFromTemplates(rows),
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));

        Assert.Contains("unknown/malformed disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: wrong network on the sfpp template.
    /// Expected: missing after mutation.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_WrongNetwork_FailsClosed()
    {
        List<Dictionary<string, string>> rows = ValskermTemplateRows(area: "backbone");
        rows[0]["networks"] = "10.16.248.0/30";

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                ConnectionFromTemplates(rows),
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: similar but distinct monitor suffix must not satisfy the planned identity.
    /// Expected: missing after mutation.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_SimilarInterfaceName_DoesNotMatch()
    {
        List<Dictionary<string, string>> rows = ValskermTemplateRows(area: "backbone");
        rows[0]["interfaces"] = SimilarButDistinctSfpp;

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                ConnectionFromTemplates(rows),
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: template moved to site area.
    /// Expected: missing after mutation (wrong area).
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_WrongArea_FailsClosed()
    {
        List<Dictionary<string, string>> rows = ValskermTemplateRows(area: "backbone");
        rows[0]["area"] = "BR01.POP003.016.Valskerm";

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                ConnectionFromTemplates(rows),
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: empty interface-template print (incomplete / empty post-mutation read).
    /// Expected: missing after mutation; never treated as success.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_EmptyInventory_FailsClosed()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => [new TikFakeDoneSentence()]);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                fake,
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Observed templates: <none>", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: API print throws (collection failure).
    /// Expected: exception propagates; not interpreted as empty success.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_ApiCollectionFailure_DoesNotPass()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ => throw new InvalidOperationException("simulated interface-template print failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                fake,
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));
    }

    /// <summary>
    /// Scenario: two identical matching templates for the same backhaul identity.
    /// Expected: ambiguous fail-closed.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_AmbiguousDuplicate_FailsClosed()
    {
        List<Dictionary<string, string>> rows = ValskermTemplateRows(area: "backbone");
        Dictionary<string, string> dup = new(rows[0]) { [".id"] = "*dup" };
        rows.Insert(1, dup);

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                ConnectionFromTemplates(rows),
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));

        Assert.Contains("Ambiguous backhaul template match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: wrong interface with matching network.
    /// Expected: missing after mutation.
    /// </summary>
    [Fact]
    public async Task AssertBackbone_Valskerm_WrongInterface_FailsClosed()
    {
        List<Dictionary<string, string>> rows = ValskermTemplateRows(area: "backbone");
        rows[0]["interfaces"] = "eth-99_WRONG";

        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().AssertBackboneStillActiveAsync(
                ConnectionFromTemplates(rows),
                LiveRemappedValskermPlan(),
                TemplateOnlyBaseline(),
                CancellationToken.None,
                requireFleetCost: false));

        Assert.Contains("missing after mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: forcing TargetBackboneAreaName lookup would miss live area=backbone; remapped plan
    /// must still pass (the production false-positive).
    /// </summary>
    [Fact]
    public async Task AssertBackbone_RemappedPlanTargetBackboneV2_LiveAreaBackbone_DoesNotFalsePositive()
    {
        OspfMigrationPlan plan = LiveRemappedValskermPlan();
        Assert.Equal("backbone", plan.ActiveBackhauls[0].AreaName);
        Assert.Equal("backbone-v2", plan.TargetBackboneAreaName);
        Assert.Equal("backbone", plan.Areas.Single(a => a.IsBackbone).Name);

        await CreateOps().AssertBackboneStillActiveAsync(
            ValskermLiveTemplatesConnection(area: "backbone"),
            plan,
            TemplateOnlyBaseline(),
            CancellationToken.None,
            requireFleetCost: false);
    }

    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    private static OspfManagementPlaneBaseline TemplateOnlyBaseline() => new()
    {
        ManagementHost = "10.16.0.3",
        BackboneInterface = ValskermSfpp,
        BackboneTemplateId = "*sfpp",
        BackboneArea = "backbone",
        BackboneNetwork = "10.16.248.8/30",
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

    private static TikFakeConnection ValskermLiveTemplatesConnection(string area = "backbone") =>
        ConnectionFromTemplates(ValskermTemplateRows(area));

    private static TikFakeConnection ConnectionFromTemplates(IReadOnlyList<Dictionary<string, string>> templates) =>
        new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ =>
                [
                    .. templates.Select(static t => new TikFakeReSentence(new Dictionary<string, string>(t))),
                    new TikFakeDoneSentence(),
                ]);

    private static List<Dictionary<string, string>> ValskermTemplateRows(string area) =>
    [
        new()
        {
            [".id"] = "*sfpp",
            ["area"] = area,
            ["auth-id"] = "1",
            ["cost"] = "13",
            ["interfaces"] = ValskermSfpp,
            ["networks"] = "10.16.248.8/30",
            ["priority"] = "1",
            ["type"] = "ptp",
            ["use-bfd"] = "no",
            ["disabled"] = "no",
        },
        new()
        {
            [".id"] = "*eth",
            ["area"] = area,
            ["auth-id"] = "1",
            ["cost"] = "11",
            ["interfaces"] = ValskermEth,
            ["networks"] = "10.16.248.12/30",
            ["priority"] = "1",
            ["type"] = "ptp",
            ["use-bfd"] = "no",
            ["disabled"] = "no",
        },
    ];

    /// <summary>
    /// Plan shape after <c>OspfLiveIdentityResolver.RemapPlan</c> when live backbone is still named
    /// <c>backbone</c> (area-id 0.0.0.0 fallback) while Target remains <c>backbone-v2</c>.
    /// </summary>
    private static OspfMigrationPlan LiveRemappedValskermPlan() => new()
    {
        ImportId = Guid.NewGuid(),
        JobId = "job",
        RouterIp = "10.16.0.3",
        Identity = "BR01.POP003.016.Valskerm",
        SourceRouterOsVersion = "7.23.8",
        Architecture = "arm",
        SourceKind = OspfCaptureSourceKind.RouterOsV7,
        InstanceName = "default-v2",
        RouterId = "10.16.0.3",
        TargetInstanceName = "default-v2",
        TargetBackboneAreaName = "backbone-v2",
        TargetSiteAreaName = "BR01.POP003.016.Valskerm",
        LoopbackInterfaceName = "Loopback",
        LoopbackCidr = "10.16.0.3/32",
        DerivedManagementCidr = "10.16.3.0/24",
        Areas =
        [
            new OspfPlanArea
            {
                Name = "backbone", AreaId = "0.0.0.0", TargetType = "default", IsBackbone = true,
            },
            new OspfPlanArea
            {
                Name = "BR01.POP003.016.Valskerm",
                AreaId = "10.16.0.3",
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
                Interface = ValskermSfpp,
                NetworkCidr = "10.16.248.8/30",
                Cost = 10,
                Type = "ptp",
                Passive = false,
                SourceCost = "13",
            },
            new OspfPlanBackhaulTemplate
            {
                AreaName = "backbone",
                Interface = ValskermEth,
                NetworkCidr = "10.16.248.12/30",
                Cost = 10,
                Type = "ptp",
                Passive = false,
                SourceCost = "11",
            },
        ],
        PassiveLoopbacks =
        [
            new OspfPlanLoopbackTemplate
            {
                AreaName = "backbone", Interface = "Loopback", NetworkCidr = "10.16.0.3/32", Passive = true,
            },
        ],
        PassiveSiteNetworks =
        [
            new OspfPlanSiteNetworkTemplate
            {
                AreaName = "BR01.POP003.016.Valskerm", Interfaces = "all", NetworkCidr = "10.16.3.0/24", Passive = true,
            },
        ],
        ValidationErrors = [],
        NormalisationNotes = [],
        ContentHash = "valskerm-backhaul-verify",
    };

    /// <summary>Unmapped logical plan still carrying AreaName=backbone with Target=backbone-v2.</summary>
    private static OspfMigrationPlan LogicalValskermPlan()
    {
        OspfMigrationPlan plan = LiveRemappedValskermPlan();
        // Same shape as remapped for Valskerm ROS7 capture; Target differs from live area name.
        return plan;
    }
}
