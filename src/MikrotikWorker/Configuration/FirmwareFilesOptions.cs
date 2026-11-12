namespace MikrotikWorker.Configuration;

/// <summary>
/// Location of MikroTik firmware packages served over HTTP.
/// </summary>
public sealed class FirmwareFilesOptions
{
    public const string SectionName = "FirmwareFiles";
    public const string RequestPath = "/firmware_files";

    /// <summary>
    /// Absolute path, or path relative to <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public string Path { get; set; } = "FirmwareFiles";

    /// <summary>
    /// Resolves <see cref="Path"/> against <paramref name="baseDirectory"/>,
    /// defaulting to <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public string GetAbsolutePath(string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new InvalidOperationException("FirmwareFiles:Path must be configured.");
        }

        string root = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        return System.IO.Path.IsPathRooted(Path)
            ? System.IO.Path.GetFullPath(Path)
            : System.IO.Path.GetFullPath(Path, root);
    }
}
