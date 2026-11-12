using MikrotikWorker.Assessment;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
/// <summary>
/// Test double that returns a fixed RouterOS 7 target snapshot (or none).
/// </summary>
internal sealed class FixedRouterOsV7TargetProvider : IRouterOsV7TargetProvider
{
    private readonly RouterOsV7TargetSnapshot? _snapshot;

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

    public string VersionFilePath { get; }

    public RouterOsV7TargetSnapshot? TryGetCurrent() => _snapshot;

    public RouterOsV7TargetSnapshot? CaptureSnapshot() => _snapshot;
}
