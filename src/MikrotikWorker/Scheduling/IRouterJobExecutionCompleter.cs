namespace MikrotikWorker.Scheduling;

/// <summary>
/// Scheduler callbacks used by the router execution pool.
/// </summary>
internal interface IRouterJobExecutionCompleter
{
    /// <summary>
    /// Completes a previously dispatched job with a terminal outcome.
    /// </summary>
    void CompleteDispatchedJob(
        object entryToken,
        ScheduledJobTerminalState state,
        TimeSpan? executionDuration = null,
        Exception? error = null);

    /// <summary>
    /// Returns a dispatched job to the pending priority queue (still before DateStartUtc).
    /// </summary>
    bool TryRequeueDispatchedJob(object entryToken);

    /// <summary>
    /// True when the import was aborted or the scheduler is stopping.
    /// </summary>
    bool IsImportAbortedOrStopping(Guid importId);
}
