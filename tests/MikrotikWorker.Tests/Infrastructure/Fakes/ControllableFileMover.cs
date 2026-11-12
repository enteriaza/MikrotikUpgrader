// -----------------------------------------------------------------------------
// File: ControllableFileMover.cs
//
// Description:
// IScheduledJobFileMover fake that can delay, fail, or observe CSV move operations
// into Accepted/Completed. Used to prove ingest failure and archive-retry paths
// without depending on real filesystem races.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;

/// <summary>
/// Test double for <see cref="IScheduledJobFileMover"/> that can force Accepted/Completed
/// move failures while delegating successful moves to <see cref="FileSystemScheduledJobFileMover"/>.
/// </summary>
/// <param name="completedDirectory">
/// Optional absolute path also treated as the Completed destination, in addition to folders named <c>Completed</c>.
/// </param>
internal sealed class ControllableFileMover(string? completedDirectory = null) : IScheduledJobFileMover
{
    /// <summary>Real mover that performs moves not intercepted by a failure gate.</summary>
    private readonly IScheduledJobFileMover _inner = new FileSystemScheduledJobFileMover();

    /// <summary>Optional absolute path additionally treated as the Completed destination.</summary>
    private readonly string? _completedDirectory = completedDirectory is null
        ? null
        : Path.GetFullPath(completedDirectory);

    /// <summary>Remaining number of Completed moves that will fail.</summary>
    private int _failCompletedMoves;

    /// <summary>When true, every Completed move fails until cleared.</summary>
    private bool _failAllCompletedMoves;

    /// <summary>Remaining number of Accepted moves that will fail.</summary>
    private int _failAcceptedMoves;

    /// <summary>Exception thrown (once) by the next Accepted move, if set.</summary>
    private Exception? _throwOnAcceptedMove;

    /// <summary>Number of attempted moves whose destination resolved as Completed.</summary>
    public int CompletedMoveAttempts { get; private set; }

    /// <summary>Completed moves that succeeded after failure gates were cleared.</summary>
    public int SuccessfulCompletedMoves { get; private set; }

    /// <summary>Number of attempted moves into an Accepted folder.</summary>
    public int AcceptedMoveAttempts { get; private set; }

    /// <summary>Fails the next <paramref name="count"/> Completed moves with <see cref="IOException"/>.</summary>
    public void FailNextCompletedMoves(int count) => _failCompletedMoves = count;

    /// <summary>Fails every subsequent Completed move until <see cref="AllowCompletedMoves"/>.</summary>
    public void FailAllCompletedMoves() => _failAllCompletedMoves = true;

    /// <summary>Clears Completed-move failure gates.</summary>
    public void AllowCompletedMoves()
    {
        _failAllCompletedMoves = false;
        _failCompletedMoves = 0;
    }

    /// <summary>Fails the next <paramref name="count"/> Accepted moves with <see cref="IOException"/>.</summary>
    public void FailNextAcceptedMoves(int count) => _failAcceptedMoves = count;

    /// <summary>
    /// Throws <paramref name="exception"/> on the next Accepted move, then clears the gate.
    /// </summary>
    public void ThrowOnNextAcceptedMove(Exception exception) => _throwOnAcceptedMove = exception;

    /// <inheritdoc />
    /// <remarks>
    /// Detects Completed destinations by folder name or optional absolute path match.
    /// Accepted failures are injected before delegating to the real mover.
    /// </remarks>
    public string MoveSafely(
        string sourcePath,
        string destinationDirectory,
        string preferredFileName,
        ILogger? logger = null)
    {
        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(destinationDirectory));
        bool toCompleted =
            string.Equals(folderName, "Completed", StringComparison.OrdinalIgnoreCase)
            || (_completedDirectory is not null
                && string.Equals(
                    Path.GetFullPath(destinationDirectory),
                    _completedDirectory,
                    StringComparison.OrdinalIgnoreCase));

        if (toCompleted)
        {
            CompletedMoveAttempts++;
            if (_failAllCompletedMoves || _failCompletedMoves > 0)
            {
                if (!_failAllCompletedMoves && _failCompletedMoves > 0)
                {
                    _failCompletedMoves--;
                }

                throw new IOException("Simulated Completed move failure.");
            }

            string result = _inner.MoveSafely(sourcePath, destinationDirectory, preferredFileName, logger);
            SuccessfulCompletedMoves++;
            return result;
        }

        if (string.Equals(folderName, "Accepted", StringComparison.OrdinalIgnoreCase))
        {
            AcceptedMoveAttempts++;
            if (_throwOnAcceptedMove is not null)
            {
                Exception ex = _throwOnAcceptedMove;
                _throwOnAcceptedMove = null;
                throw ex;
            }

            if (_failAcceptedMoves > 0)
            {
                _failAcceptedMoves--;
                throw new IOException("Simulated Accepted move failure.");
            }
        }

        return _inner.MoveSafely(sourcePath, destinationDirectory, preferredFileName, logger);
    }
}
