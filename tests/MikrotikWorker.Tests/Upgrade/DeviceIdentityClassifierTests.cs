// -----------------------------------------------------------------------------
// File: DeviceIdentityClassifierTests.cs
//
// Description:
// Unit tests for DeviceIdentityClassifier / DeviceType parsing and v7
// eligibility policy.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;

/// <summary>
/// Device identity string classification into ACE/AP/BR/DCL/Unknown and the
/// RouterOS 7 upgrade-eligibility policy attached to each result.
/// </summary>
public sealed class DeviceIdentityClassifierTests
{
    /// <summary>
    /// Standard dotted identities with ACE/AP/BR/DCL type prefixes (mixed casing).
    /// Expects DeviceType from the prefix, MatchedStandardFormat, identity echoed,
    /// and ShouldUpgradeToV7 for every standard fleet type.
    /// </summary>
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

    /// <summary>
    /// Non-standard, truncated, or prefix-poisoned identity strings.
    /// Expects DeviceType.Unknown with ShouldUpgradeToV7 still true and a
    /// policy reason mentioning unknown/non-standard — unknowns remain upgrade-eligible.
    /// </summary>
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

    /// <summary>
    /// Leading/trailing whitespace around an otherwise standard ACE identity.
    /// Expects trim before classification so DeviceType.Ace still matches and remains v7-eligible.
    /// </summary>
    [Fact]
    public void SurroundingWhitespace_IsTrimmedBeforeClassification()
    {
        DeviceIdentityClassification result = DeviceIdentityClassifier.Classify("  ACE01.POP001.001.core  ");

        Assert.Equal(DeviceType.Ace, result.DeviceType);
        Assert.True(result.ShouldUpgradeToV7);
    }

    /// <summary>
    /// Null and empty/whitespace identity inputs.
    /// Expects Unknown, MatchedStandardFormat false, and ShouldUpgradeToV7 true (fail-open).
    /// </summary>
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

    /// <summary>
    /// Direct ShouldUpgradeToV7 checks for every DeviceType enum value.
    /// Expects true for Ace, Br, Ap, Dcl, and Unknown — no type is excluded from RouterOS 7.
    /// </summary>
    [Fact]
    public void AllDeviceTypes_AreEligibleForRouterOs7()
    {
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Ace));
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Br));
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Ap));
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Dcl));
        Assert.True(DeviceIdentityClassifier.ShouldUpgradeToV7(DeviceType.Unknown));
    }

    /// <summary>
    /// AP-prefixed identity whose later component contains "ACE".
    /// Expects DeviceType.Ap and v7 eligibility — classification uses the leading component only.
    /// </summary>
    [Fact]
    public void LaterComponentContainingAce_DoesNotClassifyAsAce()
    {
        DeviceIdentityClassification result = DeviceIdentityClassifier.Classify("AP01.POP001.001.ACE-backbone");

        Assert.Equal(DeviceType.Ap, result.DeviceType);
        Assert.True(result.ShouldUpgradeToV7);
    }

    /// <summary>
    /// DCL-prefixed identity whose later component contains "BR".
    /// Expects DeviceType.Dcl and v7 eligibility — substring "BR" must not override the prefix.
    /// </summary>
    [Fact]
    public void LaterComponentContainingBr_DoesNotClassifyAsBr()
    {
        DeviceIdentityClassification result = DeviceIdentityClassifier.Classify("DCL01.POP001.001.BR-edge");

        Assert.Equal(DeviceType.Dcl, result.DeviceType);
        Assert.True(result.ShouldUpgradeToV7);
    }
}
