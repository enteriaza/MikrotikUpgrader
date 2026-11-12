// -----------------------------------------------------------------------------
// File: OspfLiveIdentityResolverTests.cs
//
// Description:
// Unit tests for OspfLiveIdentityResolver mapping planned names onto live ROS7
// inventory.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
    /// <summary>
    /// Scenario: OSPF apply path when live identity cannot be resolved.
    /// Expected: <c>InvalidOperationException</c> before any mutating OSPF command is issued.
    /// Invariant: unresolved identity fails closed; no partial OSPF mutation is allowed.
    /// </summary>
public sealed class OspfLiveIdentityResolverTests
{
    /// <summary>
    /// Scenario: a ROS6 logical plan (instance "default", areas "backbone" and "BR01.POP011.016.Tommy") is resolved against a live inventory with <c>default-v2</c> and router-id 10.16.0.11, with a matching Loopback /32.
    /// Expected: resolution succeeds and binds default to <c>default-v2</c>, backbone to <c>backbone-v2</c> and the site area to <c>BR01.POP011.016.Tommy-v2</c>; the remapped plan uses those live names and its notes contain no <c>.id=</c> values.
    /// Invariant: logical ROS6 names map to live v7 names via a Loopback-corroborated router-id, without leaking row ids.
    /// </summary>
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

    /// <summary>
    /// Scenario: the plan router-id is 198.18.0.1 while the live Loopback is 10.16.0.11.
    /// Expected: resolution fails with an error containing "does not match Loopback".
    /// Invariant: the plan router-id must agree with the Loopback address.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live <c>default-v2</c> instance has no router-id.
    /// Expected: resolution fails with an error containing "no router-id".
    /// Invariant: a live instance cannot be identified without a router-id.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live instance router-id is 198.18.0.1 while the Loopback address is 10.16.0.11.
    /// Expected: resolution fails with an error containing "conflicts with", "does not match Loopback" or "no unique router-id match".
    /// Invariant: a live router-id that contradicts the Loopback is never accepted.
    /// </summary>
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

    /// <summary>
    /// Scenario: the IP address inventory has no addresses at all.
    /// Expected: resolution fails with an error containing "Loopback interface is missing".
    /// Invariant: identity cannot be resolved without a Loopback address.
    /// </summary>
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

    /// <summary>
    /// Scenario: the Loopback carries 10.16.0.11/24 instead of a /32.
    /// Expected: resolution fails with an error mentioning "/32".
    /// Invariant: only a /32 Loopback can corroborate the router-id.
    /// </summary>
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

    /// <summary>
    /// Scenario: the Loopback has two in-scope /32 addresses (10.16.0.11 and 10.16.0.12) and the plan and live router-id are 10.16.0.11.
    /// Expected: resolution succeeds with derived loopback router-id 10.16.0.11.
    /// Invariant: the plan router-id disambiguates multiple Loopback addresses.
    /// </summary>
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

    /// <summary>
    /// Scenario: the Loopback has 10.16.0.11/32 and 10.16.0.12/32, but the plan and live router-id are 10.16.0.99.
    /// Expected: resolution fails with an error containing "Multiple distinct in-scope".
    /// Invariant: when several candidates exist and none is corroborated, no router-id is chosen.
    /// </summary>
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

    /// <summary>
    /// Scenario: the Loopback has an out-of-range address 172.16.150.253 and the in-range 10.16.0.11/32.
    /// Expected: resolution succeeds with derived loopback router-id 10.16.0.11.
    /// Invariant: out-of-range Loopback addresses do not interfere with router-id resolution.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live inventory has no instances or areas.
    /// Expected: resolution fails with an error naming <c>default-v2</c> as missing.
    /// Invariant: a plan can only be applied to an instance that exists.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live inventory has <c>other-v2</c> (router-id 1.1.1.1) and <c>default-v2</c> (router-id 10.16.0.11), with the plan router-id 10.16.0.11.
    /// Expected: resolution succeeds and binds <c>default-v2</c>.
    /// Invariant: among several instances, the one matching the router-id is chosen.
    /// </summary>
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

    /// <summary>
    /// Scenario: two live instances are both named <c>default-v2</c> with the same router-id.
    /// Expected: resolution fails with an error containing "Multiple live OSPFv2 instances named".
    /// Invariant: a duplicated instance name is ambiguous and is never guessed.
    /// </summary>
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

