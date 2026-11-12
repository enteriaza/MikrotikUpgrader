// -----------------------------------------------------------------------------
// File: IRouterJobExecutionCompleter.cs
//
// Description:
// Callback surface from the router execution pool back into the in-memory
// scheduler: terminal completion, requeue before eligibility, transient
// connect/login retry, and import abort/stop queries.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Scheduler callbacks used by the router execution pool.
/// </summary>
internal interface IRouterJobExecutionCompleter
{
    /// <summary>
    /// Completes a previously dispatched job with a terminal outcome.
    /// Side effects include capacity release, CSV result persistence, and import Remaining decrement.
    /// </summary>
    /// <param name="entryToken">Opaque <c>JobEntry</c> token from <see cref="RouterJobWorkItem.EntryToken"/>.</param>
    /// <param name="state">Terminal lifecycle state to record.</param>
    /// <param name="executionDuration">Optional measured execution duration for logging.</param>
    /// <param name="error">Optional exception associated with failure/blocked outcomes.</param>
    void CompleteDispatchedJob(
        object entryToken,
        ScheduledJobTerminalState state,
        TimeSpan? executionDuration = null,
        Exception? error = null);

    /// <summary>
    /// Returns a dispatched job to the pending priority queue (still before DateStartUtc / retry due).
    /// </summary>
    /// <param name="entryToken">Opaque scheduler entry token.</param>
    /// <returns><c>false</c> when the entry cannot be requeued (wrong state or scheduler stopping).</returns>
    bool TryRequeueDispatchedJob(object entryToken);

    /// <summary>
    /// Records a transient connection/authentication failure on the job. When the per-category
    /// attempt count is still within the configured maximum, requeues the same job for later
    /// eligibility without holding a worker slot. Returns <c>false</c> when the caller must
    /// mark the job permanently failed (attempt limit exhausted or scheduler stopping).
    /// </summary>
    /// <param name="entryToken">Opaque scheduler entry token.</param>
    /// <param name="category">Which independent retry counter to increment.</param>
    /// <param name="reason">Safe failure reason (must not contain credentials).</param>
    /// <param name="error">Optional exception for logging.</param>
    /// <returns><c>true</c> when the job was requeued for a later retry.</returns>
    bool TryScheduleTransientRetry(
        object entryToken,
        JobRetryFailureCategory category,
        string reason,
        Exception? error = null);

    /// <summary>
    /// True when the import was aborted or the scheduler is stopping.
    /// Also true when the import is no longer tracked (already closed).
    /// </summary>
    /// <param name="importId">Import to query.</param>
    bool IsImportAbortedOrStopping(Guid importId);
}
