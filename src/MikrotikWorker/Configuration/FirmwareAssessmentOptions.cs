namespace MikrotikWorker.Configuration;

/// <summary>
/// Read-only firmware compatibility assessment settings.
/// </summary>
public sealed class FirmwareAssessmentOptions
{
    public const string SectionName = "FirmwareAssessment";

    /// <summary>
    /// Target RouterOS v6 baseline. Default <c>6.49.23</c>. Must remain a v6 release.
    /// </summary>
    public string TargetRouterOsVersion { get; set; } = "6.49.23";
}