    /// <summary>
    /// Scenario: <c>default-v2</c> and <c>other-v2</c> both carry router-id 10.16.0.11.
    /// Expected: resolution succeeds and binds <c>default-v2</c>.
    /// Invariant: an exact name match wins over other instances with the same router-id.
    /// </summary>
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

    /// <summary>
    /// Scenario: <c>default-v2</c> has router-id 198.18.0.1 (conflicting) while <c>other-v2</c> has the plan's router-id 10.16.0.11.
    /// Expected: resolution fails with an error containing "not falling back" or "conflicts with".
    /// Invariant: a conflicting exact-name instance is never replaced by a different instance.
    /// </summary>
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

    /// <summary>
    /// Scenario: the only live instance, <c>default-v2</c>, has no router-id.
    /// Expected: resolution fails with an error containing "no router-id".
    /// Invariant: a sole instance is not accepted without router-id validation.
    /// </summary>
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

    /// <summary>
    /// Scenario: live areas are named "backbone" and "site-renamed-v2", though their area-ids match the plan.
    /// Expected: resolution fails with errors naming <c>backbone-v2</c> and <c>Tommy-v2</c>.
    /// Invariant: areas are matched by the expected v2 name; a matching area-id alone is not enough.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live inventory has <c>backbone-v2</c> but no <c>Tommy-v2</c> site area.
    /// Expected: resolution fails with an error naming <c>Tommy-v2</c> as missing.
    /// Invariant: every planned area must exist live before the plan can be applied.
    /// </summary>
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

    /// <summary>
    /// Scenario: a live area <c>duplicate-site</c> shares the area-id 10.16.0.11 with the site area.
    /// Expected: resolution fails with an error containing "Multiple live areas share area-id".
    /// Invariant: area ids must be unique, so the area mapping is never ambiguous.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live areas <c>backbone-v2</c> and <c>BR01.POP011.016.Tommy-v2</c> have no instance (null ownership).
    /// Expected: resolution fails with an error containing "empty instance ownership".
    /// Invariant: an area that does not name its instance is never assumed to belong to the resolved one.
    /// </summary>
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

    /// <summary>
    /// Scenario: the site area <c>Tommy-v2</c> belongs to <c>other-v2</c> instead of <c>default-v2</c>.
    /// Expected: resolution fails with an error containing "belongs to instance" or "Missing live OSPF area".
    /// Invariant: areas owned by another instance are never bound to the resolved instance.
    /// </summary>
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

    /// <summary>
    /// Scenario: two live areas are both named "shared-area", one with area-id 0.0.0.0 and one with 10.16.0.11, and neither uses the expected v2 names.
    /// Expected: resolution fails with an error naming <c>backbone-v2</c> or <c>Tommy-v2</c>, "same live area", or "Multiple live areas named".
    /// Invariant: the backbone and site areas can never map onto the same live area; colliding legacy names fail closed.
    /// </summary>
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

    /// <summary>
    /// Scenario: a plan is remapped with a binding whose live object ids are marked stale (<c>*STALE*</c>, <c>*STALE-A*</c>, <c>*STALE-B*</c>).
    /// Expected: the serialised remapped plan contains no <c>*STALE*</c> text, and the instance name is <c>default-v2</c>.
    /// Invariant: RouterOS object ids are transient and never end up in a persisted plan.
    /// </summary>
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

    /// <summary>
    /// Scenario: OSPF apply when live identity cannot be resolved.
    /// Expected: InvalidOperationException before any mutating OSPF command.
    /// Invariant: unresolved identity fails closed with no partial mutation.
    /// </summary>
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

    /// <summary>
    /// Scenario: the reconciler applies a plan where identity resolution fails: Loopback missing, live router-id mismatching the Loopback, or areas with empty instance ownership.
    /// Expected: <c>ApplyAsync</c> throws <c>InvalidOperationException</c>, and the ops fake recorded no mutations.
    /// Invariant: identity failures abort before any router change.
    /// </summary>
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

    /// <summary>
    /// Scenario: the reconciler applies a ROS6 logical plan against a live <c>default-v2</c> inventory.
    /// Expected: the recorded mutations target <c>default-v2</c> (instance filters, redistribute, router-id, cleanup), plus the areas <c>backbone-v2</c> and <c>BR01.POP011.016.Tommy-v2</c>; none targets the logical name <c>default</c>.
    /// Invariant: mutations use the resolved live names, never the logical ROS6 names.
    /// </summary>
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

