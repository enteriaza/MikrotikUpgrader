namespace MikrotikWorker.Upgrade;

/// <summary>
/// Per-job upgrade stage cap. Independent of device-identity v7 eligibility policy.
/// </summary>
public enum UpgradeExecutionMode
{
    /// <summary>
    /// Production default: v6 stepping when required, then RouterOS 7 when identity policy allows.
    /// </summary>
    FullUpgrade = 0,

    /// <summary>
    /// Stop after successful v6 stepping verification (or when already at/above the v6 stepping
    /// release). Never enters the RouterOS 7 stage.
    /// </summary>
    V6SteppingOnly = 1,
}

internal static class UpgradeExecutionModeParser
{
    public static bool TryParse(string? raw, out UpgradeExecutionMode mode)
    {
        mode = UpgradeExecutionMode.FullUpgrade;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.Equals(nameof(UpgradeExecutionMode.FullUpgrade), StringComparison.OrdinalIgnoreCase))
        {
            mode = UpgradeExecutionMode.FullUpgrade;
            return true;
        }

        if (trimmed.Equals(nameof(UpgradeExecutionMode.V6SteppingOnly), StringComparison.OrdinalIgnoreCase))
        {
            mode = UpgradeExecutionMode.V6SteppingOnly;
            return true;
        }

        return false;
    }
}
