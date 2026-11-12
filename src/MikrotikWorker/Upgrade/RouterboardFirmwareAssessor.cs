// -----------------------------------------------------------------------------
// File: RouterboardFirmwareAssessor.cs
//
// Description:
// Decides whether /system/routerboard/upgrade is required by comparing RouterBOOT
// current-firmware vs upgrade-firmware (not RouterOS package versions).
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Decides whether <c>/system/routerboard/upgrade</c> is required based on RouterBOOT
/// <c>current-firmware</c> vs <c>upgrade-firmware</c> (not RouterOS package versions).
/// </summary>
internal static class RouterboardFirmwareAssessor
{
    /// <summary>
    /// Assesses RouterBOARD upgrade need from device info fields already read from the router.
    /// Unparseable fields yield <see cref="RouterboardUpgradeDecision.Inconclusive"/> (no upgrade issued).
    /// </summary>
    public static RouterboardUpgradeAssessment Assess(MikrotikDeviceInfo deviceInfo)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);

        string? currentRaw = deviceInfo.RouterboardCurrentFirmware;
        string? upgradeRaw = deviceInfo.RouterboardUpgradeFirmware;

        bool hasCurrent = RouterOsVersion.TryParse(currentRaw, out RouterOsVersion current);
        bool hasUpgrade = RouterOsVersion.TryParse(upgradeRaw, out RouterOsVersion upgrade);

        if (!hasCurrent || !hasUpgrade)
        {
            return new RouterboardUpgradeAssessment(
                RouterboardUpgradeDecision.Inconclusive,
                currentRaw,
                upgradeRaw,
                "RouterBOARD current-firmware/upgrade-firmware could not both be parsed; upgrade will not be issued.");
        }

        int cmp = current.CompareTo(upgrade);
        if (cmp == 0)
        {
            return new RouterboardUpgradeAssessment(
                RouterboardUpgradeDecision.NotRequired,
                currentRaw,
                upgradeRaw,
                $"RouterBOOT current-firmware ({current}) matches upgrade-firmware; no RouterBOARD upgrade required.");
        }

        if (cmp > 0)
        {
            return new RouterboardUpgradeAssessment(
                RouterboardUpgradeDecision.NotRequired,
                currentRaw,
                upgradeRaw,
                $"RouterBOOT current-firmware ({current}) is newer than upgrade-firmware ({upgrade}); downgrade is not performed.");
        }

        return new RouterboardUpgradeAssessment(
            RouterboardUpgradeDecision.UpgradeRequired,
            currentRaw,
            upgradeRaw,
            $"RouterBOOT upgrade-firmware ({upgrade}) is newer than current-firmware ({current}); RouterBOARD upgrade is required.");
    }
}

/// <summary>
/// Decision from <see cref="RouterboardFirmwareAssessor.Assess"/>.
/// </summary>
internal enum RouterboardUpgradeDecision
{
    /// <summary>upgrade-firmware is newer than current-firmware; upgrade should be issued.</summary>
    UpgradeRequired = 0,

    /// <summary>Current matches or is newer than upgrade-firmware; no upgrade / no downgrade.</summary>
    NotRequired = 1,

    /// <summary>Fields could not both be parsed; assessor refuses to authorize an upgrade.</summary>
    Inconclusive = 2,
}

/// <summary>
/// Assessment result including raw firmware strings and a human-readable reason.
/// </summary>
/// <param name="Decision">Upgrade decision.</param>
/// <param name="CurrentFirmwareRaw">Raw <c>current-firmware</c> from the device.</param>
/// <param name="UpgradeFirmwareRaw">Raw <c>upgrade-firmware</c> from the device.</param>
/// <param name="Reason">Explanation suitable for logs and job detail.</param>
internal sealed record RouterboardUpgradeAssessment(
    RouterboardUpgradeDecision Decision,
    string? CurrentFirmwareRaw,
    string? UpgradeFirmwareRaw,
    string Reason);
