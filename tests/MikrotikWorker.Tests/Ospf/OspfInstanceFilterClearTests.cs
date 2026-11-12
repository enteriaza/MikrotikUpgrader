using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Ospf;

public sealed class OspfInstanceFilterClearTests
{
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

    [Fact]
    public async Task Clear_UnsetsInbound_WhenOnlyInboundPopulated()
    {
        UnsetCapture capture = await RunClearAsync(
            CreatePrintResponse(
                V2("*1", "default", inFilter: "ospf-in", outFilterChain: null)));

        Assert.Single(capture.Unsets);
        Assert.Contains(capture.Unsets, u => u.Id == "*1" && u.Property == "in-filter");
    }

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

    [Fact]
    public async Task Clear_SkipsUnset_WhenBothAlreadyUnset()
    {
        UnsetCapture capture = await RunClearAsync(
            CreatePrintResponse(
                V2("*1", "default", inFilter: null, outFilterChain: null)));

        // Already-cleared properties must not be unset (ROS7 rejects ambiguous bare out-filter).
        Assert.Empty(capture.Unsets);
    }

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
                    Networks = "10.10.0.0/16",
                    Passive = true,
                },
            ],
        };
    }

    private sealed record UnsetCapture(IReadOnlyList<(string Id, string Property)> Unsets);

    /// <summary>
    /// Retains real tik4net unset commands so <c>.id</c> / <c>value-name</c> can be inspected after AddParameter.
    /// </summary>
    private sealed class UnsetCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        private readonly List<ITikCommand> _unsetCommands = [];

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

        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            if (string.Equals(commandText, "/routing/ospf/instance/unset", StringComparison.Ordinal))
            {
                _unsetCommands.Add(cmd);
            }

            return cmd;
        }

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

        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
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

        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.255.0.1", "lo"));
    }

    private sealed class SequenceOps : IOspfMikrotikOperations
    {
        public List<string> Sequence { get; } = [];

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            Sequence.Add("filters");
            return Task.CompletedTask;
        }

        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            Sequence.Add("instance-filters");
            ClearedInstanceName = instanceName;
            return Task.CompletedTask;
        }

        public string? ClearedInstanceName { get; private set; }

        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            Sequence.Add("redistribute");
            RedistributeInstanceName = instanceName;
            return Task.CompletedTask;
        }

        public string? RedistributeInstanceName { get; private set; }

        public string? RouterIdInstanceName { get; private set; }

        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default)
        {
            RouterIdInstanceName = instanceName;
            return Task.CompletedTask;
        }

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
            Task.FromResult(Inventory(inFilter: null, outFilter: null));

        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.255.0.1", "lo"));
    }
}
