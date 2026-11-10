using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Fixed-concurrency hosted workers that execute dispatched router jobs asynchronously.
/// </summary>
internal sealed class RouterJobExecutionService(
    RouterJobExecutionQueue queue,
    RouterJobExecutor executor,
    IRouterJobExecutionCompleter completer,
    IOptions<ScheduledJobsOptions> options,
    ILogger<RouterJobExecutionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int concurrency = options.Value.MaxConcurrentRouterJobs;
        logger.LogInformation(
            "Router job execution service starting. MaxConcurrentRouterJobs={MaxConcurrentRouterJobs}",
            concurrency);

        Task[] workers = Enumerable
            .Range(0, concurrency)
            .Select(workerId => RunWorkerAsync(workerId, stoppingToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        finally
        {
            queue.Complete();
            logger.LogInformation("Router job execution service stopped");
        }
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (RouterJobWorkItem workItem in queue.Reader.ReadAllAsync(stoppingToken)
                               .ConfigureAwait(false))
            {
                try
                {
                    await executor.ExecuteAsync(workItem, completer, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    completer.CompleteDispatchedJob(
                        workItem.EntryToken,
                        ScheduledJobTerminalState.Cancelled);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Unhandled worker exception. WorkerId={WorkerId}, ImportId={ImportId}, JobId={JobId}",
                        workerId,
                        workItem.Job.ImportId,
                        workItem.Job.JobId);
                    completer.CompleteDispatchedJob(
                        workItem.EntryToken,
                        ScheduledJobTerminalState.Failed,
                        error: ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected.
        }
    }
}
