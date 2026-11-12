// -----------------------------------------------------------------------------
// File: FixedRouterOsV7TargetProvider.cs
//
// Description:
// IRouterOsV7TargetProvider stub returning a fixed RouterOS 7 target snapshot.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
/// <summary>
/// Test double that returns a fixed RouterOS 7 target snapshot (or none).
/// </summary>
internal sealed class FixedRouterOsV7TargetProvider : IRouterOsV7TargetProvider
{
    /// <summary>Snapshot returned by every call, or <see langword="null"/> when no version was supplied.</summary>
    private readonly RouterOsV7TargetSnapshot? _snapshot;

    /// <summary>Creates a provider that returns a fixed snapshot built from <paramref name="versionText"/>.</summary>
    /// <param name="versionText">RouterOS version text; null or whitespace yields no snapshot.</param>
    /// <param name="sourcePath">Value reported as the snapshot source and <see cref="VersionFilePath"/>.</param>
    public FixedRouterOsV7TargetProvider(string? versionText, string sourcePath = "test-version")
    {
        VersionFilePath = sourcePath;
        if (string.IsNullOrWhiteSpace(versionText))
        {
            _snapshot = null;
            return;
        }

        RouterOsVersion version = RouterOsVersion.Parse(versionText);
        _snapshot = new RouterOsV7TargetSnapshot
        {
            Version = version,
            VersionText = version.ToString(),
            CapturedAtUtc = DateTimeOffset.Parse("2026-11-11T00:00:00Z"),
            SourcePath = sourcePath,
        };
    }

    /// <summary>Fake version file path supplied at construction.</summary>
    public string VersionFilePath { get; }

    /// <summary>Returns the fixed snapshot without reading any file.</summary>
    /// <returns>The fixed snapshot, or <see langword="null"/> when none was configured.</returns>
    public RouterOsV7TargetSnapshot? TryGetCurrent() => _snapshot;

    /// <summary>Returns the fixed snapshot without re-reading any file.</summary>
    /// <returns>The fixed snapshot, or <see langword="null"/> when none was configured.</returns>
    public RouterOsV7TargetSnapshot? CaptureSnapshot() => _snapshot;
}
