using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Ospf;

public sealed class OspfLoopbackRouterIdTests
{
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

    [Fact]
    public void Derive_AcceptsEstablishedLoopbackNameWithExactSlash32()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
            OspfTestBaselines.LoopbackSlash32("10.16.0.11", "Loopback"));

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.0.11", result.RouterId);
        Assert.Equal("Loopback", result.LoopbackInterfaceName);
    }

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

    [Fact]
    public void Derive_Fails_WhenLoopbackHasNoAddress_IsCoveredByAbsence()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive([]);
        Assert.False(result.Succeeded);
    }

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
