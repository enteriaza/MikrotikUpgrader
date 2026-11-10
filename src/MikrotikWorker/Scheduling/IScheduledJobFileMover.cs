namespace MikrotikWorker.Scheduling;

internal interface IScheduledJobFileMover
{
    string MoveSafely(
        string sourcePath,
        string destinationDirectory,
        string preferredFileName,
        ILogger? logger = null);
}

internal sealed class FileSystemScheduledJobFileMover : IScheduledJobFileMover
{
    public string MoveSafely(
        string sourcePath,
        string destinationDirectory,
        string preferredFileName,
        ILogger? logger = null) =>
        ScheduledJobFileMover.MoveSafely(sourcePath, destinationDirectory, preferredFileName, logger);
}
