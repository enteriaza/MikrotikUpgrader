// -----------------------------------------------------------------------------
// File: OspfInstanceFilterClearTests.cs
//
// Description:
// Tests that OSPF instance in/out filter chains are cleared as required during
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
    /// Scenario: reconciler apply with ops that record the order of instance filter clear vs redistribution clear.
    /// Expected: filter-reference clear is invoked before redistribution clear.
    /// Invariant: OSPFv2 instance filter cleanup precedes redistribution cleanup in the reconcile sequence.
    /// </summary>
public sealed class OspfInstanceFilterClearTests
{
    /// <summary>
    /// Scenario: an OSPFv2 instance with both in-filter ('ospf-in') and out-filter-chain ('ospf-out') set.
    /// Expected: exactly two unsets are issued for *1, one for in-filter and one for out-filter-chain, and none for the bare out-filter property.
    /// Invariant: both filter references are cleared using the RouterOS 7 property names.
    /// </summary>
    [Fact]
    public async Task Clear_UnsetsBothFilterReferences_WhenBothPopulated()
    {
        UnsetCapture capture = await RunClearAsync(
            CreatePrintResponse(
                V2("*1", "default", inFilter: "ospf-in", outFilterChain: "ospf-out")));

        Assert.Equal(2, capture.Unsets.Count);
        Assert.Contains(capture.Unsets, u => u.Id == "*1" && u.Property == "in-filter");
        Assert.Contains(capture.Unsets, u => u.Id == "*1" && u.Property == "out-filter-chain");
        Assert.DoesNotContain(capture.Unsets, u => u.Property == "out-filter");
    }

    /// <summary>
    /// Scenario: only in-filter is populated on the instance.
    /// Expected: a single unset of in-filter on *1.
    /// Invariant: properties that are not set are left untouched.
    /// </summary>
    [Fact]
    public async Task Clear_UnsetsInbound_WhenOnlyInboundPopulated()
    {
        UnsetCapture capture = await RunClearAsync(
            CreatePrintResponse(
                V2("*1", "default", inFilter: "ospf-in", outFilterChain: null)));

        Assert.Single(capture.Unsets);
        Assert.Contains(capture.Unsets, u => u.Id == "*1" && u.Property == "in-filter");
    }

    /// <summary>
    /// Scenario: only out-filter-chain is populated on the instance.
    /// Expected: a single unset of out-filter-chain on *1, never the bare out-filter property.
    /// Invariant: the outbound reference is cleared via out-filter-chain, not the ambiguous out-filter.
    /// </summary>
    [Fact]
    public async Task Clear_UnsetsOutboundChain_WhenOnlyOutboundPopulated()
    {
        UnsetCapture capture = await RunClearAsync(
            CreatePrintResponse(
                V2("*1", "default", inFilter: null, outFilterChain: "ospf-out")));

        Assert.Single(capture.Unsets);
        Assert.Contains(capture.Unsets, u => u.Id == "*1" && u.Property == "out-filter-chain");
        Assert.DoesNotContain(capture.Unsets, u => u.Property == "out-filter");
    }

    /// <summary>
    /// Scenario: the instance has neither in-filter nor out-filter-chain populated.
    /// Expected: no unset commands are sent.
    /// Invariant: already-cleared properties must not be unset, because RouterOS 7 rejects an ambiguous bare out-filter.
    /// </summary>
    [Fact]
    public async Task Clear_SkipsUnset_WhenBothAlreadyUnset()
    {
        UnsetCapture capture = await RunClearAsync(
            CreatePrintResponse(
                V2("*1", "default", inFilter: null, outFilterChain: null)));

        // Already-cleared properties must not be unset (ROS7 rejects ambiguous bare out-filter).
        Assert.Empty(capture.Unsets);
    }

    /// <summary>
    /// Scenario: a v2 instance (*2) and a v3 instance (*3) both carry in/out filter references.
    /// Expected: only the two unsets for *2 are issued; nothing targets *3.
    /// Invariant: clearing filter references only touches the OSPFv2 instance being migrated.
    /// </summary>
    [Fact]
    public async Task Clear_DoesNotModifyOspfV3Instances()
    {
        UnsetCapture capture = await RunClearAsync(
            CreatePrintResponse(
                V2("*2", "default", inFilter: "ospf-in", outFilterChain: "ospf-out"),
                V3("*3", "v3-inst", inFilter: "v3-in", outFilterChain: "v3-out")));

        Assert.Equal(2, capture.Unsets.Count);
        Assert.All(capture.Unsets, u => Assert.Equal("*2", u.Id));
        Assert.DoesNotContain(capture.Unsets, u => u.Id == "*3");
    }

