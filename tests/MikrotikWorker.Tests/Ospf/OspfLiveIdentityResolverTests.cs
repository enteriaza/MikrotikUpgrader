using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Ospf;

public sealed class OspfLiveIdentityResolverTests
{
    [Fact]
    public void Resolve_MapsRos6DefaultAndAreas_UsingLoopbackDerivedRouterId()
    {
        OspfMigrationPlan plan = Ros6LogicalPlan();
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            plan,
            LiveDefaultV2Inventory(routerId: "10.16.0.11"),
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.True(result.Succeeded, result.Detail);
        Assert.NotNull(result.Binding);
        Assert.Equal("default", result.Binding.LogicalInstanceName);
        Assert.Equal("default-v2", result.Binding.LiveInstanceName);
        Assert.Equal("10.16.0.11", result.Binding.DerivedLoopbackRouterId);
        Assert.Equal("backbone-v2", result.Binding.Areas["backbone"].LiveName);
        Assert.Equal("BR01.POP011.016.Tommy-v2", result.Binding.Areas["BR01.POP011.016.Tommy"].LiveName);

        OspfMigrationPlan remapped = OspfLiveIdentityResolver.RemapPlan(plan, result.Binding);
        Assert.Equal("default-v2", remapped.InstanceName);
        Assert.All(remapped.ActiveBackhauls, b => Assert.Equal("backbone-v2", b.AreaName));
        Assert.All(remapped.PassiveSiteNetworks, s => Assert.Equal("BR01.POP011.016.Tommy-v2", s.AreaName));
        Assert.DoesNotContain(remapped.NormalisationNotes, n => n.Contains(".id=", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_Fails_WhenPlanRouterIdDiffersFromLoopback()
    {
        OspfMigrationPlan plan = ClonePlan(Ros6LogicalPlan(), routerId: "198.18.0.1");

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            plan,
            LiveDefaultV2Inventory(routerId: "10.16.0.11"),
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("does not match Loopback", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_Fails_WhenLiveRouterIdMissing()
    {
        OspfV7Inventory live = LiveDefaultV2Inventory(routerId: null);
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("no router-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_Fails_WhenLiveRouterIdDiffersFromLoopback()
    {
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            LiveDefaultV2Inventory(routerId: "198.18.0.1"),
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("conflicts with", StringComparison.OrdinalIgnoreCase)
            || e.Contains("does not match Loopback", StringComparison.OrdinalIgnoreCase)
            || e.Contains("no unique router-id match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_Fails_WhenLoopbackMissing()
    {
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            LiveDefaultV2Inventory(routerId: "10.16.0.11"),
            []);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Loopback interface is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_Fails_WhenLoopbackNotSlash32()
    {
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            LiveDefaultV2Inventory(routerId: "10.16.0.11"),
            [new OspfIpAddressRow { Address = "10.16.0.11/24", Network = "10.16.0.0", Interface = "Loopback" }]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("/32", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_SelectsPlanCorroboratedLoopback_WhenMultipleInScopeAddresses()
    {
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            LiveDefaultV2Inventory(routerId: "10.16.0.11"),
            [
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.0.12/32", Network = "10.16.0.12", Interface = "Loopback" },
            ]);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal("10.16.0.11", result.Binding!.DerivedLoopbackRouterId);
    }

    [Fact]
    public void Resolve_Fails_WhenMultipleInScopeLoopbacksConflictWithPlan()
    {
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            ClonePlan(Ros6LogicalPlan(), routerId: "10.16.0.99"),
            LiveDefaultV2Inventory(routerId: "10.16.0.99"),
            [
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.0.12/32", Network = "10.16.0.12", Interface = "Loopback" },
            ]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Multiple distinct in-scope", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_IgnoresOutsideRangeLoopbackAddresses()
    {
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            LiveDefaultV2Inventory(routerId: "10.16.0.11"),
            [
                new OspfIpAddressRow { Address = "172.16.150.253", Network = "172.16.150.253", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
            ]);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal("10.16.0.11", result.Binding!.DerivedLoopbackRouterId);
    }

    [Fact]
    public void Resolve_Fails_WhenInstanceMissing()
    {
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            new OspfV7Inventory { Instances = [], Areas = [] },
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("default-v2", StringComparison.Ordinal)
            && e.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_SelectsUniqueRouterIdMatch_AmongMultipleInstances()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "other-v2", RouterId = "1.1.1.1", Version = "2" },
                new OspfInstanceRow { Id = "*2", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
            ],
            Areas = TommyAreas("default-v2"),
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("default-v2", result.Binding!.LiveInstanceName);
    }

    [Fact]
    public void Resolve_Fails_WhenExpectedInstanceNameIsDuplicated()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
                new OspfInstanceRow { Id = "*2", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
            ],
            Areas = TommyAreas("default-v2"),
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Multiple live OSPFv2 instances named", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_IgnoresOtherInstances_WhenExactDefaultV2Matches()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
                new OspfInstanceRow { Id = "*2", Name = "other-v2", RouterId = "10.16.0.11", Version = "2" },
            ],
            Areas = TommyAreas("default-v2"),
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("default-v2", result.Binding!.LiveInstanceName);
    }

    [Fact]
    public void Resolve_Fails_ExactNameWithConflictingRouterId_DoesNotFallBack()
    {
        OspfMigrationPlan plan = ClonePlan(Ros6LogicalPlan(), instanceName: "default-v2", routerId: "10.16.0.11");
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "198.18.0.1", Version = "2" },
                new OspfInstanceRow { Id = "*2", Name = "other-v2", RouterId = "10.16.0.11", Version = "2" },
            ],
            Areas = TommyAreas("other-v2"),
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            plan,
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("not falling back", StringComparison.OrdinalIgnoreCase)
            || e.Contains("conflicts with", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_Fails_WhenSoleInstanceLacksRouterId_NeverAcceptsWithoutRidValidation()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = null, Version = "2" },
            ],
            Areas = TommyAreas("default-v2"),
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("no router-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_Fails_WhenExpectedAreaNamesMissing_DoesNotFallBackToAreaId()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*a0",
                    Name = "backbone",
                    AreaId = "0.0.0.0",
                    Instance = "default-v2",
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Id = "*a1",
                    Name = "site-renamed-v2",
                    AreaId = "10.16.0.11",
                    Instance = "default-v2",
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("backbone-v2", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("Tommy-v2", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_Fails_WhenCorrespondingAreaMissing()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*a0",
                    Name = "backbone-v2",
                    AreaId = "0.0.0.0",
                    Instance = "default-v2",
                    Type = "default",
                },
            ],
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Tommy-v2", StringComparison.Ordinal)
            && e.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_Fails_WhenDuplicateAreaId()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
            ],
            Areas =
            [
                ..TommyAreas("default-v2"),
                new OspfAreaRow
                {
                    Id = "*a2",
                    Name = "duplicate-site",
                    AreaId = "10.16.0.11",
                    Instance = "default-v2",
                    Type = "stub",
                },
            ],
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Multiple live areas share area-id", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_Fails_OnEmptyInstanceOwnership()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*a0",
                    Name = "backbone-v2",
                    AreaId = "0.0.0.0",
                    Instance = null,
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Id = "*a1",
                    Name = "BR01.POP011.016.Tommy-v2",
                    AreaId = "10.16.0.11",
                    Instance = null,
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("empty instance ownership", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_RejectsCrossInstanceArea()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
                new OspfInstanceRow { Id = "*2", Name = "other-v2", RouterId = "1.2.3.4", Version = "2" },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*a0",
                    Name = "backbone-v2",
                    AreaId = "0.0.0.0",
                    Instance = "default-v2",
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Id = "*a1",
                    Name = "BR01.POP011.016.Tommy-v2",
                    AreaId = "10.16.0.11",
                    Instance = "other-v2",
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
        };

        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e =>
            e.Contains("belongs to instance", StringComparison.OrdinalIgnoreCase)
            || e.Contains("Missing live OSPF area", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_Fails_WhenBackboneAndSiteCollideOntoSameLiveArea()
    {
        OspfV7Inventory live = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*1", Name = "default-v2", RouterId = "10.16.0.11", Version = "2" },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Id = "*a0",
                    Name = "shared-area",
                    AreaId = "0.0.0.0",
                    Instance = "default-v2",
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Id = "*a1",
                    Name = "shared-area",
                    AreaId = "10.16.0.11",
                    Instance = "default-v2",
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
        };

        // Name collision path: both logical areas resolve by area-id to rows that share LiveName.
        // Force both planned areas to map to the same live name via area-id-only rows with identical names.
        OspfLiveIdentityResolutionResult result = OspfLiveIdentityResolver.Resolve(
            Ros6LogicalPlan(),
            live,
            OspfTestBaselines.LoopbackSlash32("10.16.0.11"));

        Assert.False(result.Succeeded);
        // Deterministic naming requires backbone-v2 / {Identity}-v2; colliding legacy names fail closed.
        Assert.Contains(result.Errors, e =>
            e.Contains("backbone-v2", StringComparison.Ordinal)
            || e.Contains("Tommy-v2", StringComparison.Ordinal)
            || e.Contains("same live area", StringComparison.OrdinalIgnoreCase)
            || e.Contains("Multiple live areas named", StringComparison.Ordinal));
    }

    [Fact]
    public void RemapPlan_DoesNotPersistLiveObjectIds()
    {
        OspfMigrationPlan plan = Ros6LogicalPlan();
        OspfLiveIdentityBinding binding = new()
        {
            LogicalInstanceName = "default",
            LiveInstanceName = "default-v2",
            LiveInstanceId = "*STALE*",
            LiveRouterId = "10.16.0.11",
            DerivedLoopbackRouterId = "10.16.0.11",
            LoopbackInterfaceName = "Loopback",
            Areas = new Dictionary<string, OspfLiveAreaBinding>
            {
                ["backbone"] = new()
                {
                    LogicalName = "backbone",
                    LiveName = "backbone-v2",
                    AreaId = "0.0.0.0",
                    LiveObjectId = "*STALE-A*",
                },
                ["BR01.POP011.016.Tommy"] = new()
                {
                    LogicalName = "BR01.POP011.016.Tommy",
                    LiveName = "BR01.POP011.016.Tommy-v2",
                    AreaId = "10.16.0.11",
                    LiveObjectId = "*STALE-B*",
                },
            },
        };

        OspfMigrationPlan remapped = OspfLiveIdentityResolver.RemapPlan(plan, binding);
        string json = System.Text.Json.JsonSerializer.Serialize(remapped);
        Assert.DoesNotContain("*STALE*", json, StringComparison.Ordinal);
        Assert.Equal("default-v2", remapped.InstanceName);
    }

    [Fact]
    public async Task Apply_FailsClosed_BeforeAnyMutation_WhenIdentityUnresolved()
    {
        TrackingMutationOps ops = new()
        {
            Inventory = new OspfV7Inventory { Instances = [], Areas = [] },
            Addresses = OspfTestBaselines.LoopbackSlash32("10.16.0.11"),
        };

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
                .ApplyAsync(
                    FakeMikrotikConnections.CreateFullyPopulated(version: "7.25.1"),
                    Ros6LogicalPlan(),
                    managementHost: "198.18.240.11",
                    CancellationToken.None));

        Assert.Contains("identity resolution failed before mutation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ops.Mutations);
        Assert.True(ops.InventoryReads >= 1);
    }

    [Theory]
    [InlineData("loopback-missing")]
    [InlineData("live-rid-mismatch")]
    [InlineData("empty-area-instance")]
    public async Task Apply_FailsClosed_BeforeAnyMutation_ForIdentityFailures(string mode)
    {
        TrackingMutationOps ops = mode switch
        {
            "loopback-missing" => new()
            {
                Inventory = LiveDefaultV2Inventory("10.16.0.11"),
                Addresses = [],
            },
            "live-rid-mismatch" => new()
            {
                Inventory = LiveDefaultV2Inventory("198.18.0.1"),
                Addresses = OspfTestBaselines.LoopbackSlash32("10.16.0.11"),
            },
            "empty-area-instance" => new()
            {
                Inventory = new OspfV7Inventory
                {
                    Instances =
                    [
                        new OspfInstanceRow
                        {
                            Id = "*1",
                            Name = "default-v2",
                            RouterId = "10.16.0.11",
                            Version = "2",
                        },
                    ],
                    Areas =
                    [
                        new OspfAreaRow
                        {
                            Id = "*a0",
                            Name = "backbone-v2",
                            AreaId = "0.0.0.0",
                            Instance = null,
                            Type = "default",
                        },
                        new OspfAreaRow
                        {
                            Id = "*a1",
                            Name = "BR01.POP011.016.Tommy-v2",
                            AreaId = "10.16.0.11",
                            Instance = null,
                            Type = "stub",
                        },
                    ],
                },
                Addresses = OspfTestBaselines.LoopbackSlash32("10.16.0.11"),
            },
            _ => throw new InvalidOperationException(mode),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
                .ApplyAsync(
                    FakeMikrotikConnections.CreateFullyPopulated(version: "7.25.1"),
                    Ros6LogicalPlan(),
                    managementHost: "198.18.240.11",
                    CancellationToken.None));

        Assert.Empty(ops.Mutations);
    }

    [Fact]
    public async Task Apply_TargetsResolvedLiveInstanceAndAreas()
    {
        TrackingMutationOps ops = new()
        {
            Inventory = LiveDefaultV2Inventory("10.16.0.11"),
            Addresses = OspfTestBaselines.LoopbackSlash32("10.16.0.11"),
        };

        await new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
            .ApplyAsync(
                FakeMikrotikConnections.CreateFullyPopulated(version: "7.25.1"),
                Ros6LogicalPlan(),
                managementHost: "198.18.240.11",
                CancellationToken.None);

        Assert.Contains("instance-filters:default-v2", ops.Mutations);
        Assert.Contains("redistribute:default-v2", ops.Mutations);
        Assert.Contains("router-id:default-v2", ops.Mutations);
        Assert.Contains("area:default-v2/backbone-v2", ops.Mutations);
        Assert.Contains("area:default-v2/BR01.POP011.016.Tommy-v2", ops.Mutations);
        Assert.Contains("cleanup:default-v2", ops.Mutations);
        Assert.DoesNotContain(ops.Mutations, m => m.Contains(":default/", StringComparison.Ordinal)
            || m.EndsWith(":default", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_UsesResolvedLiveAreaNames_AndLoopbackRid()
    {
        OspfMigrationPlan plan = Ros6LogicalPlan();
        OspfV7Inventory inventory = new()
        {
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*1",
                    Name = "default-v2",
                    RouterId = "10.16.0.11",
                    Version = "2",
                    RedistributeProperties = new Dictionary<string, string?>(),
                },
            ],
            Areas = TommyAreas("default-v2"),
            AreaRanges =
            [
                new OspfAreaRangeRow { Area = "BR01.POP011.016.Tommy-v2", Range = "10.16.11.0/24" },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone-v2",
                    Interfaces = "eth-01_BH01.Tommy-Shylock",
                    Networks = "10.16.248.76/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone-v2",
                    Interfaces = "Loopback",
                    Networks = "10.16.0.11/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "BR01.POP011.016.Tommy-v2",
                    Interfaces = "all",
                    Networks = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
                    Passive = true,
                },
            ],
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(
                new TrackingMutationOps
                {
                    Inventory = inventory,
                    Addresses = OspfTestBaselines.LoopbackSlash32("10.16.0.11"),
                },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(version: "7.25.1"), plan, CancellationToken.None);

        Assert.True(result.Succeeded, result.Detail);
    }

    [Fact]
    public async Task Verify_Fails_WhenLoopbackRidChanges()
    {
        TrackingMutationOps ops = new()
        {
            Inventory = LiveDefaultV2Inventory("10.16.0.11"),
            Addresses = OspfTestBaselines.LoopbackSlash32("198.18.0.1"),
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(
                ops,
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(
                FakeMikrotikConnections.CreateFullyPopulated(version: "7.25.1"),
                Ros6LogicalPlan(),
                CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("identity resolution failed", StringComparison.OrdinalIgnoreCase)
            || e.Contains("Loopback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_ReReadsInventory_DoesNotReuseStaleIdsAcrossCalls()
    {
        StaleIdOps ops = new();
        OspfMigrationPlan plan = Ros6LogicalPlan();

        await new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
            .ApplyAsync(
                FakeMikrotikConnections.CreateFullyPopulated(version: "7.25.1"),
                plan,
                managementHost: "198.18.240.11",
                CancellationToken.None);

        Assert.Equal(["*FIRST"], ops.ObservedInstanceIds);

        ops.NextInstanceId = "*SECOND";
        await new OspfV7Reconciler(ops, NullLogger<OspfV7Reconciler>.Instance)
            .ApplyAsync(
                FakeMikrotikConnections.CreateFullyPopulated(version: "7.25.1"),
                plan,
                managementHost: "198.18.240.11",
                CancellationToken.None);

        Assert.Equal(["*FIRST", "*SECOND"], ops.ObservedInstanceIds);
    }

    [Fact]
    public void Planner_Fails_WhenInstanceRouterIdDiffersFromLoopbackSlash32()
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
            Instances = [new OspfInstanceRow { Name = "default", RouterId = "1.2.3.4" }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Type = "default" },
                new OspfAreaRow { Name = "site", AreaId = "10.255.0.1", Type = "stub" },
            ],
            Networks =
            [
                new OspfNetworkRow { Area = "backbone", Network = "10.255.0.1/32" },
                new OspfNetworkRow { Area = "backbone", Network = "10.0.0.0/30" },
                new OspfNetworkRow { Area = "site", Network = "10.10.0.0/16" },
            ],
            Interfaces = [new OspfInterfaceRow { Interface = "ether1", NetworkType = "ptp", Cost = "10" }],
            IpAddresses =
            [
                new OspfIpAddressRow { Address = "10.255.0.1/32", Network = "10.255.0.1", Interface = "lo" },
                new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
                new OspfIpAddressRow { Address = "10.10.1.1/24", Network = "10.10.1.0", Interface = "bridge-local" },
            ],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snap);
        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Plan.ValidationErrors,
            e => e.Contains("does not match Loopback /32", StringComparison.Ordinal));
    }

    private static OspfV7Inventory LiveDefaultV2Inventory(string? routerId) =>
        new()
        {
            Instances =
            [
                new OspfInstanceRow
                {
                    Id = "*1",
                    Name = "default-v2",
                    RouterId = routerId,
                    Version = "2",
                },
            ],
            Areas = TommyAreas("default-v2"),
        };

    private static OspfMigrationPlan Ros6LogicalPlan() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = "198.18.240.11",
            Identity = "BR01.POP011.016.Tommy",
            SourceRouterOsVersion = "6.49.23",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            ContentHash = "test",
            InstanceName = "default",
            RouterId = "10.16.0.11",
            TargetInstanceName = OspfFleetNaming.Ros7InstanceName,
            TargetBackboneAreaName = OspfFleetNaming.Ros7BackboneAreaName,
            TargetSiteAreaName = OspfFleetNaming.Ros7SiteAreaName("BR01.POP011.016.Tommy"),
            LoopbackInterfaceName = "Loopback",
            LoopbackCidr = "10.16.0.11/32",
            DerivedManagementCidr = "10.16.11.0/24",
            Areas =
            [
                new OspfPlanArea
                {
                    Name = "backbone",
                    AreaId = "0.0.0.0",
                    IsBackbone = true,
                    TargetType = "default",
                },
                new OspfPlanArea
                {
                    Name = "BR01.POP011.016.Tommy",
                    AreaId = "10.16.0.11",
                    IsBackbone = false,
                    TargetType = "stub",
                    DefaultCost = "1",
                },
            ],
            AreaRanges =
            [
                new OspfPlanAreaRange
                {
                    AreaName = "BR01.POP011.016.Tommy",
                    RangeCidr = "10.16.11.0/24",
                },
            ],
            ActiveBackhauls =
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "eth-01_BH01.Tommy-Shylock",
                    NetworkCidr = "10.16.248.76/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
            ],
            PassiveLoopbacks =
            [
                new OspfPlanLoopbackTemplate
                {
                    AreaName = "backbone",
                    Interface = "Loopback",
                    NetworkCidr = "10.16.0.11/32",
                    Passive = true,
                },
            ],
            PassiveSiteNetworks =
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "BR01.POP011.016.Tommy",
                    Interfaces = "all",
                    NetworkCidr = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
                    Passive = true,
                },
            ],
        };

    private static OspfMigrationPlan ClonePlan(
        OspfMigrationPlan plan,
        string? instanceName = null,
        string? routerId = null) =>
        new()
        {
            ImportId = plan.ImportId,
            JobId = plan.JobId,
            RouterIp = plan.RouterIp,
            Identity = plan.Identity,
            SourceRouterOsVersion = plan.SourceRouterOsVersion,
            Architecture = plan.Architecture,
            SourceKind = plan.SourceKind,
            ContentHash = plan.ContentHash,
            InstanceName = instanceName ?? plan.InstanceName,
            RouterId = routerId ?? plan.RouterId,
            TargetInstanceName = plan.TargetInstanceName,
            TargetBackboneAreaName = plan.TargetBackboneAreaName,
            TargetSiteAreaName = plan.TargetSiteAreaName,
            LoopbackInterfaceName = plan.LoopbackInterfaceName,
            LoopbackCidr = plan.LoopbackCidr,
            DerivedManagementCidr = plan.DerivedManagementCidr,
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            SupersededTemplates = plan.SupersededTemplates,
            RemoveAllRoutingFilters = plan.RemoveAllRoutingFilters,
            ClearAllRedistribution = plan.ClearAllRedistribution,
            RemoveAllOspfV3 = plan.RemoveAllOspfV3,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = plan.NormalisationNotes,
            ValidationErrors = plan.ValidationErrors,
        };

    private static List<OspfAreaRow> TommyAreas(string instance) =>
    [
        new OspfAreaRow
        {
            Id = "*a0",
            Name = "backbone-v2",
            AreaId = "0.0.0.0",
            Instance = instance,
            Type = "default",
        },
        new OspfAreaRow
        {
            Id = "*a1",
            Name = "BR01.POP011.016.Tommy-v2",
            AreaId = "10.16.0.11",
            Instance = instance,
            Type = "stub",
            DefaultCost = "1",
            NoSummaries = false,
        },
    ];

    private sealed class TrackingMutationOps : IOspfMikrotikOperations
    {
        public required OspfV7Inventory Inventory { get; init; }

        public IReadOnlyList<OspfIpAddressRow> Addresses { get; init; } =
            OspfTestBaselines.LoopbackSlash32("10.16.0.11");

        public List<string> Mutations { get; } = [];

        public int InventoryReads { get; private set; }

        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            InventoryReads++;
            return Task.FromResult(Inventory);
        }

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            Mutations.Add("filters");
            return Task.CompletedTask;
        }

        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            Mutations.Add("instance-filters:" + instanceName);
            return Task.CompletedTask;
        }

        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            Mutations.Add("redistribute:" + instanceName);
            return Task.CompletedTask;
        }

        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default)
        {
            Mutations.Add("router-id:" + instanceName);
            return Task.CompletedTask;
        }

        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default)
        {
            Mutations.Add($"area:{instanceName}/{area.Name}");
            return Task.CompletedTask;
        }

        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default)
        {
            Mutations.Add("range:" + range.AreaName);
            return Task.CompletedTask;
        }

        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default)
        {
            Mutations.Add("backhaul:" + template.AreaName);
            return Task.CompletedTask;
        }

        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Mutations.Add("loopback:" + template.AreaName);
            return Task.CompletedTask;
        }

        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Mutations.Add("site:" + template.AreaName);
            return Task.CompletedTask;
        }


        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default)
        {
            Mutations.Add("cleanup:" + plan.InstanceName);
            return Task.CompletedTask;
        }

        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Addresses);
    }

    private sealed class StaleIdOps : IOspfMikrotikOperations
    {
        public string NextInstanceId { get; set; } = "*FIRST";

        public List<string> ObservedInstanceIds { get; } = [];

        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            ObservedInstanceIds.Add(NextInstanceId);
            return Task.FromResult(new OspfV7Inventory
            {
                Instances =
                [
                    new OspfInstanceRow
                    {
                        Id = NextInstanceId,
                        Name = "default-v2",
                        RouterId = "10.16.0.11",
                        Version = "2",
                    },
                ],
                Areas = TommyAreas("default-v2"),
            });
        }

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) => Task.FromResult(OspfTestBaselines.Tommy(managementHost));
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));
    }
}
