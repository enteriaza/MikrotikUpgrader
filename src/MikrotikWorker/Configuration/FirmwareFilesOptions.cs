// -----------------------------------------------------------------------------
// File: FirmwareFilesOptions.cs
//
// Description:
// Options bound from the "FirmwareFiles" section locating the on-disk MikroTik
// package catalogue served at the fixed HTTP request path /firmware_files.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Configuration;

/// <summary>
/// Location of MikroTik firmware packages served over HTTP.
/// </summary>
public sealed class FirmwareFilesOptions
{
    /// <summary>Configuration section name: <c>FirmwareFiles</c>.</summary>
    public const string SectionName = "FirmwareFiles";

    /// <summary>
    /// Fixed HTTP request path under which packages are served: <c>/firmware_files</c>.
    /// </summary>
    public const string RequestPath = "/firmware_files";

    /// <summary>
    /// Absolute path, or path relative to <see cref="AppContext.BaseDirectory"/>.
    /// Default: <c>FirmwareFiles</c>.
    /// </summary>
    public string Path { get; set; } = "FirmwareFiles";

    /// <summary>
    /// Resolves <see cref="Path"/> against <paramref name="baseDirectory"/>,
    /// defaulting to <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Path"/> is empty.</exception>
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
