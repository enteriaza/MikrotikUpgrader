// -----------------------------------------------------------------------------
// File: CsvFileReadinessChecker.cs
//
// Description:
// Waits until an Incoming CSV is stable enough to ingest: exclusive open succeeds
// and length is unchanged across two consecutive observations. Prevents racing
// a still-growing upload against validation and moves.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Polls a CSV until it can be opened exclusively with a stable length, or times out.
/// </summary>
/// <param name="options">Readiness timeout and poll interval from <see cref="ScheduledJobsOptions"/>.</param>
internal sealed class CsvFileReadinessChecker(IOptions<ScheduledJobsOptions> options)
{
    /// <summary>
    /// Blocks until the file is exclusively readable and its length is unchanged for two polls,
    /// or until <see cref="ScheduledJobsOptions.FileReadyTimeout"/> elapses.
    /// </summary>
    /// <param name="filePath">CSV path under Incoming (or equivalent) being watched.</param>
    /// <param name="cancellationToken">Cancels the wait loop.</param>
    /// <exception cref="FileNotFoundException">Thrown when the file disappears before becoming ready.</exception>
    /// <exception cref="TimeoutException">Thrown when the deadline expires without two stable observations.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    public async Task WaitUntilReadyAsync(string filePath, CancellationToken cancellationToken)
    {
        ScheduledJobsOptions settings = options.Value;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + settings.FileReadyTimeout;
        long previousLength = -1;
        int stableObservations = 0;

        while (DateTimeOffset.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                FileInfo info = new(filePath);
                if (!info.Exists)
                {
                    throw new FileNotFoundException("CSV file disappeared before it became ready.", filePath);
                }

                await using FileStream stream = new(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);

                long length = stream.Length;
                if (length == previousLength)
                {
                    stableObservations++;
                    if (stableObservations >= 2)
                    {
                        return;
                    }
                }
                else
                {
                    previousLength = length;
                    stableObservations = 0;
                }
            }
            catch (IOException)
            {
                stableObservations = 0;
            }
            catch (UnauthorizedAccessException)
            {
                stableObservations = 0;
            }

            await Task.Delay(settings.FileReadyCheckInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out waiting for CSV file to become ready: {Path.GetFileName(filePath)}");
    }
}
