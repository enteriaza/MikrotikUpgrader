using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests;

public sealed class DeviceIdentityClassifierTests
{
    [Theory]
    [InlineData("ACE01.POP001.001.core", DeviceType.Ace)]
    [InlineData("ACE12.POP123.456.edge", DeviceType.Ace)]
    [InlineData("ace01.pop001.001.core", DeviceType.Ace)]
    [InlineData("Ace12.Pop123.456.Edge", DeviceType.Ace)]
    [InlineData("AP01.POP001.001.access", DeviceType.Ap)]
    [InlineData("AP12.POP123.456.distribution", DeviceType.Ap)]
    [InlineData("ap01.pop001.001.access", DeviceType.Ap)]
    [InlineData("BR01.POP001.001.core", DeviceType.Br)]
    [InlineData("BR12.POP123.456.edge", DeviceType.Br)]
    [InlineData("br01.pop001.001.core", DeviceType.Br)]
    [InlineData("DCL01.POP001.001.access", DeviceType.Dcl)]
    [InlineData("DCL12.POP123.456.some.device.name", DeviceType.Dcl)]
    [InlineData("dcl01.pop001.001.access", DeviceType.Dcl)]
    public void StandardIdentities_ClassifyByTypePrefix(string identity, DeviceType expected)
    {
        DeviceIdentityClassification result = DeviceIdentityClassifier.Classify(identity);

        Assert.Equal(expected, result.DeviceType);
        Assert.True(result.MatchedStandardFormat);
        Assert.Equal(identity, result.Identity);
        Assert.True(result.ShouldUpgradeToV7);
    }

    [Theory]
    [InlineData("UNKNOWN01.POP001.001.device")]
    [InlineData("MikroTik")]
    [InlineData("router-01")]
    [InlineData("XACE01.POP001.001.core")]
    [InlineData("ACE1.POP001.001.core")]
    [InlineData("ACE01.POP01.001.core")]
    [InlineData("ACE01.POP001.01.core")]
    [InlineData("ACE01.POP001.001")]
    [InlineData("ACE01..POP001.001.core")]
    [InlineData("ACE01.POP001..core")]
    [InlineData("prefix.ACE01.POP001.001.core")]
    [InlineData("not-a-standard-identity")]
    public void NonStandardOrMalformed_ClassifyAsUnknownAndUpgrade(string identity)
    {
        DeviceIdentityClassification result = DeviceIdentityClassifier.Classify(identity);

        Assert.Equal(DeviceType.Unknown, result.DeviceType);
        Assert.True(result.ShouldUpgradeToV7);
        Assert.Contains("unknown or non-standard", result.PolicyReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SurroundingWhitespace_IsTrimmedBeforeClassification()
    {
        DeviceIdentityClassification result = DeviceIdentityClassifier.Classify("  ACE01.POP001.001.core  ");

        Assert.Equal(DeviceType.Ace, result.DeviceType);
        Assert.True(result.ShouldUpgradeToV7);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmptyIdentity_ClassifiesUnknownAndUpgrades(string? identity)
    {
        DeviceIdentityClassification result = DeviceIdentityClassifier.Classify(identity);

        Assert.Equal(DeviceType.Unknown, result.DeviceType);
        Assert.False(result.MatchedStandardFormat);
        Assert.True(result.ShouldUpgradeToV7);
    }

    [Fact]
    public void AllDeviceTypes_AreEligibleForRouterOs7()
    {
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Ace));
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Br));
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Ap));
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Dcl));
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Unknown));
    }

    [Fact]
    public void LaterComponentContainingAce_DoesNotClassifyAsAce()
    {
        DeviceIdentityClassification result = DeviceIdentityClassifier.Classify("AP01.POP001.001.ACE-backbone");

        Assert.Equal(DeviceType.Ap, result.DeviceType);
        Assert.True(result.ShouldUpgradeToV7);
    }

    [Fact]
    public void LaterComponentContainingBr_DoesNotClassifyAsBr()
    {
        DeviceIdentityClassification result = DeviceIdentityClassifier.Classify("DCL01.POP001.001.BR-edge");

        Assert.Equal(DeviceType.Dcl, result.DeviceType);
        Assert.True(result.ShouldUpgradeToV7);
    }
}
