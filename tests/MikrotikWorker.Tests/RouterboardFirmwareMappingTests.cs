using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests;

/// <summary>
/// RouterBOARD /system/routerboard/print fields are RouterBOOT loader versions, not RouterOS.
/// Live observation after ROS 7 install without /system/routerboard/upgrade:
/// current-firmware and upgrade-firmware values changed while no upgrade command was issued.
/// Mapping must preserve raw API field names; do not invent a swap correction.
/// External MikroTik docs: current-firmware = loader in use; upgrade-firmware = newest
/// loader file found on the device (often staged by a RouterOS package).
/// </summary>
public sealed class RouterboardFirmwareMappingTests
{
    [Fact]
    public async Task ApiClient_MapsRawRouterboardFields_WithoutSwap()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/identity/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["name"] = "Mikrotik" }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["version"] = "7.23.8 (long-term)",
                        ["architecture-name"] = "mipsbe",
                        ["board-name"] = "hEX PoE",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/routerboard/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["model"] = "RB960PGS",
                        ["firmware-type"] = "qca9550L",
                        ["factory-firmware"] = "3.41",
                        ["current-firmware"] = "6.49.23",
                        ["upgrade-firmware"] = "7.23.8",
                    }),
                    new TikFakeDoneSentence(),
                ]);

        IMikrotikRouterOperations client = new MikrotikApiClient(
            new ThrowingMikrotikConnectionFactory(new IOException("unused")),
            Options.Create(new MikroTikOptions { Enabled = false, Port = 8728 }),
            NullLogger<MikrotikApiClient>.Instance);

        MikrotikDeviceInfo info = await client.ReadDeviceInfoAsync(fake, "198.18.240.11");

        Assert.Equal("RB960PGS", info.RouterboardModel);
        Assert.Equal("qca9550L", info.RouterboardFirmwareType);
        Assert.Equal("3.41", info.RouterboardFactoryFirmware);
        Assert.Equal("6.49.23", info.RouterboardCurrentFirmware);
        Assert.Equal("7.23.8", info.RouterboardUpgradeFirmware);
    }

    [Fact]
    public void Assessor_PreservesUpgradeAvailable_WithoutClaimingUpgradeExecuted()
    {
        string root = Directory.CreateTempSubdirectory("rb-fw-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [1]);
        DeviceCompatibilityAssessor assessor = new(
            new StaticFirmwarePackageCatalog(root),
            new FixedRouterOsV7TargetProvider("7.23.8"));

        MikrotikDeviceInfo device = new()
        {
            Identity = "Mikrotik",
            Version = "7.23.8 (long-term)",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardModel = "RB960PGS",
            RouterboardFirmwareType = "qca9550L",
            RouterboardFactoryFirmware = "3.41",
            RouterboardCurrentFirmware = "6.49.23",
            RouterboardUpgradeFirmware = "7.23.8",
        };

        DeviceCompatibilityAssessment assessment = assessor.Assess(
            "198.18.240.11",
            device,
            DateTimeOffset.Parse("2026-11-11T10:00:00Z"));

        Assert.Equal(RouterboardFirmwareStatus.UpgradeAvailable, assessment.RouterboardAssessment);
        Assert.Equal("6.49.23", assessment.RouterboardCurrentFirmware);
        Assert.Equal("7.23.8", assessment.RouterboardUpgradeFirmware);
        Assert.Contains(
            assessment.Findings,
            static f => f.Code == "routerboard.upgrade_available"
                && f.Message.Contains("Assessment does not issue /system/routerboard/upgrade", StringComparison.Ordinal));
        Assert.Contains(
            assessment.Findings,
            static f => f.Message.Contains("bootloader", StringComparison.OrdinalIgnoreCase)
                || f.Message.Contains("RouterBOOT", StringComparison.Ordinal));
    }

    [Fact]
    public void Assessor_CurrentAheadOfUpgrade_IsNotUpgradeAvailable()
    {
        // Pre-ROS7 lab observation: current-firmware 7.23.8, upgrade-firmware 6.49.23.
        string root = Directory.CreateTempSubdirectory("rb-fw2-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1]);
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [1]);
        DeviceCompatibilityAssessor assessor = new(
            new StaticFirmwarePackageCatalog(root),
            new FixedRouterOsV7TargetProvider("7.23.8"));

        MikrotikDeviceInfo device = new()
        {
            Identity = "Mikrotik",
            Version = "6.49.23 (stable)",
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
            RouterboardModel = "960PGS",
            RouterboardCurrentFirmware = "7.23.8",
            RouterboardUpgradeFirmware = "6.49.23",
            RouterboardFactoryFirmware = "3.41",
        };

        DeviceCompatibilityAssessment assessment = assessor.Assess(
            "198.18.240.11",
            device,
            DateTimeOffset.Parse("2026-11-11T10:00:00Z"));

        Assert.Equal(RouterboardFirmwareStatus.Current, assessment.RouterboardAssessment);
        Assert.Equal("7.23.8", assessment.RouterboardCurrentFirmware);
        Assert.Equal("6.49.23", assessment.RouterboardUpgradeFirmware);
        Assert.Contains(assessment.Findings, static f => f.Code == "routerboard.current_ahead");
    }
}
