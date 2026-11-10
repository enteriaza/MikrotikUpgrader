namespace MikrotikWorker.Scheduling;

internal static class ScheduledJobFileMover
{
    /// <summary>
    /// Moves <paramref name="sourcePath"/> into <paramref name="destinationDirectory"/> using the preferred file name.
    /// On collision, uses a timestamped unique name instead of overwriting.
    /// </summary>
    /// <returns>The full destination path actually used.</returns>
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
