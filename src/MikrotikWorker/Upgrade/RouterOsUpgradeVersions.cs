// -----------------------------------------------------------------------------
// File: RouterOsUpgradeVersions.cs
//
// Description:
// Fixed RouterOS upgrade milestones that are not configuration-driven
// (currently the v6 stepping release 6.49.23).
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Fixed RouterOS upgrade milestones that are not configuration-driven.
/// </summary>
internal static class RouterOsUpgradeVersions
{
    /// <summary>
    /// Fixed RouterOS v6 stepping release. Devices older than this on v6 must step through it
    /// before RouterOS 7. Devices already on this release or newer v6 never downgrade to it.
    /// </summary>
    public const string V6SteppingVersionText = "6.49.23";

    /// <summary>
    /// Parsed <see cref="V6SteppingVersionText"/> used for version comparisons.
    /// </summary>
    public static RouterOsVersion V6Stepping { get; } = RouterOsVersion.Parse(V6SteppingVersionText);
}
