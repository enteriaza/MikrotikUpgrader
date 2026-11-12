// -----------------------------------------------------------------------------
// File: OspfRoutingFilterMenuCompatibilityTests.cs
//
// Description:
// Non-live coverage for version-specific routing-filter menus used by OSPF
// migration.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
    /// <summary>
    /// Scenario: clear OSPFv2 instance in/out-filter references on a fake connection, then run the v7 verifier.
    /// Expected: unset succeeds and verification passes with filter counts cleared in the fake inventory path.
    /// Invariant: menu-compatible filter clear still leaves instance filter references unset for verification.
    /// </summary>
public sealed class OspfRoutingFilterMenuCompatibilityTests
{
    /// <summary>
    /// Scenario: <c>RemoveAllRoutingFiltersAsync</c> runs against a RouterOS 6.49.23 fake holding one <c>ospf-out</c> filter in <c>/routing/filter</c>.
    /// Expected: the v6 filter print and remove commands are issued; no v7 filter-rule or select-rule commands are issued; num-list and community-list menus are untouched.
    /// Invariant: v6 routers are only ever cleaned via the legacy filter menu.
    /// </summary>
    [Fact]
    public async Task V6_Remove_UsesOnlyLegacyFilterPath()
    {
        TikFakeConnection fake = CreateVersionedFake("6.49.23")
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V6FilterPrint,
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*10",
                        ["chain"] = "ospf-out",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V6FilterRemove,
                static _ => [new TikFakeDoneSentence()]);

        CommandRecordingConnection recording = new(fake);
        await CreateOps().RemoveAllRoutingFiltersAsync(recording, CancellationToken.None);

