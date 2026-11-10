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
        logger.LogInformation("Router job worker available. WorkerId={WorkerId}", workerId);
        try
        {
            await foreach (RouterJobWorkItem workItem in queue.Reader.ReadAllAsync(stoppingToken)
                               .ConfigureAwait(false))
            {
                MikrotikScheduledJob job = workItem.Job;
                logger.LogInformation(
                    "Job dequeued for execution. WorkerId={WorkerId}, ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
                    workerId,
                    job.ImportId,
                    job.JobId,
                    job.RouterIp);
                logger.LogInformation(
                    "Worker beginning job. WorkerId={WorkerId}, ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
                    workerId,
                    job.ImportId,
                    job.JobId,
                    job.RouterIp);

                try
                {
                    await executor.ExecuteAsync(workItem, completer, stoppingToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "Worker finished job. WorkerId={WorkerId}, ImportId={ImportId}, JobId={JobId}, RouterIp={RouterIp}",
                        workerId,
                        job.ImportId,
                        job.JobId,
                        job.RouterIp);
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
        finally
        {
            logger.LogInformation("Router job worker stopping. WorkerId={WorkerId}", workerId);
        }
    }
}
