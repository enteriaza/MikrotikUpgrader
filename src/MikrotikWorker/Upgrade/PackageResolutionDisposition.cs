// -----------------------------------------------------------------------------
// File: PackageResolutionDisposition.cs
//
// Description:
// Structured dispositions for target-release-driven package resolution
// diagnostics. Distinguishes selected packages from normal MikroTik
// distribution differences (discontinued or architecture-exclusive packages).
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade;

/// <summary>
/// How a source independent package was treated relative to the target catalogue.
/// </summary>
public enum PackageResolutionDisposition
{
    /// <summary>Package was selected for download/transfer from the target catalogue.</summary>
    Selected = 0,

    /// <summary>Package is the target core distribution (<c>routeros</c> or <c>system</c>).</summary>
    CorePackage = 1,

    /// <summary>
    /// No package with this identity exists in the target release for any architecture.
    /// </summary>
    NotDistributedInTargetRelease = 2,

    /// <summary>
    /// A same-name package exists in the target release, but not for the device architecture.
    /// </summary>
    NotDistributedForTargetArchitecture = 3,

    /// <summary>
    /// Source package has no separate installable counterpart in the target catalogue
    /// (discontinued, incorporated, or otherwise not separately shipped).
    /// </summary>
    NoSeparateTargetCounterpart = 4,

    /// <summary>Package was rejected (ambiguous candidates or contradictory selection).</summary>
    Rejected = 5,
}

/// <summary>
/// One structured package-resolution diagnostic row.
/// </summary>
internal sealed class PackageResolutionDiagnostic
{
    /// <summary>Source/catalogue package identity.</summary>
    public required string PackageIdentity { get; init; }

    /// <summary>Source installed version text when known.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>Target RouterOS version text.</summary>
    public required string TargetVersion { get; init; }

    /// <summary>Normalised device architecture.</summary>
    public required string TargetArchitecture { get; init; }

    /// <summary>Final disposition.</summary>
    public required PackageResolutionDisposition Disposition { get; init; }

    /// <summary>Human-readable explanation (no secrets).</summary>
    public required string Detail { get; init; }

    /// <summary>
    /// Architectures that ship this package in the target release, when known.
    /// </summary>
    public IReadOnlyList<string> OtherArchitectures { get; init; } = [];

    /// <summary>Formats a single-line diagnostic suitable for logging.</summary>
    public override string ToString()
    {
        string others = OtherArchitectures.Count == 0
            ? string.Empty
            : $" otherArchitectures=[{string.Join(", ", OtherArchitectures)}]";
        return $"disposition={Disposition} package={PackageIdentity} sourceVersion={SourceVersion ?? "<unknown>"} "
            + $"targetVersion={TargetVersion} targetArchitecture={TargetArchitecture}{others}: {Detail}";
    }
}
