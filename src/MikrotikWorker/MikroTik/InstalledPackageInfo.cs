namespace MikrotikWorker.MikroTik;

/// <summary>
/// One row from <c>/system/package/print</c>.
/// When <see cref="Bundle"/> is set, the package is a component of that combined RouterOS package
/// (Winbox shows it indented under the main package). Independent optionals have a null/empty bundle.
/// </summary>
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

public sealed record RouterFileInfo(
    string Name,
    long SizeBytes,
    string? Id = null);
