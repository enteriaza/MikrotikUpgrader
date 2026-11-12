// -----------------------------------------------------------------------------
// File: RouterboardFirmwareAssessorTests.cs
//
// Description:
// Unit tests for RouterboardFirmwareAssessor upgrade-required decisions.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;
/// <summary>
/// Unit tests for RouterboardFirmwareAssessor decisions.
/// </summary>
public sealed class RouterboardFirmwareAssessorTests
{
    /// <summary>
    /// Current-firmware 7.9 against upgrade-firmware 7.10 must give
    /// <c>RouterboardUpgradeDecision.UpgradeRequired</c>.
    /// </summary>
    [Fact]
    public void UpgradeRequired_WhenUpgradeFirmwareNewer()
    {
        RouterboardUpgradeAssessment result = RouterboardFirmwareAssessor.Assess(Device("7.9", "7.10"));
        Assert.Equal(RouterboardUpgradeDecision.UpgradeRequired, result.Decision);
    }

    /// <summary>
    /// Identical current and upgrade firmware (7.23.8 and 7.23.8) must give
    /// <c>RouterboardUpgradeDecision.NotRequired</c>.
    /// </summary>
    [Fact]
    public void NotRequired_WhenEqual()
    {
        RouterboardUpgradeAssessment result = RouterboardFirmwareAssessor.Assess(Device("7.23.8", "7.23.8"));
        Assert.Equal(RouterboardUpgradeDecision.NotRequired, result.Decision);
    }

    /// <summary>
    /// Current-firmware 7.23.8 against an older upgrade-firmware 6.49.23 must give <c>NotRequired</c>, and the
    /// explanation must mention 'downgrade' so an older staged loader is never applied.
    /// </summary>
    [Fact]
    public void NotRequired_WhenCurrentNewer_NoDowngrade()
    {
        RouterboardUpgradeAssessment result = RouterboardFirmwareAssessor.Assess(Device("7.23.8", "6.49.23"));
        Assert.Equal(RouterboardUpgradeDecision.NotRequired, result.Decision);
        Assert.Contains("downgrade", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pins numeric rather than lexical ordering: 7.10 is newer than 7.9, so current 7.9 with upgrade 7.10 gives
    /// <c>UpgradeRequired</c>.
    /// </summary>
    [Fact]
    public void NumericComparison_SevenTenGreaterThanSevenNine()
    {
        RouterboardUpgradeAssessment result = RouterboardFirmwareAssessor.Assess(Device("7.9", "7.10"));
        Assert.Equal(RouterboardUpgradeDecision.UpgradeRequired, result.Decision);
    }

    /// <summary>
    /// Each InlineData pair, where current or upgrade firmware is null, empty or unparseable ('not-a-version',
    /// 'bad'), must give <c>RouterboardUpgradeDecision.Inconclusive</c> rather than a guess.
    /// </summary>
    [Theory]
    [InlineData(null, "7.23.8")]
    [InlineData("", "7.23.8")]
    [InlineData("7.23.8", null)]
    [InlineData("7.23.8", "")]
    [InlineData("not-a-version", "7.23.8")]
    [InlineData("7.23.8", "bad")]
    public void Inconclusive_WhenFieldsMissingOrMalformed(string? current, string? upgrade)
    {
        RouterboardUpgradeAssessment result = RouterboardFirmwareAssessor.Assess(Device(current, upgrade));
        Assert.Equal(RouterboardUpgradeDecision.Inconclusive, result.Decision);
    }

    /// <summary>
    /// Builds a mipsbe 960PGS device on RouterOS 7.23.8 with the given current and upgrade RouterBOARD firmware
    /// versions.
    /// </summary>
    private static MikrotikDeviceInfo Device(string? current, string? upgrade) =>
        new()
        {
            Identity = "AP01.POP001.001.access",
            Version = "7.23.8",
            ArchitectureName = "mipsbe",
            RouterboardCurrentFirmware = current,
            RouterboardUpgradeFirmware = upgrade,
            RouterboardModel = "960PGS",
            RouterboardFirmwareType = "qca9550L",
            RouterboardFactoryFirmware = "3.41",
        };
}