        Assert.Contains(OspfRoutingFilterMenus.V6FilterPrint, recording.Commands);
        Assert.Contains(OspfRoutingFilterMenus.V6FilterRemove, recording.Commands);
        Assert.DoesNotContain(OspfRoutingFilterMenus.V7FilterRulePrint, recording.Commands);
        Assert.DoesNotContain(OspfRoutingFilterMenus.V7FilterRuleRemove, recording.Commands);
        Assert.DoesNotContain(OspfRoutingFilterMenus.V7FilterSelectRulePrint, recording.Commands);
        AssertNeverTouchesSupportingCatalogues(recording);
    }

    /// <summary>
    /// Scenario: <c>RemoveAllRoutingFiltersAsync</c> runs against a RouterOS 7.23.8 fake holding one <c>ospf-in</c> rule in <c>/routing/filter/rule</c>.
    /// Expected: the v7 rule print and remove commands are issued; the v6 filter print and remove are never issued; num-list and community-list menus are untouched.
    /// Invariant: v7 routers never touch the legacy <c>/routing/filter</c> menu, which does not exist there.
    /// </summary>
    [Fact]
    public async Task V7_Remove_UsesFilterRulePath_NeverLegacyFilterPrint()
    {
        TikFakeConnection fake = CreateVersionedFake("7.23.8")
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterRulePrint,
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*r1",
                        ["chain"] = "ospf-in",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterRuleRemove,
                static _ => [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterSelectRulePrint,
                [new TikFakeDoneSentence()]);

        CommandRecordingConnection recording = new(fake);
        await CreateOps().RemoveAllRoutingFiltersAsync(recording, CancellationToken.None);

        Assert.Contains(OspfRoutingFilterMenus.V7FilterRulePrint, recording.Commands);
        Assert.Contains(OspfRoutingFilterMenus.V7FilterRuleRemove, recording.Commands);
        Assert.DoesNotContain(OspfRoutingFilterMenus.V6FilterPrint, recording.Commands);
        Assert.DoesNotContain(OspfRoutingFilterMenus.V6FilterRemove, recording.Commands);
        AssertNeverTouchesSupportingCatalogues(recording);
    }

    /// <summary>
    /// Scenario: a RouterOS 7.23.8 fake reports no filter rules and no select rules.
    /// Expected: removal completes, the filter count reads 0, no v6 print is issued, and no <c>/remove</c> command is sent.
    /// Invariant: an already-clean router is a no-op rather than an error.
    /// </summary>
    [Fact]
    public async Task V7_EmptyFilterInventory_Succeeds()
    {
        TikFakeConnection fake = CreateVersionedFake("7.23.8")
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterRulePrint,
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterSelectRulePrint,
                [new TikFakeDoneSentence()]);

        CommandRecordingConnection recording = new(fake);
        await CreateOps().RemoveAllRoutingFiltersAsync(recording, CancellationToken.None);

        int count = await CreateOps().CountRoutingFiltersAsync(recording, CancellationToken.None);
        Assert.Equal(0, count);
        Assert.DoesNotContain(OspfRoutingFilterMenus.V6FilterPrint, recording.Commands);
        Assert.DoesNotContain(recording.Commands, static c => c.EndsWith("/remove", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: a RouterOS 7.23.8 fake reports two filter rules (<c>*a</c> in chain c1, <c>*b</c> in chain c2).
    /// Expected: the captured remove commands carry exactly the ids <c>*a</c> and <c>*b</c>, in order.
    /// Invariant: rules are removed one by one by their <c>.id</c>, never via a bulk or wildcard remove.
    /// </summary>
    [Fact]
    public async Task V7_ExistingRules_AreRemovedByExactId()
    {
        TikFakeConnection fake = CreateVersionedFake("7.23.8")
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterRulePrint,
                [
                    new TikFakeReSentence(new Dictionary<string, string> { [".id"] = "*a", ["chain"] = "c1" }),
                    new TikFakeReSentence(new Dictionary<string, string> { [".id"] = "*b", ["chain"] = "c2" }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterRuleRemove,
                static _ => [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterSelectRulePrint,
                [new TikFakeDoneSentence()]);

        RemoveIdCapturingConnection capturing = new(fake);
        await CreateOps().RemoveAllRoutingFiltersAsync(capturing, CancellationToken.None);

        Assert.Equal(["*a", "*b"], capturing.RemovedIds);
    }

    /// <summary>
    /// Scenario: the reconciler applies a valid plan to a v7 connection whose routing-filter removal throws <c>filter-remove-failed</c>.
    /// Expected: <c>ApplyAsync</c> throws <c>InvalidOperationException</c> mentioning <c>filter-remove-failed</c>, and the redistribution-clear step is never reached.
    /// Invariant: a failed filter removal aborts the migration (fail closed) instead of continuing with stale filters.
    /// </summary>
    [Fact]
    public async Task V7_RemovalFailure_PreventsOspfMigrationApplySuccess()
    {
        ThrowingFilterOps ops = new();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
                .ApplyAsync(
                    FakeMikrotikConnections.CreateFullyPopulated(version: "7.23.8"),
                    BuildValidPlan(),
                    managementHost: "10.0.0.1",
                    CancellationToken.None));

        Assert.Contains("filter-remove-failed", ex.Message, StringComparison.Ordinal);
        Assert.False(ops.ReachedRedistribution);
    }

    /// <summary>
    /// Scenario: the verifier runs with an ops fake that still reports 2 routing filters, over an otherwise matching inventory.
    /// Expected: verification fails with an error containing <c>Routing filters remain</c>.
    /// Invariant: post-apply verification does not pass while routing filters are still present.
    /// </summary>
    [Fact]
    public async Task V7_ReadBack_DetectsRemainingRules()
    {
        CountingOps ops = new() { FilterCount = 2, Inventory = BuildVerifierInventory() };
        OspfVerificationResult result = await new OspfV7MigrationVerifier(
                ops,
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(
                FakeMikrotikConnections.CreateFullyPopulated(version: "7.23.8"),
                BuildValidPlan(),
                CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Routing filters remain", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: a RouterOS 7.23.8 fake traps the required <c>/routing/filter/rule/print</c> command with "no such command".
    /// Expected: <c>RemoveAllRoutingFiltersAsync</c> throws <c>InvalidOperationException</c> whose message names the rule print menu.
    /// Invariant: a missing mandatory filter menu is an error, not treated as "no filters".
    /// </summary>
    [Fact]
    public async Task V7_UnsupportedRequiredRuleMenu_FailsClosed()
    {
        TikFakeConnection fake = CreateVersionedFake("7.23.8")
            .WithTrap(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterRulePrint,
                "no such command");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveAllRoutingFiltersAsync(new CommandRecordingConnection(fake), CancellationToken.None));

        Assert.Contains(OspfRoutingFilterMenus.V7FilterRulePrint, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: <c>/system/resource/print</c> returns the unparseable version "not-a-version".
    /// Expected: <c>CountRoutingFiltersAsync</c> throws <c>InvalidOperationException</c>.
    /// Invariant: without a recognisable RouterOS version the code refuses to pick a filter menu.
    /// </summary>
    [Fact]
    public async Task AmbiguousRouterOsVersion_FailsClosed()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["version"] = "not-a-version" }),
                    new TikFakeDoneSentence(),
                ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().CountRoutingFiltersAsync(new CommandRecordingConnection(fake), CancellationToken.None));
    }

    /// <summary>
    /// Scenario: a RouterOS 7.23.8 fake has no filter rules and traps the optional select-rule print with "no such command".
    /// Expected: removal succeeds; both rule and select-rule prints were attempted; num-list and community-list menus are never touched.
    /// Invariant: the optional select-rule menu may be absent without failing, and supporting catalogues are never modified.
    /// </summary>
    [Fact]
    public async Task V7_UnsupportedSelectRuleMenu_IsSkipped_WithoutTouchingCatalogues()
    {
        TikFakeConnection fake = CreateVersionedFake("7.23.8")
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterRulePrint,
                [new TikFakeDoneSentence()])
            .WithTrap(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterSelectRulePrint,
                "no such command");

        CommandRecordingConnection recording = new(fake);
        await CreateOps().RemoveAllRoutingFiltersAsync(recording, CancellationToken.None);

        Assert.Contains(OspfRoutingFilterMenus.V7FilterRulePrint, recording.Commands);
        Assert.Contains(OspfRoutingFilterMenus.V7FilterSelectRulePrint, recording.Commands);
        AssertNeverTouchesSupportingCatalogues(recording);
    }

    /// <summary>
    /// Scenario: <c>CaptureAsync</c> runs against a RouterOS 7.23.8 fake with empty OSPF menus.
    /// Expected: the v7 filter-rule print is issued and the v6 filter print is not.
    /// Invariant: capture uses the version-appropriate filter menu.
    /// </summary>
    [Fact]
    public async Task V7_Capture_UsesRulePath_NotLegacyFilter()
    {
        TikFakeConnection fake = CreateVersionedFake("7.23.8")
            .WithResponse(
                static rows => rows.First() == "/ip/address/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterRulePrint,
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterSelectRulePrint,
                [new TikFakeDoneSentence()])
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
                [new TikFakeDoneSentence()]);

        CommandRecordingConnection recording = new(fake);
        await CreateOps().CaptureAsync(
            recording,
            new OspfCaptureRequest
            {
                ImportId = Guid.NewGuid(),
                JobId = "job",
                RouterIp = IPAddress.Parse("198.18.240.11"),
                Identity = "BR01.POP011.016.Tommy",
                RouterOsVersion = "7.23.8",
                Architecture = "mipsbe",
                SourceKind = OspfCaptureSourceKind.RouterOsV7,
            },
            CancellationToken.None);

        Assert.Contains(OspfRoutingFilterMenus.V7FilterRulePrint, recording.Commands);
        Assert.DoesNotContain(OspfRoutingFilterMenus.V6FilterPrint, recording.Commands);
    }

    /// <summary>
    /// Scenario: <c>CaptureAsync</c> runs against a RouterOS 6.49.23 fake with empty OSPF menus (OSPFv3 menus trap as missing).
    /// Expected: the v6 filter print is issued and the v7 filter-rule print is not.
    /// Invariant: v6 capture never queries v7-only menus.
    /// </summary>
    [Fact]
    public async Task V6_Capture_UsesLegacyFilterPath_NotV7Rule()
    {
        TikFakeConnection fake = CreateVersionedFake("6.49.23")
            .WithResponse(
                static rows => rows.First() == "/ip/address/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V6FilterPrint,
                [new TikFakeDoneSentence()])
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
                static rows => rows.First() == "/routing/ospf/network/print",
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface/print",
                [new TikFakeDoneSentence()])
            .WithTrap(
                static rows => rows.First().StartsWith("/routing/ospf-v3/", StringComparison.Ordinal),
                "no such command");

        CommandRecordingConnection recording = new(fake);
        await CreateOps().CaptureAsync(
            recording,
            new OspfCaptureRequest
            {
                ImportId = Guid.NewGuid(),
                JobId = "job",
                RouterIp = IPAddress.Parse("198.18.240.11"),
                Identity = "BR01.POP011.016.Tommy",
                RouterOsVersion = "6.49.23",
                Architecture = "mipsbe",
                SourceKind = OspfCaptureSourceKind.RouterOsV6,
            },
            CancellationToken.None);

        Assert.Contains(OspfRoutingFilterMenus.V6FilterPrint, recording.Commands);
        Assert.DoesNotContain(OspfRoutingFilterMenus.V7FilterRulePrint, recording.Commands);
    }

    /// <summary>
    /// Scenario: <c>ReadV7InventoryAsync</c> runs against a RouterOS 7.23.8 fake with empty OSPF and filter menus.
    /// Expected: the v7 filter-rule print is issued, the v6 filter print is not, and num-list and community-list menus are untouched.
    /// Invariant: reading a v7 inventory stays within v7 menus and read-only on supporting catalogues.
    /// </summary>
    [Fact]
    public async Task V7_ReadInventory_NeverInvokesLegacyFilterPrint()
    {
        TikFakeConnection fake = CreateVersionedFake("7.23.8")
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
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterRulePrint,
                [new TikFakeDoneSentence()])
            .WithResponse(
                static rows => rows.First() == OspfRoutingFilterMenus.V7FilterSelectRulePrint,
                [new TikFakeDoneSentence()]);

        CommandRecordingConnection recording = new(fake);
        await CreateOps().ReadV7InventoryAsync(recording, CancellationToken.None);

        Assert.Contains(OspfRoutingFilterMenus.V7FilterRulePrint, recording.Commands);
        Assert.DoesNotContain(OspfRoutingFilterMenus.V6FilterPrint, recording.Commands);
        AssertNeverTouchesSupportingCatalogues(recording);
    }

    /// <summary>
    /// Scenario: clear OSPFv2 instance filter references, then run the v7 verifier.
    /// Expected: unset succeeds and verification passes.
    /// Invariant: menu-compatible filter clear leaves instance filter references unset.
    /// </summary>
    [Fact]
    public async Task InstanceFilterReferences_StillUnsetAndVerified()
    {
        // Retain existing OSPFv2 in/out-filter clear + verifier behaviour.
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/instance/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*1",
                        ["name"] = "default",
                        ["version"] = "2",
                        ["in-filter"] = "ospf-in",
                        ["out-filter-chain"] = "ospf-out",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/instance/unset",
                static _ => [new TikFakeDoneSentence()]);

        await CreateOps().ClearOspfInstanceFilterReferencesAsync(fake, "default", CancellationToken.None);

        CountingOps ops = new()
        {
            FilterCount = 0,
            Inventory = BuildVerifierInventory(),
        };

        OspfVerificationResult verified = await new OspfV7MigrationVerifier(
                ops,
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), BuildValidPlan(), CancellationToken.None);

        Assert.True(verified.Succeeded, verified.Detail);
    }

    /// <summary>
    /// Creates the real <c>OspfMikrotikOperations</c> with a null logger.
    /// </summary>
    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    /// <summary>
    /// Builds a fully populated fake device that reports the given RouterOS <paramref
    /// name="version"/> with fixed identity, architecture and board. The version is the only
    /// variable, because it selects between the v6 <c>/routing/filter</c> and v7
    /// <c>/routing/filter/rule</c> menus.
    /// </summary>
    private static TikFakeConnection CreateVersionedFake(string version) =>
        FakeMikrotikConnections.CreateFullyPopulated(
            identity: "BR01.POP011.016.Tommy",
            version: version,
            architecture: "mipsbe",
            boardName: "hEX PoE");

    /// <summary>
    /// Asserts that no recorded command touched the routing filter num-list or community-list menus.
    /// </summary>
    private static void AssertNeverTouchesSupportingCatalogues(CommandRecordingConnection recording)
    {
        Assert.DoesNotContain(recording.Commands, static c =>
            c.StartsWith("/routing/filter/num-list", StringComparison.Ordinal)
            || c.StartsWith("/routing/filter/community-list", StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds a valid ROS6-sourced plan via the real planner (asserting success) so reconciler and
    /// verifier tests only fail because of the routing-filter behaviour under test.
    /// </summary>
    private static OspfMigrationPlan BuildValidPlan()
    {
        OspfCaptureSnapshot snap = new()
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

        OspfPlanValidationResult built = OspfMigrationPlanner.Build(snap);
        Assert.True(built.Succeeded, string.Join("; ", built.Plan.ValidationErrors));
        return built.Plan;
    }

    /// <summary>
    /// Builds a ROS7 inventory matching <see cref="BuildValidPlan"/> with no in/out-filter
    /// references, so the verifier fails only when routing filters are reported as still present.
    /// </summary>
    private static OspfV7Inventory BuildVerifierInventory()
    {
        OspfMigrationPlan plan = BuildValidPlan();
        OspfV7Inventory inv = OspfTestBaselines.InventoryMatchingPlan(plan);
        return new OspfV7Inventory
        {
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*1",
                    Name = plan.TargetInstanceName,
                    Version = "2",
                    RouterId = plan.RouterId,
                    InFilter = null,
                    OutFilter = null,
                    RedistributeProperties = new Dictionary<string, string?>(),
                },
            ],
            Areas = inv.Areas.Select(a => new OspfAreaRow
            {
                Id = a.Id,
                Name = a.Name,
                AreaId = a.AreaId,
                Instance = plan.TargetInstanceName,
                Type = a.Type,
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
                    Passive = false,
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
    /// Connection wrapper that captures v7 filter-rule remove commands and exposes the de-duplicated rule ids
    /// removed, in order.
    /// </summary>
    private sealed class RemoveIdCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        /// <summary>
        /// De-duplicated <c>.id</c> values materialised from <see cref="_removeCommands"/>,
        /// preserving the order in which removals were issued.
        /// </summary>
        private readonly List<string> _removedIds = [];

        /// <summary>
        /// v7 filter-rule <c>remove</c> commands captured at creation time; their <c>.id</c>
        /// parameters are only populated after <c>CreateCommand</c> returns.
        /// </summary>
        private readonly List<ITikCommand> _removeCommands = [];

        /// <summary>
        /// Rule ids passed to remove commands, in call order. Reading is deferred until after
        /// <c>AddParameter</c> so tests can prove each rule is removed by its exact <c>.id</c> and
        /// not via a bulk or wildcard removal.
        /// </summary>
        public IReadOnlyList<string> RemovedIds
        {
            get
            {
                // Materialize .id after AddParameter calls completed.
                foreach (ITikCommand cmd in _removeCommands)
                {
                    string? id = cmd.Parameters.FirstOrDefault(p => p.Name == ".id")?.Value;
                    if (!string.IsNullOrWhiteSpace(id) && !_removedIds.Contains(id))
                    {
                        _removedIds.Add(id);
                    }
                }

                return _removedIds;
            }
        }

        /// <summary>
        /// Captures v7 filter-rule remove commands for later <c>.id</c> inspection, then returns
        /// the inner command unchanged.
        /// </summary>
        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            if (string.Equals(commandText, OspfRoutingFilterMenus.V7FilterRuleRemove, StringComparison.Ordinal))
            {
                _removeCommands.Add(cmd);
            }

            return cmd;
        }

        /// <summary>
        /// Overload with an explicit parameter format; captures identically so the removal cannot
        /// escape tracking via this overload.
        /// </summary>
        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            if (string.Equals(commandText, OspfRoutingFilterMenus.V7FilterRuleRemove, StringComparison.Ordinal))
            {
                _removeCommands.Add(cmd);
            }

            return cmd;
        }
    }

    /// <summary>
    /// Ops fake whose routing-filter removal throws and which records whether the reconciler wrongly continued to
    /// redistribution clearing.
    /// </summary>
    private sealed class ThrowingFilterOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Set when the reconciler proceeds to the redistribution clear. It must stay false after a
        /// filter-removal failure, proving the migration fails closed instead of leaving stale
        /// filters while moving on.
        /// </summary>
        public bool ReachedRedistribution { get; private set; }

        /// <summary>
        /// Throws <c>filter-remove-failed</c> to simulate RouterOS refusing to delete the
        /// routing-filter rules.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("filter-remove-failed");

        /// <summary>
        /// Records that the reconciler progressed past filter removal; reaching this after the
        /// injected failure would be a fail-open bug.
        /// </summary>
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            ReachedRedistribution = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op: clearing instance in-filter/out-filter references is not under test in this
        /// scenario.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Always throws <see cref="NotSupportedException"/>: this fake only supports the v7
        /// reconcile/verify path, never a live capture.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// Reports zero routing filters so the "routing filters remain" read-back check cannot fail
        /// for reasons unrelated to this scenario.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// No-op: area reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: area-range reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: the backhaul-template ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: the loopback-template ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: the site-matcher ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: the post-ensure site-template read-back is assumed to pass in this scenario.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns a minimal management-plane baseline so backbone guards stay inert; the scenario
        /// is about filter removal only.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Minimal(managementHost));

        /// <summary>
        /// No-op backbone guard: this fake never reports a dropped adjacency or a changed
        /// management route.
        /// </summary>
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Returns no neighbours; the adjacency guard is not exercised by this scenario.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);


        /// <summary>
        /// No-op: legacy interface-template cleanup is not under test in this scenario.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: OSPFv3 removal is not under test in this scenario.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns the clean verifier inventory so no later stage fails for unrelated inventory
        /// reasons.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildVerifierInventory());

        /// <summary>
        /// No-op: the router-id in the fake inventory is already correct, so no router-id write is
        /// modelled.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns a single <c>10.255.0.1/32</c> Loopback address so loopback-derived router-id
        /// validation sees one consistent /32.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.255.0.1", "lo"));
    }

    /// <summary>
    /// Ops fake that reports a configurable remaining routing filter count and returns an injected inventory, to
    /// exercise the verifier's filters-remain check.
    /// </summary>
    private sealed class CountingOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Number of routing filters this fake reports as still present; a non-zero value simulates
        /// an incomplete removal for the verifier read-back.
        /// </summary>
        public required int FilterCount { get; init; }

        /// <summary>
        /// Inventory returned to the verifier, otherwise consistent with the plan so only the
        /// filter count can fail verification.
        /// </summary>
        public required OspfV7Inventory Inventory { get; init; }

        /// <summary>
        /// Returns the configured <see cref="FilterCount"/> to exercise the verifier's "Routing
        /// filters remain" check.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(FilterCount);

        /// <summary>
        /// Returns the injected <see cref="Inventory"/>.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        /// <summary>
        /// No-op: routing-filter removal is outside this scenario, so the fake completes without
        /// recording or failing.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: redistribution clearing is not under test in this scenario.
        /// </summary>
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: clearing instance in-filter/out-filter references is not under test in this
        /// scenario.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Always throws <see cref="NotSupportedException"/>: this fake only supports the v7
        /// reconcile/verify path, never a live capture.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op: area reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: area-range reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: the backhaul-template ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: the loopback-template ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: the site-matcher ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: the post-ensure site-template read-back is assumed to pass in this scenario.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns a minimal management-plane baseline so backbone guards stay inert in
        /// verifier-focused tests.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Minimal(managementHost));

        /// <summary>
        /// No-op backbone guard: this fake never reports a dropped adjacency or a changed
        /// management route.
        /// </summary>
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Returns no neighbours; the adjacency guard is not exercised by this scenario.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);


        /// <summary>
        /// No-op: legacy interface-template cleanup is not under test in this scenario.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: OSPFv3 removal is not under test in this scenario.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: the router-id in the fake inventory is already correct, so no router-id write is
        /// modelled.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns a single <c>10.255.0.1/32</c> Loopback address so loopback-derived router-id
        /// validation sees one consistent /32.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.255.0.1", "lo"));
    }
}
