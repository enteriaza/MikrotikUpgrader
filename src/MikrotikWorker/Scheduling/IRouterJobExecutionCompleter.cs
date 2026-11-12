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
    /// Records a transient connection/authentication failure on the job. When the per-category
    /// attempt count is still within the configured maximum, requeues the same job for later
    /// eligibility without holding a worker slot. Returns <c>false</c> when the caller must
    /// mark the job permanently failed (attempt limit exhausted or scheduler stopping).
    /// </summary>
    bool TryScheduleTransientRetry(
        object entryToken,
        JobRetryFailureCategory category,
        string reason,
        Exception? error = null);

    /// <summary>
    /// True when the import was aborted or the scheduler is stopping.
    /// </summary>
    bool IsImportAbortedOrStopping(Guid importId);
}
