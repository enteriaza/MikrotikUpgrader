using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Non-live coverage for version-specific routing-filter menus used by OSPF migration.
/// </summary>
public sealed class OspfRoutingFilterMenuCompatibilityTests
{
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

    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    private static TikFakeConnection CreateVersionedFake(string version) =>
        FakeMikrotikConnections.CreateFullyPopulated(
            identity: "BR01.POP011.016.Tommy",
            version: version,
            architecture: "mipsbe",
            boardName: "hEX PoE");

    private static void AssertNeverTouchesSupportingCatalogues(CommandRecordingConnection recording)
    {
        Assert.DoesNotContain(recording.Commands, static c =>
            c.StartsWith("/routing/filter/num-list", StringComparison.Ordinal)
            || c.StartsWith("/routing/filter/community-list", StringComparison.Ordinal));
    }

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

    private sealed class RemoveIdCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        private readonly List<string> _removedIds = [];
        private readonly List<ITikCommand> _removeCommands = [];

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

        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            if (string.Equals(commandText, OspfRoutingFilterMenus.V7FilterRuleRemove, StringComparison.Ordinal))
            {
                _removeCommands.Add(cmd);
            }

            return cmd;
        }

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

    private sealed class ThrowingFilterOps : IOspfMikrotikOperations
    {
        public bool ReachedRedistribution { get; private set; }

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("filter-remove-failed");

        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            ReachedRedistribution = true;
            return Task.CompletedTask;
        }

        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

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
            Task.FromResult(BuildVerifierInventory());

        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.255.0.1", "lo"));
    }

    private sealed class CountingOps : IOspfMikrotikOperations
    {
        public required int FilterCount { get; init; }
        public required OspfV7Inventory Inventory { get; init; }

        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(FilterCount);

        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
}
