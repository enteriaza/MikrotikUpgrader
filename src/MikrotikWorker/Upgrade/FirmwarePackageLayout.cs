// -----------------------------------------------------------------------------
// File: FirmwarePackageLayout.cs
//
// Description:
// Distinguishes combined RouterOS package installations from individual-package
// (legacy split) installations when resolving upgrade .npk sets.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Target package layout selected for an upgrade stage from the target catalogue
/// (combined <c>routeros</c> versus individual <c>system</c>).
/// </summary>
public enum FirmwarePackageLayout
{
    /// <summary>Layout was not established (resolution failed before classification).</summary>
    Unknown = 0,

    /// <summary>
    /// Target core is the combined main package (<c>routeros</c> / <c>routeros-{arch}</c>);
    /// bundled components ride inside that package.
    /// </summary>
    Combined = 1,

    /// <summary>
    /// Target core is the individual <c>system</c> package. Independents are selected only when the
    /// target catalogue still distributes them for the device architecture.
    /// </summary>
    Individual = 2,
}