    /// <summary>
    /// Scenario: post-apply inventory still references a filter: in-filter only, out-filter only, or both.
    /// Expected: verification fails and the errors name each stale reference ('in-filter' and/or 'out-filter').
    /// Invariant: a leftover filter reference of either kind is reported as an incomplete migration.
    /// </summary>
    [Theory]
    [InlineData("ospf-in", null)]
    [InlineData(null, "ospf-out")]
    [InlineData("ospf-in", "ospf-out")]
    public async Task Verifier_DetectsEitherStaleFilterReference(string? inFilter, string? outFilter)
    {
        OspfMigrationPlan plan = ValidPlan();
        FakeOps ops = new()
        {
            Inventory = Inventory(inFilter: inFilter, outFilter: outFilter),
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(
                ops,
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        if (!string.IsNullOrWhiteSpace(inFilter))
        {
            Assert.Contains(result.Errors, e => e.Contains("in-filter", StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(outFilter))
        {
            Assert.Contains(result.Errors, e => e.Contains("out-filter", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Scenario: post-apply inventory has no in-filter or out-filter references.
    /// Expected: verification succeeds.
    /// Invariant: a cleanly cleared instance is not reported as drift.
    /// </summary>
    [Fact]
    public async Task Verifier_AcceptsUnsetFilterReferences()
    {
        OspfMigrationPlan plan = ValidPlan();
        FakeOps ops = new() { Inventory = Inventory(inFilter: null, outFilter: null) };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(
                ops,
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
    }

    /// <summary>
    /// Scenario: reconciler apply recording filter-clear versus redistribution-clear order.
    /// Expected: instance filter clear runs before redistribution clear in the recorded sequence.
    /// Invariant: OSPFv2 filter cleanup precedes redistribution cleanup.
    /// </summary>
    [Fact]
    public async Task Reconciler_InvokesFilterClear_BeforeRedistributionClear()
    {
        SequenceOps ops = new();
        OspfMigrationPlan plan = ValidPlan();

        await new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
            .ApplyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, managementHost: "10.0.0.1", CancellationToken.None);

        Assert.Contains("filters", ops.Sequence);
        Assert.Contains("instance-filters", ops.Sequence);
        Assert.Contains("redistribute", ops.Sequence);
        Assert.True(ops.Sequence.IndexOf("filters") < ops.Sequence.IndexOf("instance-filters"));
        Assert.True(ops.Sequence.IndexOf("instance-filters") < ops.Sequence.IndexOf("redistribute"));
        Assert.Equal("default-v2", ops.ClearedInstanceName);
        Assert.Equal("default-v2", ops.RedistributeInstanceName);
        Assert.Equal("default-v2", ops.RouterIdInstanceName);
    }

    /// <summary>
    /// Runs <c>ClearOspfInstanceFilterReferencesAsync</c> for the default instance against a fake print response
    /// and returns the unset commands that were issued.
    /// </summary>
    private static async Task<UnsetCapture> RunClearAsync(IReadOnlyList<ITikSentence> printRows)
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/instance/print",
                printRows.ToList())
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/instance/unset",
                static _ => [new TikFakeDoneSentence()]);

        UnsetCapturingConnection capturing = new(fake);
        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);
        await ops.ClearOspfInstanceFilterReferencesAsync(capturing, "default", CancellationToken.None);
        return new UnsetCapture(capturing.Unsets);
    }

    /// <summary>
    /// Converts instance rows into RouterOS reply sentences followed by a done sentence, for use as a print
    /// response.
    /// </summary>
    private static List<ITikSentence> CreatePrintResponse(params Dictionary<string, string>[] instances)
    {
        List<ITikSentence> rows = [];
        foreach (Dictionary<string, string> instance in instances)
        {
            rows.Add(new TikFakeReSentence(instance));
        }

        rows.Add(new TikFakeDoneSentence());
        return rows;
    }

    /// <summary>
    /// Builds a RouterOS <c>/routing/ospf/instance/print</c> row for an OSPFv2 instance.
    /// <c>in-filter</c> and <c>out-filter-chain</c> keys are emitted only when non-null, mirroring
    /// RouterOS omitting unset properties, so tests can distinguish "unset" from "populated" (an
    /// already-cleared property must never be unset again).
    /// </summary>
    private static Dictionary<string, string> V2(
        string id,
        string name,
        string? inFilter,
        string? outFilterChain)
    {
        var d = new Dictionary<string, string>
        {
            [".id"] = id,
            ["name"] = name,
            ["version"] = "2",
        };
        if (inFilter is not null)
        {
            d["in-filter"] = inFilter;
        }

        if (outFilterChain is not null)
        {
            d["out-filter-chain"] = outFilterChain;
        }

        return d;
    }

    /// <summary>
    /// Builds an OSPFv3 instance row (<c>version=3</c>) with the same shape as <see cref="V2"/>;
    /// used to prove the OSPFv2 filter-reference clear never touches v3 instances that may
    /// legitimately carry their own filters.
    /// </summary>
    private static Dictionary<string, string> V3(
        string id,
        string name,
        string? inFilter,
        string? outFilterChain)
    {
        var d = new Dictionary<string, string>
        {
            [".id"] = id,
            ["name"] = name,
            ["version"] = "3",
        };
        if (inFilter is not null)
        {
            d["in-filter"] = inFilter;
        }

        if (outFilterChain is not null)
        {
            d["out-filter-chain"] = outFilterChain;
        }

        return d;
    }

    /// <summary>
    /// Builds a valid ROS6-sourced migration plan through the real planner (asserting it succeeds)
    /// so verifier and reconciler tests begin from a plan that passes validation; any verifier
    /// failure then comes only from the stale filter state under test.
    /// </summary>
    private static OspfMigrationPlan ValidPlan()
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
    /// Builds a ROS7 inventory that matches <see cref="ValidPlan"/> in every respect except the
    /// instance <c>in-filter</c>/<c>out-filter</c> values, isolating the verifier's
    /// stale-filter-reference check from all other verification rules.
    /// </summary>
    private static OspfV7Inventory Inventory(string? inFilter, string? outFilter)
    {
        OspfMigrationPlan plan = ValidPlan();
        OspfV7Inventory inv = OspfTestBaselines.InventoryMatchingPlan(plan);
        return new OspfV7Inventory
        {
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*1",
                    Name = plan.TargetInstanceName,
                    RouterId = plan.RouterId,
                    Version = "2",
                    InFilter = inFilter,
                    OutFilter = outFilter,
                },
            ],
            Areas = inv.Areas.Select(a => new OspfAreaRow
            {
                Id = a.Id,
                Name = a.Name,
                AreaId = a.AreaId,
                Type = a.Type,
                Instance = plan.TargetInstanceName,
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
    /// Captured instance unset commands as pairs of object id and property name.
    /// </summary>
    private sealed record UnsetCapture(IReadOnlyList<(string Id, string Property)> Unsets);

    /// <summary>
    /// Retains real tik4net unset commands so <c>.id</c> / <c>value-name</c> can be inspected after AddParameter.
    /// </summary>
    private sealed class UnsetCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        /// <summary>
        /// Instance <c>unset</c> commands created through this connection. They are retained rather
        /// than inspected immediately because tik4net parameters are attached after
        /// <c>CreateCommand</c> returns.
        /// </summary>
        private readonly List<ITikCommand> _unsetCommands = [];

        /// <summary>
        /// Projects the captured unset commands into <c>(.id, value-name)</c> pairs, evaluated
        /// lazily after parameters were added. Commands missing either value are ignored so only
        /// real property unsets are asserted on.
        /// </summary>
        public IReadOnlyList<(string Id, string Property)> Unsets =>
            _unsetCommands
                .Select(static cmd =>
                {
                    string id = cmd.Parameters.FirstOrDefault(p => p.Name == ".id")?.Value ?? "";
                    string prop = cmd.Parameters
                        .FirstOrDefault(p => p.Name == TikSpecialProperties.UnsetValueName)?.Value ?? "";
                    return (id, prop);
                })
                .Where(static t => t.id.Length > 0 && t.prop.Length > 0)
                .ToArray();

        /// <summary>
        /// Records <c>/routing/ospf/instance/unset</c> commands (so their parameters can be
        /// inspected later) and otherwise delegates to the inner fake.
        /// </summary>
        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            if (string.Equals(commandText, "/routing/ospf/instance/unset", StringComparison.Ordinal))
            {
                _unsetCommands.Add(cmd);
            }

            return cmd;
        }

        /// <summary>
        /// Overload taking an explicit parameter format; must record exactly like the other
        /// overload so no unset can bypass capture depending on which overload the production code
        /// calls.
        /// </summary>
        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            if (string.Equals(commandText, "/routing/ospf/instance/unset", StringComparison.Ordinal))
            {
                _unsetCommands.Add(cmd);
            }

            return cmd;
        }
    }

    /// <summary>
    /// Ops fake that returns an injected v7 inventory (for example with stale filter references) and a single
    /// loopback address; all other operations are no-ops or unsupported.
    /// </summary>
    private sealed class FakeOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Canned live inventory returned by <see cref="ReadV7InventoryAsync"/>; tests inject stale
        /// in-filter/out-filter references here to drive the verifier.
        /// </summary>
        public required OspfV7Inventory Inventory { get; init; }

        /// <summary>
        /// Returns the injected <see cref="Inventory"/> unchanged so the verifier evaluates exactly
        /// the state the test constructed.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        /// <summary>
        /// Reports zero routing filters so the "routing filters remain" read-back check cannot fail
        /// for reasons unrelated to this scenario.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// Always throws <see cref="NotSupportedException"/>: this fake only supports the v7
        /// reconcile/verify path, never a live capture.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
        /// Returns a minimal management-plane baseline (no management route observed) so backbone
        /// guard logic has nothing to trip over in these filter-focused tests.
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

    /// <summary>
    /// Ops fake that logs the order of the filter, instance-filter and redistribution stages and the instance names
    /// they receive, so tests can assert ordering and live-instance targeting.
    /// </summary>
    private sealed class SequenceOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Ordered log of mutation stages (<c>filters</c>, <c>instance-filters</c>,
        /// <c>redistribute</c>) used to assert the reconciler removes filters and clears instance
        /// filter references before touching redistribution.
        /// </summary>
        public List<string> Sequence { get; } = [];

        /// <summary>
        /// Records the <c>filters</c> stage; the routing-filter rules must go before anything that
        /// referenced them is cleared.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            Sequence.Add("filters");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records the <c>instance-filters</c> stage and the instance name it was invoked for,
        /// proving the reconciler targets the resolved live instance rather than the logical plan
        /// name.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            Sequence.Add("instance-filters");
            ClearedInstanceName = instanceName;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Instance name received by the filter-reference clear; expected to be the live
        /// <c>default-v2</c> instance, not the ROS6 logical name <c>default</c>.
        /// </summary>
        public string? ClearedInstanceName { get; private set; }

        /// <summary>
        /// Records the <c>redistribute</c> stage and the instance name used, so ordering relative
        /// to the filter stages and live-name targeting can both be asserted.
        /// </summary>
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            Sequence.Add("redistribute");
            RedistributeInstanceName = instanceName;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Instance name received by the redistribution clear; must match the live
        /// <c>default-v2</c> instance.
        /// </summary>
        public string? RedistributeInstanceName { get; private set; }

        /// <summary>
        /// Instance name received by the router-id set; must match the live <c>default-v2</c>
        /// instance so the loopback-derived router-id lands on the right object.
        /// </summary>
        public string? RouterIdInstanceName { get; private set; }

        /// <summary>
        /// Captures only the target instance name; the router-id value itself is validated
        /// elsewhere.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default)
        {
            RouterIdInstanceName = instanceName;
            return Task.CompletedTask;
        }

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
        /// Returns a minimal management-plane baseline so backbone guard logic is inert in this
        /// ordering test.
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
        /// Returns a clean inventory (no filter references) matching the plan so identity
        /// resolution succeeds and the reconciler proceeds into its mutation stages.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory(inFilter: null, outFilter: null));

        /// <summary>
        /// Reports zero routing filters so the "routing filters remain" read-back check cannot fail
        /// for reasons unrelated to this scenario.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// Always throws <see cref="NotSupportedException"/>: this fake only supports the v7
        /// reconcile/verify path, never a live capture.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// Returns a single <c>10.255.0.1/32</c> Loopback address so loopback-derived router-id
        /// validation sees one consistent /32.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.255.0.1", "lo"));
    }
}