    /// <summary>
    /// Scenario: the verifier runs a ROS6 logical plan against a live inventory using the v2 names, matching templates and area range, and a Loopback of 10.16.0.11.
    /// Expected: verification succeeds.
    /// Invariant: verification resolves the live identity itself, so a correctly migrated router passes.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live instance router-id is 10.16.0.11 but the Loopback is now 198.18.0.1.
    /// Expected: verification fails with an error mentioning "identity resolution failed" or "Loopback".
    /// Invariant: a router-id that no longer matches the Loopback is caught at verification.
    /// </summary>
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

    /// <summary>
    /// Scenario: the reconciler applies the same plan twice, and the ops fake changes the live instance id from <c>*FIRST</c> to <c>*SECOND</c> between calls.
    /// Expected: the ids observed by the ops are <c>*FIRST</c> and then <c>*SECOND</c>.
    /// Invariant: each apply re-reads inventory, so stale RouterOS ids are not reused across calls.
    /// </summary>
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

    /// <summary>
    /// Scenario: a ROS6 capture whose instance router-id is 1.2.3.4 while the <c>lo</c> address is 10.255.0.1/32.
    /// Expected: the plan fails validation with an error containing "does not match Loopback /32".
    /// Invariant: the planner refuses a router-id that disagrees with the Loopback /32.
    /// </summary>
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

    /// <summary>
    /// Builds a v7 inventory with a single default-v2 instance carrying the given router id and the Tommy areas.
    /// </summary>
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

    /// <summary>
    /// Builds a ROS6-origin plan that uses logical names (instance <c>default</c>, area
    /// <c>backbone</c>, identity-named site area) alongside ROS7 targets. The resolver must map
    /// these onto live <c>default-v2</c>, <c>backbone-v2</c> and <c>{Identity}-v2</c> objects and a
    /// loopback-derived router-id of <c>10.16.0.11</c>.
    /// </summary>
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

    /// <summary>
    /// Copies a plan overriding only <c>InstanceName</c> and <c>RouterId</c>, letting tests
    /// introduce instance-name or router-id disagreements while every other field stays identical.
    /// </summary>
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

    /// <summary>
    /// Builds the two live ROS7 areas (<c>backbone-v2</c> at 0.0.0.0 and the identity-named stub
    /// area at 10.16.0.11) owned by <paramref name="instance"/>; varying the owner models
    /// cross-instance area ownership.
    /// </summary>
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

    /// <summary>
    /// Ops fake that returns a configurable live inventory and addresses and logs every mutating call with its live
    /// instance or area name, proving identity is resolved before any mutation.
    /// </summary>
    private sealed class TrackingMutationOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Live inventory returned on every read; tests vary it to model missing, duplicate or
        /// mis-owned instances and areas.
        /// </summary>
        public required OspfV7Inventory Inventory { get; init; }

        /// <summary>
        /// Live IP address inventory used for loopback router-id derivation. Defaults to a single
        /// <c>10.16.0.11/32</c> Loopback; tests override it to model a missing, changed or non-/32
        /// loopback.
        /// </summary>
        public IReadOnlyList<OspfIpAddressRow> Addresses { get; init; } =
            OspfTestBaselines.LoopbackSlash32("10.16.0.11");

        /// <summary>
        /// Ordered log of mutating calls labelled <c>stage:instance[/area]</c>. Asserted empty when
        /// identity resolution fails (nothing may be mutated first) and asserted to name only
        /// resolved live objects when it succeeds.
        /// </summary>
        public List<string> Mutations { get; } = [];

        /// <summary>
        /// Count of inventory reads; proves the reconciler read live state to resolve identity
        /// before attempting any mutation.
        /// </summary>
        public int InventoryReads { get; private set; }

