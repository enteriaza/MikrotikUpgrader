// -----------------------------------------------------------------------------
// File: ScheduledJobFileMover.cs
//
// Description:
// Static helper that performs non-destructive CSV moves across ScheduledJobs
// lifecycle folders. On destination name collision it chooses a timestamped
// unique name instead of overwriting, preserving both files for inspection.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Filesystem helper for moving scheduled CSV files without overwriting existing destinations.
/// </summary>
internal static class ScheduledJobFileMover
{
    /// <summary>
    /// Moves <paramref name="sourcePath"/> into <paramref name="destinationDirectory"/> using the preferred file name.
    /// On collision, uses a timestamped unique name instead of overwriting.
    /// </summary>
    /// <param name="sourcePath">Existing file to move.</param>
    /// <param name="destinationDirectory">Target folder; created when absent.</param>
    /// <param name="preferredFileName">Desired destination file name.</param>
    /// <param name="logger">Optional logger for collision warnings.</param>
    /// <returns>The full destination path actually used.</returns>
    /// <exception cref="ArgumentException">Thrown when any path argument is null or whitespace.</exception>
    public static string MoveSafely(
        string sourcePath,
        string destinationDirectory,
        string preferredFileName,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredFileName);

        Directory.CreateDirectory(destinationDirectory);
        string destinationPath = Path.Combine(destinationDirectory, preferredFileName);

        try
        {
            File.Move(sourcePath, destinationPath, overwrite: false);
            return destinationPath;
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            string uniqueName =
                $"{Path.GetFileNameWithoutExtension(preferredFileName)}.{DateTime.UtcNow:yyyyMMddHHmmssfff}{Path.GetExtension(preferredFileName)}";
            string uniquePath = Path.Combine(destinationDirectory, uniqueName);

            logger?.LogWarning(
                "Destination collision for {PreferredFileName}; moving to {UniqueFileName}",
                preferredFileName,
                uniqueName);

            File.Move(sourcePath, uniquePath, overwrite: false);
            return uniquePath;
        }
    }
}
