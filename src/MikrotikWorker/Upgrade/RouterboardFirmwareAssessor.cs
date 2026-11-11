using MikrotikWorker.Assessment;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Decides whether <c>/system/routerboard/upgrade</c> is required based on RouterBOOT
/// <c>current-firmware</c> vs <c>upgrade-firmware</c> (not RouterOS package versions).
/// </summary>
internal static class RouterboardFirmwareAssessor
{
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

internal enum RouterboardUpgradeDecision
{
    UpgradeRequired = 0,
    NotRequired = 1,
    Inconclusive = 2,
}

internal sealed record RouterboardUpgradeAssessment(
    RouterboardUpgradeDecision Decision,
    string? CurrentFirmwareRaw,
    string? UpgradeFirmwareRaw,
    string Reason);