        /// <summary>
        /// Counts the read and returns the configured <see cref="Inventory"/>.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            InventoryReads++;
            return Task.FromResult(Inventory);
        }

        /// <summary>
        /// Records a <c>filters</c> mutation (not instance-scoped).
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            Mutations.Add("filters");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>instance-filters:&lt;instance&gt;</c> to prove the clear targets the resolved
        /// live instance name.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            Mutations.Add("instance-filters:" + instanceName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>redistribute:&lt;instance&gt;</c> to prove redistribution is cleared on the
        /// live instance.
        /// </summary>
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default)
        {
            Mutations.Add("redistribute:" + instanceName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>router-id:&lt;instance&gt;</c> to prove the router-id is written to the
        /// resolved live instance.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default)
        {
            Mutations.Add("router-id:" + instanceName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>area:&lt;instance&gt;/&lt;area&gt;</c> so tests can assert areas are
        /// addressed by their live <c>-v2</c> names, never by logical names.
        /// </summary>
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default)
        {
            Mutations.Add($"area:{instanceName}/{area.Name}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>range:&lt;area&gt;</c> for the area-range ensure.
        /// </summary>
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default)
        {
            Mutations.Add("range:" + range.AreaName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>backhaul:&lt;area&gt;</c> for the backhaul-template ensure.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default)
        {
            Mutations.Add("backhaul:" + template.AreaName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>loopback:&lt;area&gt;</c> for the loopback-template ensure.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Mutations.Add("loopback:" + template.AreaName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records <c>site:&lt;area&gt;</c> for the site-matcher ensure.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default)
        {
            Mutations.Add("site:" + template.AreaName);
            return Task.CompletedTask;
        }


        /// <summary>
        /// No-op: the post-ensure site-template read-back is assumed to pass in this scenario.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Records <c>cleanup:&lt;instance&gt;</c>; the cleanup stage must run against the live
        /// instance name carried by the remapped plan.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default)
        {
            Mutations.Add("cleanup:" + plan.InstanceName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// No-op: OSPFv3 removal is not under test in this scenario.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns the Tommy lab baseline (Full neighbour, default route over the backbone) so
        /// backbone guards run against realistic state.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        /// <summary>
        /// No-op backbone guard: this fake never reports a dropped adjacency or a changed
        /// management route.
        /// </summary>
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
        /// Returns no neighbours; the adjacency guard is not exercised by this scenario.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        /// <summary>
        /// Returns the configured <see cref="Addresses"/>.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Addresses);
    }

    /// <summary>
    /// Ops fake whose inventory read reports the current instance id and records each id handed out, proving ids
    /// are re-read on every apply; mutations are no-ops.
    /// </summary>
    private sealed class StaleIdOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Object <c>.id</c> reported for <c>default-v2</c> on the next inventory read; tests
        /// change it between runs to model RouterOS renumbering objects.
        /// </summary>
        public string NextInstanceId { get; set; } = "*FIRST";

        /// <summary>
        /// Id handed out on each inventory read. Two entries after two applies prove every
        /// <c>ApplyAsync</c> re-reads live state instead of reusing ids cached from a previous run.
        /// </summary>
        public List<string> ObservedInstanceIds { get; } = [];

        /// <summary>
        /// Records the currently configured <see cref="NextInstanceId"/> and returns an otherwise
        /// valid inventory carrying that id.
        /// </summary>
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

        /// <summary>
        /// No-op: routing-filter removal is outside this scenario, so the fake completes without
        /// recording or failing.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: clearing instance in-filter/out-filter references is not under test in this
        /// scenario.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: redistribution clearing is not under test in this scenario.
        /// </summary>
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the router-id in the fake inventory is already correct, so no router-id write is
        /// modelled.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: area reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: area-range reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the backhaul-template ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the loopback-template ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the site-matcher ensure step is not under test, so no RouterOS mutation is
        /// modelled.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the post-ensure site-template read-back is assumed to pass in this scenario.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: legacy interface-template cleanup is not under test in this scenario.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: OSPFv3 removal is not under test in this scenario.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Returns the Tommy lab baseline so backbone guards run against realistic state.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) => Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        /// <summary>
        /// No-op backbone guard: this fake never reports a dropped adjacency or a changed
        /// management route.
        /// </summary>
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Reports zero routing filters so the "routing filters remain" read-back check cannot fail
        /// for reasons unrelated to this scenario.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(0);

        /// <summary>
        /// Always throws <see cref="NotSupportedException"/>: this fake only supports the v7
        /// reconcile/verify path, never a live capture.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>
        /// Returns no neighbours; the adjacency guard is not exercised by this scenario.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        /// <summary>
        /// Returns a single <c>10.16.0.11/32</c> Loopback address so loopback-derived router-id
        /// validation sees one consistent /32.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));
    }
}
