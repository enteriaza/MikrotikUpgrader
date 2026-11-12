// -----------------------------------------------------------------------------
// File: IScheduledJobFileMover.cs
//
// Description:
// Abstraction over safe CSV lifecycle moves between ScheduledJobs folders
// (Incoming → Processing → Accepted/Rejected/Completed). The default
// implementation delegates to <see cref="ScheduledJobFileMover"/> so tests can
// substitute movers without touching static helpers.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Moves scheduled-job CSV files between lifecycle directories without overwriting on name collision.
/// </summary>
internal interface IScheduledJobFileMover
{
    /// <summary>
    /// Moves <paramref name="sourcePath"/> into <paramref name="destinationDirectory"/> using
    /// <paramref name="preferredFileName"/>, falling back to a timestamped unique name on collision.
    /// </summary>
    /// <param name="sourcePath">Existing file to move.</param>
    /// <param name="destinationDirectory">Target folder (created if missing).</param>
    /// <param name="preferredFileName">Desired destination file name.</param>
    /// <param name="logger">Optional logger for collision warnings.</param>
    /// <returns>The full destination path actually used.</returns>
    string MoveSafely(
        string sourcePath,
        string destinationDirectory,
        string preferredFileName,
        ILogger? logger = null);
}

/// <summary>
/// Production <see cref="IScheduledJobFileMover"/> that delegates to <see cref="ScheduledJobFileMover.MoveSafely"/>.
/// </summary>
internal sealed class FileSystemScheduledJobFileMover : IScheduledJobFileMover
{
    /// <inheritdoc />
    public string MoveSafely(
        string sourcePath,
        string destinationDirectory,
        string preferredFileName,
        ILogger? logger = null) =>
        ScheduledJobFileMover.MoveSafely(sourcePath, destinationDirectory, preferredFileName, logger);
}
