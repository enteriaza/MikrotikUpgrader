// -----------------------------------------------------------------------------
// File: InstalledPackageInfo.cs
//
// Description:
// Immutable DTOs for /system/package/print rows and /file/print metadata used
// by assessment, upgrade package selection, and backup file readiness polls.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.MikroTik;

/// <summary>
/// One row from <c>/system/package/print</c>.
/// When <see cref="Bundle"/> is set, the package is a component of that combined RouterOS package
/// (Winbox shows it indented under the main package). Independent optionals have a null/empty bundle.
/// </summary>
/// <param name="Name">Installed package name (e.g. <c>routeros</c>, <c>routing</c>).</param>
/// <param name="Version">Installed package version string from RouterOS.</param>
/// <param name="Disabled">Whether the package is disabled on the device.</param>
/// <param name="Bundle">Parent combined package name when this row is a bundled component; otherwise null/empty.</param>
public sealed record InstalledPackageInfo(
    string Name,
    string Version,
    bool Disabled,
    string? Bundle = null)
{
    /// <summary>
    /// True when this package is bundled inside a combined <c>routeros-*</c> package.
    /// </summary>
    public bool IsBundledComponent => !string.IsNullOrWhiteSpace(Bundle);
}

/// <summary>
/// Metadata for one RouterOS <c>/file</c> entry used during backup readiness and fetch verification.
/// </summary>
/// <param name="Name">Exact file name on the router.</param>
/// <param name="SizeBytes">Reported size in bytes.</param>
/// <param name="Id">Optional RouterOS <c>.id</c> when available.</param>
public sealed record RouterFileInfo(
    string Name,
    long SizeBytes,
    string? Id = null);
