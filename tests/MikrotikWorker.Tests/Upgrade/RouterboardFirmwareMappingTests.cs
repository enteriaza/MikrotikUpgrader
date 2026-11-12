// -----------------------------------------------------------------------------
// File: RouterboardFirmwareMappingTests.cs
//
// Description:
// RouterBOARD /system/routerboard/print fields are RouterBOOT loader versions,
// not RouterOS. Live observation after ROS 7 install without
// /system/routerboard/upgrade: current-firmware and upgrade-firmware values
// changed while no upgrade command was issued. Mapping must preserve raw API
// field names; do not invent a swap correction. External MikroTik docs:
// current-firmware = loader in use; upgrade-firmware = newest loader file
// found on the device (often staged by a RouterOS package).
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade;
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
    /// <summary>
    /// Feeds fake identity, resource and <c>/system/routerboard/print</c> replies through
    /// <c>ReadDeviceInfoAsync</c> and asserts each RouterBOARD property carries its own raw API field: model
    /// <c>RB960PGS</c>, firmware type <c>qca9550L</c>, factory <c>3.41</c>, current-firmware <c>6.49.23</c> and
    /// upgrade-firmware <c>7.23.8</c>. The deliberately 'wrong-looking' current/upgrade pair must come through
    /// unswapped.
    /// </summary>
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

    /// <summary>
    /// A 7.23.8 router whose loader reports current-firmware 6.49.23 and upgrade-firmware 7.23.8 must be assessed
    /// as <c>RouterboardFirmwareStatus.UpgradeAvailable</c>, with both raw values echoed on the assessment. The
    /// <c>routerboard.upgrade_available</c> finding must state that assessment does not issue
    /// <c>/system/routerboard/upgrade</c>, and some finding must describe the loader as 'bootloader' or
    /// 'RouterBOOT' so it is not read as a RouterOS version.
    /// </summary>
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

    /// <summary>
    /// Pre-ROS7 lab observation: current-firmware 7.23.8, upgrade-firmware 6.49.23.
    /// With current-firmware 7.23.8 ahead of upgrade-firmware 6.49.23, the assessment must report
    /// <c>RouterboardFirmwareStatus.Current</c> rather than UpgradeAvailable, keep both raw values unswapped, and
    /// record a <c>routerboard.current_ahead</c> finding.
    /// </summary>
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
