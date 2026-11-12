using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;
public sealed class RouterboardFirmwareAssessorTests
{
    [Fact]
    public void UpgradeRequired_WhenUpgradeFirmwareNewer()
    {
        RouterboardUpgradeAssessment result = RouterboardFirmwareAssessor.Assess(Device("7.9", "7.10"));
        Assert.Equal(RouterboardUpgradeDecision.UpgradeRequired, result.Decision);
    }

    [Fact]
    public void NotRequired_WhenEqual()
    {
        RouterboardUpgradeAssessment result = RouterboardFirmwareAssessor.Assess(Device("7.23.8", "7.23.8"));
        Assert.Equal(RouterboardUpgradeDecision.NotRequired, result.Decision);
    }

    [Fact]
    public void NotRequired_WhenCurrentNewer_NoDowngrade()
    {
        RouterboardUpgradeAssessment result = RouterboardFirmwareAssessor.Assess(Device("7.23.8", "6.49.23"));
        Assert.Equal(RouterboardUpgradeDecision.NotRequired, result.Decision);
        Assert.Contains("downgrade", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NumericComparison_SevenTenGreaterThanSevenNine()
    {
        RouterboardUpgradeAssessment result = RouterboardFirmwareAssessor.Assess(Device("7.9", "7.10"));
        Assert.Equal(RouterboardUpgradeDecision.UpgradeRequired, result.Decision);
    }

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
