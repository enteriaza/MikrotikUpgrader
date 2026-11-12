using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
internal sealed class ControllableFileMover(string? completedDirectory = null) : IScheduledJobFileMover
{
    private readonly IScheduledJobFileMover _inner = new FileSystemScheduledJobFileMover();
    private readonly string? _completedDirectory = completedDirectory is null
        ? null
        : Path.GetFullPath(completedDirectory);
    private int _failCompletedMoves;
    private bool _failAllCompletedMoves;
    private int _failAcceptedMoves;
    private Exception? _throwOnAcceptedMove;

    public int CompletedMoveAttempts { get; private set; }

    public int SuccessfulCompletedMoves { get; private set; }

    public int AcceptedMoveAttempts { get; private set; }

    public void FailNextCompletedMoves(int count) => _failCompletedMoves = count;

    public void FailAllCompletedMoves() => _failAllCompletedMoves = true;

    public void AllowCompletedMoves()
    {
        _failAllCompletedMoves = false;
        _failCompletedMoves = 0;
    }

    public void FailNextAcceptedMoves(int count) => _failAcceptedMoves = count;

    public void ThrowOnNextAcceptedMove(Exception exception) => _throwOnAcceptedMove = exception;

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
