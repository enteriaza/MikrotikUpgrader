// -----------------------------------------------------------------------------
// File: OspfLoopbackRouterIdTests.cs
//
// Description:
// Unit tests for OspfLoopbackRouterId derivation from Loopback /32 addresses.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Unit tests for OspfLoopbackRouterId derivation.
/// </summary>
public sealed class OspfLoopbackRouterIdTests
{
    /// <summary>
    /// Scenario: the address inventory has a <c>lo</c> interface with 10.255.0.1/32 plus an unrelated ether1 /30.
    /// Expected: derivation succeeds with router-id 10.255.0.1, loopback interface <c>lo</c> and CIDR 10.255.0.1/32.
    /// Invariant: a /32 on a supported loopback name yields the router-id; other interfaces are ignored.
    /// </summary>
    [Fact]
    public void Derive_AcceptsLoWithExactSlash32()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "10.255.0.1/32", Network = "10.255.0.1", Interface = "lo" },
            new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
        ]);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.255.0.1", result.RouterId);
        Assert.Equal("lo", result.LoopbackInterfaceName);
        Assert.Equal("10.255.0.1/32", result.LoopbackCidr);
    }

    /// <summary>
    /// Scenario: the inventory holds 10.16.0.11/32 on an interface named <c>Loopback</c>.
    /// Expected: derivation succeeds with router-id 10.16.0.11 and interface name <c>Loopback</c>.
    /// Invariant: the established <c>Loopback</c> name is accepted alongside <c>lo</c>.
    /// </summary>
    [Fact]
    public void Derive_AcceptsEstablishedLoopbackNameWithExactSlash32()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
            OspfTestBaselines.LoopbackSlash32("10.16.0.11", "Loopback"));

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.0.11", result.RouterId);
        Assert.Equal("Loopback", result.LoopbackInterfaceName);
    }

    /// <summary>
    /// Scenario: the inventory has only an ether1 address and no loopback interface address.
    /// Expected: derivation fails with an error containing "Loopback interface is missing".
    /// Invariant: a router-id is never guessed from non-loopback interfaces.
    /// </summary>
    [Fact]
    public void Derive_Fails_WhenLoopbackAbsent()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
        ]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Loopback interface is missing", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: the address inventory is empty, so the loopback has no address.
    /// Expected: derivation fails.
    /// Invariant: a loopback without an address is treated the same as an absent loopback.
    /// </summary>
    [Fact]
    public void Derive_Fails_WhenLoopbackHasNoAddress_IsCoveredByAbsence()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive([]);
        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// Scenario: the <c>Loopback</c> interface carries 10.16.0.11/24 instead of a host route.
    /// Expected: derivation fails with an error containing "must be a /32".
    /// Invariant: only an exact /32 loopback address can become the router-id.
    /// </summary>
    [Fact]
    public void Derive_Fails_WhenLoopbackAddressIsNotSlash32()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "10.16.0.11/24", Network = "10.16.0.0", Interface = "Loopback" },
        ]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("must be a /32", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: <c>Loopback</c> carries two in-scope /32 addresses (10.16.0.11 and 10.16.0.12) and no corroborating router-id is supplied.
    /// Expected: derivation fails with an error containing "Multiple distinct in-scope".
    /// Invariant: an ambiguous router-id is never chosen arbitrarily.
    /// </summary>
    [Fact]
    public void Derive_Fails_WhenMultipleInScopeAddressesWithoutCorroboration()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
            new OspfIpAddressRow { Address = "10.16.0.12/32", Network = "10.16.0.12", Interface = "Loopback" },
        ]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Multiple distinct in-scope", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: <c>Loopback</c> carries 10.16.0.11/32 and 10.16.0.12/32, and the corroborating router-id is 10.16.0.11.
    /// Expected: derivation succeeds with router-id 10.16.0.11 and reports both observed in-scope addresses.
    /// Invariant: an existing router-id can disambiguate between several in-scope loopback addresses.
    /// </summary>
    [Fact]
    public void Derive_SelectsCorroboratedInScopeAddress_WhenMultiplePresent()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
            new OspfIpAddressRow { Address = "10.16.0.12/32", Network = "10.16.0.12", Interface = "Loopback" },
        ],
        corroboratingRouterId: "10.16.0.11");

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.0.11", result.RouterId);
        Assert.Equal(2, result.ObservedInScopeAddresses.Count);
    }

    /// <summary>
    /// Scenario: <c>Loopback</c> carries 10.16.0.11/32 and 10.16.0.12/32, but the corroborating router-id is 10.16.0.99, which matches neither.
    /// Expected: derivation fails with an error containing "does not uniquely match".
    /// Invariant: a corroborating router-id that matches no candidate is a conflict, not a fallback to either address.
    /// </summary>
    [Fact]
    public void Derive_Fails_WhenCorroboratingRouterIdConflicts()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
            new OspfIpAddressRow { Address = "10.16.0.12/32", Network = "10.16.0.12", Interface = "Loopback" },
        ],
        corroboratingRouterId: "10.16.0.99");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("does not uniquely match", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: the same 10.16.0.11/32 is assigned to both <c>lo</c> and <c>Loopback</c>.
    /// Expected: derivation fails with an error containing "multiple Loopback interfaces".
    /// Invariant: a router-id host address must live on exactly one loopback interface.
    /// </summary>
    [Fact]
    public void Derive_Fails_WhenSelectedHostAssignedOnMultipleLoopbackInterfaces()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "lo" },
            new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
        ]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("multiple Loopback interfaces", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: <c>Loopback</c> carries bare addresses 172.16.150.253 and 102.66.242.100 (outside the infrastructure range) and 10.16.254.1 (inside).
    /// Expected: derivation succeeds with router-id 10.16.254.1 and CIDR 10.16.254.1/32, two addresses reported as ignored and one as in-scope.
    /// Invariant: out-of-range loopback addresses are skipped rather than blocking selection of the in-scope one.
    /// </summary>
    [Fact]
    public void Derive_IgnoresOutsideRangeLoopback_AndSelectsInScope()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "172.16.150.253", Network = "172.16.150.253", Interface = "Loopback" },
            new OspfIpAddressRow { Address = "102.66.242.100", Network = "102.66.242.100", Interface = "Loopback" },
            new OspfIpAddressRow { Address = "10.16.254.1", Network = "10.16.254.1", Interface = "Loopback" },
        ]);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.254.1", result.RouterId);
        Assert.Equal("10.16.254.1/32", result.LoopbackCidr);
        Assert.Equal(2, result.IgnoredOutsideRangeAddresses.Count);
        Assert.Single(result.ObservedInScopeAddresses);
    }

    /// <summary>
    /// Scenario: the only loopback address is 172.16.150.253/32, outside the infrastructure range.
    /// Expected: derivation fails with an error containing "No in-scope".
    /// Invariant: an out-of-range address is never promoted to the router-id when nothing else is available.
    /// </summary>
    [Fact]
    public void Derive_Fails_WhenOnlyOutsideRangeLoopbackAddressesExist()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "172.16.150.253/32", Network = "172.16.150.253", Interface = "Loopback" },
        ]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("No in-scope", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: the <c>lo</c> row carries the unparseable address "not-an-ip/32".
    /// Expected: derivation fails with an error containing "not a valid IPv4" (case-insensitive).
    /// Invariant: malformed inventory data produces a clear failure instead of an exception or a bogus router-id.
    /// </summary>
    [Fact]
    public void Derive_Fails_OnMalformedAddress()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "not-an-ip/32", Network = "x", Interface = "lo" },
        ]);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("not a valid IPv4", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: <c>Loopback</c> holds the bare host address 10.16.0.11 with no prefix length.
    /// Expected: derivation succeeds with router-id 10.16.0.11 and loopback CIDR 10.16.0.11/32.
    /// Invariant: a prefix-less host address is treated as a /32.
    /// </summary>
    [Fact]
    public void Derive_NormalisesBareHostAddress_AsSlash32()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            new OspfIpAddressRow { Address = "10.16.0.11", Network = "10.16.0.11", Interface = "Loopback" },
        ]);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.0.11", result.RouterId);
        Assert.Equal("10.16.0.11/32", result.LoopbackCidr);
    }

    /// <summary>
    /// Scenario: interface-name and host-scope helpers are queried with a spread of names and addresses.
    /// Expected: "bridge-loopback", "lo0" and "ether1" are rejected; "LO" and "loopback" are accepted (case-insensitive); 10.16.0.11 is an infrastructure-scope host and 172.16.150.253 is not.
    /// Invariant: only the exact names <c>lo</c> and <c>Loopback</c> count as loopbacks, and only in-range hosts count as infrastructure scope.
    /// </summary>
    [Fact]
    public void IsSupportedLoopbackInterfaceName_RejectsUnrelatedNames()
    {
        Assert.False(OspfLoopbackRouterId.IsSupportedLoopbackInterfaceName("bridge-loopback"));
        Assert.False(OspfLoopbackRouterId.IsSupportedLoopbackInterfaceName("lo0"));
        Assert.False(OspfLoopbackRouterId.IsSupportedLoopbackInterfaceName("ether1"));
        Assert.True(OspfLoopbackRouterId.IsSupportedLoopbackInterfaceName("LO"));
        Assert.True(OspfLoopbackRouterId.IsSupportedLoopbackInterfaceName("loopback"));
        Assert.True(OspfLoopbackRouterId.IsInfrastructureScopeHost("10.16.0.11"));
        Assert.False(OspfLoopbackRouterId.IsInfrastructureScopeHost("172.16.150.253"));
    }
}
