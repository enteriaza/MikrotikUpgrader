using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

internal sealed class CsvFileReadinessChecker(IOptions<ScheduledJobsOptions> options)
{
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
