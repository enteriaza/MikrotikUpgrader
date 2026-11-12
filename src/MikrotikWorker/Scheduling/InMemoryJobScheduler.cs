// -----------------------------------------------------------------------------
// File: InMemoryJobScheduler.cs
//
// Description:
// Time-aware, capacity-bounded in-memory job scheduler and its hosted loop.
// Tracks import batches (Remaining, HeldForDispatch, AcceptedOnDisk), holds jobs
// until the Accepted CSV is on disk, dispatches due work to the execution queue,
// applies transient connect/login retries, and closes imports via Completed
// archive or retire-leaving-Accepted when activation was incomplete.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Contract for registering CSV imports, scheduling rows, aborting batches, and running the wake loop.
/// </summary>
internal interface IInMemoryJobScheduler
{
    /// <summary>Configured ceiling for simultaneously pending (queued + dispatched) jobs.</summary>
    int MaximumPendingJobs { get; }

    /// <summary>Current count of queued plus dispatched (non-terminal) jobs.</summary>
    int PendingCount { get; }

    /// <summary>Returns whether an import batch is still tracked in memory.</summary>
    /// <param name="importId">Import to query.</param>
    bool IsTracked(Guid importId);

    /// <summary>
    /// Creates a new import batch with <paramref name="jobCount"/> validated rows as initial Remaining.
    /// </summary>
    /// <param name="importId">Unique import identifier.</param>
    /// <param name="jobCount">Validated CSV row count (≥ 0).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="jobCount"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the import is already registered or the scheduler is stopping.</exception>
    void RegisterImport(Guid importId, int jobCount);

    /// <summary>
    /// Confirms the Accepted CSV is on disk, populates the path store, releases
    /// <c>HeldForDispatch</c> jobs into the priority queue, and may attempt import closure.
    /// </summary>
    /// <param name="importId">Import whose Accepted path is now real.</param>
    /// <param name="acceptedPath">Full path to the Accepted CSV.</param>
    void SetAcceptedPath(Guid importId, string acceptedPath);

    /// <summary>
    /// Registers the intended Accepted path before the filesystem move completes.
    /// Completion to Completed is blocked until <see cref="SetAcceptedPath"/> confirms the on-disk path.
    /// </summary>
    /// <param name="importId">Import being activated.</param>
    /// <param name="intendedAcceptedPath">Expected Accepted path (pre-move).</param>
    void BindAcceptedPathIntent(Guid importId, string intendedAcceptedPath);

    /// <summary>
    /// Marks import activation finished (all intended rows scheduled, or activation stopped early).
    /// Reconciles <c>Remaining</c> so never-scheduled rows are not treated as outstanding work.
    /// </summary>
    /// <param name="importId">Import whose activation ended.</param>
    /// <param name="scheduledCount">Rows successfully accepted by <see cref="ScheduleAsync"/>.</param>
    /// <param name="activationIncomplete">
    /// True when activation stopped early (cancel/failure) and <paramref name="scheduledCount"/> may be
    /// less than the validated row count. Incomplete activation never archives to Completed.
    /// </param>
    void FinalizeImportActivation(Guid importId, int scheduledCount, bool activationIncomplete);

    /// <summary>
    /// Accepts a job into capacity, tracking tables, and either the priority queue or
    /// <c>HeldForDispatch</c> until <see cref="SetAcceptedPath"/> runs.
    /// </summary>
    /// <param name="job">Job to schedule.</param>
    /// <param name="cancellationToken">Cancels waiting for capacity; linked with scheduler stop.</param>
    ValueTask ScheduleAsync(MikrotikScheduledJob job, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the import and all of its pending/dispatched entries, clears the path store,
    /// and leaves any Processing/Accepted file on disk for manual recovery.
    /// </summary>
    /// <param name="importId">Import to abort.</param>
    /// <param name="cancellationToken">Checked before work begins.</param>
    Task AbortImportAsync(Guid importId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the due-job / idle-wait loop until cancellation, then discards remaining in-memory work.
    /// </summary>
    /// <param name="cancellationToken">Host stop token.</param>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Requests graceful stop: rejects new schedules, drains in-flight schedule ops, discards pending.</summary>
    /// <param name="cancellationToken">Unused; stop is coordinated via internal lifecycle.</param>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Time-aware bounded in-memory scheduler. Future jobs never block eligible jobs.
/// Implements <see cref="IRouterJobExecutionCompleter"/> for the execution pool.
/// </summary>
internal sealed class InMemoryJobScheduler : IInMemoryJobScheduler, IRouterJobExecutionCompleter, IAsyncDisposable
{
    /// <summary>Scheduler process lifecycle used to reject schedules during shutdown.</summary>
    private enum Lifecycle
    {
        /// <summary>Accepting schedules and running the wake loop.</summary>
        Running = 0,

        /// <summary>Stop requested; waiting for in-flight <see cref="ScheduleAsync"/> calls.</summary>
        Stopping = 1,

        /// <summary>Capacity disposed; instance is finished.</summary>
        Disposed = 2,
    }

    /// <summary>Scheduled-jobs folder layout; supplies the Completed folder that finished imports are archived into.</summary>
    private readonly ScheduledJobsPaths _paths;
    /// <summary>Moves an Accepted CSV to Completed when its import closes successfully.</summary>
    private readonly IScheduledJobFileMover _fileMover;
    /// <summary>Clock used for eligibility checks, retry delays and idle waits; injectable for tests.</summary>
    private readonly TimeProvider _timeProvider;
    /// <summary>Diagnostic logger for scheduling, dispatch and import-closure events.</summary>
    private readonly ILogger<InMemoryJobScheduler> _logger;
    /// <summary>Persists terminal per-row results to the imported CSV; a no-op writer when none was supplied.</summary>
    private readonly IImportedCsvResultWriter _csvResultWriter;
    /// <summary>Registry of the Accepted CSV path for each import, shared with result writing.</summary>
    private readonly ImportedCsvPathStore _csvPathStore;
    /// <summary>Bounded hand-off queue through which due jobs are dispatched to the router execution pool.</summary>
    private readonly RouterJobExecutionQueue _executionQueue;
    /// <summary>Transient-failure retry delays and attempt limits; defaults when none were configured.</summary>
    private readonly JobRetryOptions _retryOptions;
    /// <summary>Counts free pending-job slots (queued plus dispatched); acquired when a job is scheduled and released exactly once when it ends.</summary>
    private readonly SemaphoreSlim _capacity;
    /// <summary>Guards <c>_queue</c>, <c>_entries</c> and <c>_dispatched</c> so queue membership and the tracking tables change together.</summary>
    private readonly object _gate = new();
    /// <summary>Priority queue of schedulable entries ordered by effective eligible time, then arrival sequence. It can contain stale heads for entries that were removed or already dispatched; those are skipped lazily.</summary>
    private readonly PriorityQueue<JobEntry, JobPriority> _queue = new();
    /// <summary>Pending entries (accepted, not yet dispatched) keyed by import id and CSV row number; guarded by <c>_gate</c>.</summary>
    private readonly Dictionary<(Guid ImportId, int RowNumber), JobEntry> _entries = new();
    /// <summary>Entries handed to the execution queue and not yet terminal, keyed by import id and row number; guarded by <c>_gate</c>.</summary>
    private readonly Dictionary<(Guid ImportId, int RowNumber), JobEntry> _dispatched = new();
    /// <summary>In-memory state of each registered import, keyed by import id.</summary>
    private readonly ConcurrentDictionary<Guid, ImportBatch> _imports = new();
    /// <summary>Guards swapping and reading <c>_wakeSignal</c> so a wake request is never lost between snapshot and wait.</summary>
    private readonly object _wakeGate = new();
    /// <summary>Guards <c>_lifecycle</c> and <c>_activeScheduleOperations</c> and is used to wait for in-flight schedule calls during stop.</summary>
    private readonly object _lifecycleGate = new();
    /// <summary>Cancelled when stop begins; unblocks callers waiting for capacity or the wake loop.</summary>
    private readonly CancellationTokenSource _stoppingCts = new();
    /// <summary>Completion source signalled to wake the run loop; replaced after each signal. Access under <c>_wakeGate</c>.</summary>
    private TaskCompletionSource _wakeSignal = NewWakeSignal();
    /// <summary>Monotonic arrival counter used as the tie-breaker for jobs with equal eligible time (FIFO within a time).</summary>
    private int _sequence;
    /// <summary>Number of <see cref="ScheduleAsync"/> calls currently in progress; stop waits for it to reach zero. Guarded by <c>_lifecycleGate</c>.</summary>
    private int _activeScheduleOperations;
    /// <summary>Current scheduler lifecycle stage; guarded by <c>_lifecycleGate</c>.</summary>
    private Lifecycle _lifecycle = Lifecycle.Running;

    /// <summary>
    /// Creates a scheduler bound to the given paths, execution queue, and optional CSV/retry collaborators.
    /// </summary>
    /// <param name="options">Supplies <see cref="MaximumPendingJobs"/>.</param>
    /// <param name="paths">Completed folder for Accepted→Completed archival.</param>
    /// <param name="fileMover">Moves Accepted CSVs to Completed on successful closure.</param>
    /// <param name="executionQueue">Bounded handoff for eligible jobs.</param>
    /// <param name="timeProvider">Clock for eligibility, retries, and idle waits (testable).</param>
    /// <param name="logger">Scheduler diagnostics.</param>
    /// <param name="retryOptions">Transient retry delays and maxima; defaults when null.</param>
    /// <param name="csvResultWriter">Persists terminal results; no-op when null.</param>
    /// <param name="csvPathStore">Accepted path registry; a private store is created when null.</param>
    public InMemoryJobScheduler(
        IOptions<ScheduledJobsOptions> options,
        ScheduledJobsPaths paths,
        IScheduledJobFileMover fileMover,
        RouterJobExecutionQueue executionQueue,
        TimeProvider timeProvider,
        ILogger<InMemoryJobScheduler> logger,
        IOptions<JobRetryOptions>? retryOptions = null,
        IImportedCsvResultWriter? csvResultWriter = null,
        ImportedCsvPathStore? csvPathStore = null)
    {
        MaximumPendingJobs = options.Value.MaximumPendingJobs;
        _capacity = new SemaphoreSlim(MaximumPendingJobs, MaximumPendingJobs);
        _paths = paths;
        _fileMover = fileMover;
        _executionQueue = executionQueue;
        _timeProvider = timeProvider;
        _logger = logger;
        _retryOptions = retryOptions?.Value ?? new JobRetryOptions();
        _csvResultWriter = csvResultWriter ?? NullImportedCsvResultWriter.Instance;
        _csvPathStore = csvPathStore ?? new ImportedCsvPathStore();
    }

    /// <inheritdoc />
    public int MaximumPendingJobs { get; }

    /// <summary>
    /// Test seam: invoked after the wake task is snapshotted and before the queue is inspected.
    /// Used to prove lost-wakeup-free signalling.
    /// </summary>
    internal Func<Task>? AfterWakeSnapshotBeforeQueueCheckAsync { get; set; }

    /// <inheritdoc />
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count + _dispatched.Count;
            }
        }
    }

    /// <summary>Test seam: jobs in the pending table (not yet dispatched).</summary>
    internal int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Test seam: jobs handed to the execution queue and not yet terminal.</summary>
    internal int DispatchedCount
    {
        get
        {
            lock (_gate)
            {
                return _dispatched.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool IsTracked(Guid importId) => _imports.ContainsKey(importId);

    /// <summary>
    /// Test/helper seam: attempts completion for any import that is fully terminal and accepted.
    /// </summary>
    internal void RetryPendingCompletions() => TryCompletePendingImports();

    /// <summary>
    /// Test seam: wakes the scheduler loop (e.g. after a FakeTimeProvider clock jump).
    /// </summary>
    internal void NudgeWake() => SignalWake();

    /// <summary>
    /// Test seam: runs due-job processing on the caller thread.
    /// </summary>
    internal void ProcessDueJobsForTests() => ProcessDueJobs();

    /// <inheritdoc />
    public void RegisterImport(Guid importId, int jobCount)
    {
        ThrowIfNotRunning();

        if (jobCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jobCount));
        }

        ImportBatch batch = new(importId, jobCount);
        if (!_imports.TryAdd(importId, batch))
        {
            throw new InvalidOperationException($"Import '{importId}' is already registered.");
        }

        _logger.LogInformation(
            "Import registered: ImportId={ImportId}, JobCount={JobCount}",
            importId,
            jobCount);
    }

    /// <inheritdoc />
    public void BindAcceptedPathIntent(Guid importId, string intendedAcceptedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intendedAcceptedPath);

        if (!_imports.TryGetValue(importId, out ImportBatch? batch))
        {
            throw new InvalidOperationException($"Import '{importId}' is not registered.");
        }

        lock (batch.Sync)
        {
            if (batch.Aborted)
            {
                throw new InvalidOperationException($"Import '{importId}' was aborted.");
            }

            batch.AcceptedPath = intendedAcceptedPath;
            batch.AcceptedOnDisk = false;
        }

        _logger.LogInformation(
            "Import accepted path intent bound: ImportId={ImportId}, Path={Path}",
            importId,
            intendedAcceptedPath);
    }

    /// <inheritdoc />
    public void SetAcceptedPath(Guid importId, string acceptedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedPath);

        if (!_imports.TryGetValue(importId, out ImportBatch? batch))
        {
            throw new InvalidOperationException($"Import '{importId}' is not registered.");
        }

        bool shouldAttemptCompletion;
        lock (batch.Sync)
        {
            if (batch.Aborted)
            {
                throw new InvalidOperationException($"Import '{importId}' was aborted.");
            }

            batch.AcceptedPath = acceptedPath;
            batch.AcceptedOnDisk = true;
            shouldAttemptCompletion = batch.Remaining == 0 && !batch.Archived;
        }

        // Path store must be populated before any held jobs become dispatchable.
        _csvPathStore.SetAcceptedPath(importId, acceptedPath);
        ReleaseHeldJobsForDispatch(batch);

        _logger.LogInformation(
            "Import accepted path set: ImportId={ImportId}, Path={Path}, Remaining={Remaining}",
            importId,
            acceptedPath,
            batch.Remaining);

        if (shouldAttemptCompletion)
        {
            AttemptImportClosure(batch);
        }
    }

    /// <inheritdoc />
    public void FinalizeImportActivation(Guid importId, int scheduledCount, bool activationIncomplete)
    {
        if (!_imports.TryGetValue(importId, out ImportBatch? batch))
        {
            return;
        }

        if (scheduledCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scheduledCount));
        }

        int validatedCount;
        int neverScheduled;
        int remainingAfter;
        string? acceptedPath;
        bool acceptedOnDisk;
        lock (batch.Sync)
        {
            if (batch.Aborted || batch.Archived)
            {
                return;
            }

            if (batch.ActivationFinalized)
            {
                return;
            }

            if (scheduledCount > batch.ValidatedCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(scheduledCount),
                    scheduledCount,
                    $"Scheduled count exceeds validated count {batch.ValidatedCount} for import '{importId}'.");
            }

            validatedCount = batch.ValidatedCount;
            neverScheduled = validatedCount - scheduledCount;
            batch.ActivationFinalized = true;
            batch.ActivationIncomplete = activationIncomplete;
            batch.ScheduledCount = scheduledCount;

            // Remaining started as ValidatedCount and decrements only on terminal scheduled jobs.
            // Never-scheduled rows must not keep the import outstanding forever.
            if (neverScheduled > 0)
            {
                batch.Remaining = Math.Max(0, batch.Remaining - neverScheduled);
            }

            remainingAfter = batch.Remaining;
            acceptedPath = batch.AcceptedPath;
            acceptedOnDisk = batch.AcceptedOnDisk;
        }

        bool retainForScheduledJobs = remainingAfter > 0;
        bool retireImmediately =
            remainingAfter == 0 && activationIncomplete && scheduledCount < validatedCount;
        string finalizeMessage =
            "Import activation finalized: ImportId={ImportId}, AcceptedPath={AcceptedPath}, ValidatedRows={ValidatedRows}, ScheduledRows={ScheduledRows}, NeverScheduledRows={NeverScheduledRows}, OutstandingScheduledJobs={OutstandingScheduledJobs}, ActivationIncomplete={ActivationIncomplete}, AcceptedOnDisk={AcceptedOnDisk}, RetainForScheduledJobs={RetainForScheduledJobs}, RetireImmediately={RetireImmediately}";
        if (activationIncomplete)
        {
            _logger.LogWarning(
                finalizeMessage,
                importId,
                acceptedPath,
                validatedCount,
                scheduledCount,
                neverScheduled,
                remainingAfter,
                activationIncomplete,
                acceptedOnDisk,
                retainForScheduledJobs,
                retireImmediately);
        }
        else
        {
            _logger.LogInformation(
                finalizeMessage,
                importId,
                acceptedPath,
                validatedCount,
                scheduledCount,
                neverScheduled,
                remainingAfter,
                activationIncomplete,
                acceptedOnDisk,
                retainForScheduledJobs,
                retireImmediately);
        }

        if (remainingAfter == 0 && acceptedOnDisk)
        {
            AttemptImportClosure(batch);
        }
    }

    /// <summary>
    /// Test seam: outstanding work remaining for a tracked import (scheduled jobs not yet terminal,
    /// after activation reconciliation when finalized).
    /// </summary>
    internal bool TryGetImportRemaining(Guid importId, out int remaining)
    {
        if (_imports.TryGetValue(importId, out ImportBatch? batch))
        {
            lock (batch.Sync)
            {
                remaining = batch.Remaining;
                return true;
            }
        }

        remaining = 0;
        return false;
    }

    /// <inheritdoc />
    public async ValueTask ScheduleAsync(MikrotikScheduledJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ThrowIfNotRunning();

        if (!_imports.TryGetValue(job.ImportId, out ImportBatch? batch) || Volatile.Read(ref batch.AbortedFlag) != 0)
        {
            throw new InvalidOperationException($"Import '{job.ImportId}' is not active.");
        }

        EnterScheduleOperation();
        bool capacityHeld = false;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _stoppingCts.Token);

            await _capacity.WaitAsync(linked.Token).ConfigureAwait(false);
            capacityHeld = true;
            ThrowIfNotRunning();

            lock (_gate)
            {
                if (Volatile.Read(ref batch.AbortedFlag) != 0)
                {
                    _capacity.Release();
                    capacityHeld = false;
                    throw new InvalidOperationException($"Import '{job.ImportId}' was aborted.");
                }

                (Guid, int) key = (job.ImportId, job.RowNumber);
                if (_entries.ContainsKey(key) || _dispatched.ContainsKey(key))
                {
                    _capacity.Release();
                    capacityHeld = false;
                    _logger.LogDebug(
                        "Skipping duplicate schedule for ImportId={ImportId}, RowNumber={RowNumber}",
                        job.ImportId,
                        job.RowNumber);
                    return;
                }

                JobEntry entry = new(job);
                int sequence = ++_sequence;
                JobPriority priority = new(job.EffectiveEligibleUtc, sequence);
                _entries[key] = entry;

                // Jobs must not enter the dispatch queue until the Accepted CSV path is on disk
                // and registered for this ImportId (see SetAcceptedPath / ReleaseHeldJobsForDispatch).
                bool dispatchable;
                lock (batch.Sync)
                {
                    dispatchable = batch.AcceptedOnDisk;
                    if (!dispatchable)
                    {
                        batch.HeldForDispatch.Add((entry, priority));
                    }
                }

                if (dispatchable)
                {
                    _queue.Enqueue(entry, priority);
                }

                capacityHeld = false;
            }

            _logger.LogInformation(
                "Job accepted into scheduler. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, DateStartUtc={DateStartUtc}, DateStopUtc={DateStopUtc}, PendingCount={PendingCount}",
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp,
                job.DateStartUtc,
                job.DateStopUtc,
                PendingCount);

            SignalWake();
        }
        catch (OperationCanceledException) when (_stoppingCts.IsCancellationRequested)
        {
            if (capacityHeld)
            {
                _capacity.Release();
                capacityHeld = false;
            }

            throw new InvalidOperationException("Scheduler is stopping; scheduling is rejected.");
        }
        catch
        {
            if (capacityHeld)
            {
                _capacity.Release();
            }

            throw;
        }
        finally
        {
            ExitScheduleOperation();
        }
    }

    /// <inheritdoc />
    public Task AbortImportAsync(Guid importId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_imports.TryGetValue(importId, out ImportBatch? batch))
        {
            return Task.CompletedTask;
        }

        List<JobEntry> toRelease = [];
        lock (_gate)
        {
            lock (batch.Sync)
            {
                batch.AbortedFlag = 1;
                batch.Aborted = true;
                batch.HeldForDispatch.Clear();
            }

            foreach (KeyValuePair<(Guid ImportId, int RowNumber), JobEntry> pair in _entries.ToArray())
            {
                if (pair.Key.ImportId != importId)
                {
                    continue;
                }

                if (pair.Value.TryMarkRemoved())
                {
                    toRelease.Add(pair.Value);
                }

                _entries.Remove(pair.Key);
            }

            foreach (KeyValuePair<(Guid ImportId, int RowNumber), JobEntry> pair in _dispatched.ToArray())
            {
                if (pair.Key.ImportId != importId)
                {
                    continue;
                }

                if (pair.Value.TryMarkRemoved())
                {
                    toRelease.Add(pair.Value);
                }

                _dispatched.Remove(pair.Key);
            }
        }

        foreach (JobEntry entry in toRelease)
        {
            ReleaseCapacity(entry);
        }

        _imports.TryRemove(importId, out _);
        _csvPathStore.Clear(importId);
        SignalWake();

        _logger.LogWarning(
            "Import aborted: ImportId={ImportId}. ReleasedPending={ReleasedPending}. CSV remains in Processing if not yet Accepted.",
            importId,
            toRelease.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "In-memory job scheduler started. MaximumPendingJobs={MaximumPendingJobs}",
            MaximumPendingJobs);

        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _stoppingCts.Token);

            while (!linked.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Yield();

                    ProcessDueJobs();
                    TryCompletePendingImports();

                    if (!await IdleWaitAsync(linked.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stoppingCts.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        finally
        {
            await StopCoreAsync().ConfigureAwait(false);
            _logger.LogInformation("In-memory job scheduler stopped; pending in-memory jobs discarded");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default) => StopCoreAsync();

    /// <summary>Stops the scheduler and releases resources (same path as <see cref="StopAsync"/>).</summary>
    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Idle wait that snapshots the wake signal before peeking the queue (lost-wakeup-safe),
    /// then sleeps until the next due time, a wake, or cancellation.
    /// </summary>
    /// <returns><c>false</c> when cancellation ended the wait and the loop should exit.</returns>
    private async Task<bool> IdleWaitAsync(CancellationToken cancellationToken)
    {
        // Snapshot the wake task BEFORE inspecting the queue to avoid lost wake-ups.
        Task wakeTask = SnapshotWakeTask();

        if (AfterWakeSnapshotBeforeQueueCheckAsync is not null)
        {
            await AfterWakeSnapshotBeforeQueueCheckAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset? nextWake;
        lock (_gate)
        {
            nextWake = PeekNextWakeUtc_NoLock();
        }

        if (nextWake is null)
        {
            await WaitForSignalAsync(wakeTask, cancellationToken).ConfigureAwait(false);
            return !cancellationToken.IsCancellationRequested;
        }

        if (nextWake.Value <= now)
        {
            return true;
        }

        DateTimeOffset cappedDue = now + CapDelay(nextWake.Value - now);
        await WaitUntilAsync(cappedDue, wakeTask, cancellationToken).ConfigureAwait(false);
        return !cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Dequeues due priority-queue heads: expires past DateStopUtc, otherwise marks dispatched
    /// and hands eligible jobs to <see cref="DispatchEligibleJob"/>.
    /// </summary>
    private void ProcessDueJobs()
    {
        DateTime utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        List<(JobEntry Entry, ScheduledJobTerminalState State)> expired = [];
        List<JobEntry> toDispatch = [];

        lock (_gate)
        {
            while (_queue.Count > 0)
            {
                if (!_queue.TryPeek(out JobEntry? entry, out JobPriority priority))
                {
                    break;
                }

                if (entry.IsRemovedOrTerminalOrDispatched)
                {
                    _queue.Dequeue();
                    continue;
                }

                if (priority.DateStartUtc > utcNow)
                {
                    _logger.LogDebug(
                        "Future job skipped for now. ImportId={ImportId}, JobId={JobId}, DateStartUtc={DateStartUtc}, UtcNow={UtcNow}",
                        entry.Job.ImportId,
                        entry.Job.JobId,
                        entry.Job.DateStartUtc,
                        utcNow);
                    break;
                }

                _queue.Dequeue();
                (Guid ImportId, int RowNumber) key = (entry.Job.ImportId, entry.Job.RowNumber);
                _entries.Remove(key);

                if (utcNow >= entry.Job.DateStopUtc)
                {
                    if (entry.TryMarkTerminal(ScheduledJobTerminalState.Expired))
                    {
                        expired.Add((entry, ScheduledJobTerminalState.Expired));
                    }

                    continue;
                }

                if (!entry.TryMarkDispatched())
                {
                    continue;
                }

                _dispatched[key] = entry;
                toDispatch.Add(entry);
            }
        }

        foreach ((JobEntry entry, ScheduledJobTerminalState state) in expired)
        {
            CompleteTerminalJob(entry, state, executionDuration: null, error: null);
        }

        foreach (JobEntry entry in toDispatch)
        {
            DispatchEligibleJob(entry);
        }
    }

    /// <summary>
    /// Writes a work item to the execution queue; on capacity failure requeues or fails the job.
    /// </summary>
    private void DispatchEligibleJob(JobEntry entry)
    {
        MikrotikScheduledJob job = entry.Job;
        _logger.LogInformation(
            "Job became eligible and was dispatched to execution queue. ImportId={ImportId}, JobId={JobId}, RowNumber={RowNumber}, RouterName={RouterName}, RouterIp={RouterIp}, DateStartUtc={DateStartUtc}, DateStopUtc={DateStopUtc}",
            job.ImportId,
            job.JobId,
            job.RowNumber,
            job.RouterName,
            job.RouterIp,
            job.DateStartUtc,
            job.DateStopUtc);

        RouterJobWorkItem workItem = new(job, entry);
        if (_executionQueue.TryWrite(workItem))
        {
            return;
        }

        _logger.LogWarning(
            "Execution queue at capacity; requeueing eligible job. ImportId={ImportId}, JobId={JobId}",
            job.ImportId,
            job.JobId);

        if (!TryRequeueDispatchedJob(entry))
        {
            CompleteDispatchedJob(entry, ScheduledJobTerminalState.Failed);
        }
    }

    /// <inheritdoc />
    public void CompleteDispatchedJob(
        object entryToken,
        ScheduledJobTerminalState state,
        TimeSpan? executionDuration = null,
        Exception? error = null)
    {
        if (entryToken is not JobEntry entry)
        {
            throw new ArgumentException("Invalid scheduler entry token.", nameof(entryToken));
        }

        CompleteDispatchedJob(entry, state, executionDuration, error);
    }

    /// <summary>
    /// Marks a dispatched entry terminal, removes it from the dispatched table, and runs shared terminal handling.
    /// </summary>
    private void CompleteDispatchedJob(
        JobEntry entry,
        ScheduledJobTerminalState state,
        TimeSpan? executionDuration = null,
        Exception? error = null)
    {
        if (!entry.TryMarkTerminal(state))
        {
            return;
        }

        lock (_gate)
        {
            _dispatched.Remove((entry.Job.ImportId, entry.Job.RowNumber));
        }

        CompleteTerminalJob(entry, state, executionDuration, error);
    }

    /// <inheritdoc />
    public bool TryRequeueDispatchedJob(object entryToken)
    {
        if (entryToken is not JobEntry entry)
        {
            throw new ArgumentException("Invalid scheduler entry token.", nameof(entryToken));
        }

        return TryRequeueDispatchedJob(entry);
    }

    /// <summary>
    /// Moves a dispatched entry back to pending/priority queue when still Running; otherwise releases capacity.
    /// </summary>
    private bool TryRequeueDispatchedJob(JobEntry entry)
    {
        if (!entry.TryMarkRequeued())
        {
            return false;
        }

        lock (_gate)
        {
            if (_lifecycle != Lifecycle.Running)
            {
                entry.TryMarkRemoved();
                ReleaseCapacity(entry);
                return false;
            }

            (Guid, int) key = (entry.Job.ImportId, entry.Job.RowNumber);
            _dispatched.Remove(key);
            _entries[key] = entry;
            int sequence = ++_sequence;
            _queue.Enqueue(entry, new JobPriority(entry.Job.EffectiveEligibleUtc, sequence));
        }

        SignalWake();
        return true;
    }

    /// <inheritdoc />
    public bool TryScheduleTransientRetry(
        object entryToken,
        JobRetryFailureCategory category,
        string reason,
        Exception? error = null)
    {
        if (entryToken is not JobEntry entry)
        {
            throw new ArgumentException("Invalid scheduler entry token.", nameof(entryToken));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        MikrotikScheduledJob job = entry.Job;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        int attempt;
        int maximum = _retryOptions.MaximumUnsuccessfulAttempts;
        TimeSpan delay;
        switch (category)
        {
            case JobRetryFailureCategory.Connection:
                attempt = ++job.ConnectionFailureCount;
                delay = _retryOptions.ConnectionRetryDelay;
                break;
            case JobRetryFailureCategory.Authentication:
                attempt = ++job.AuthenticationFailureCount;
                delay = _retryOptions.AuthenticationRetryDelay;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown retry failure category.");
        }

        job.LastRetryFailureCategory = category;
        job.LastRetryFailureReason = reason;
        job.LastRetryFailureUtc = now;

        if (attempt > maximum)
        {
            job.NextRetryUtc = null;
            _logger.LogError(
                error,
                "Transient {FailureCategory} failure exhausted retries; job will fail permanently. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, Attempt={Attempt}, MaximumUnsuccessfulAttempts={MaximumUnsuccessfulAttempts}, Reason={Reason}",
                category,
                job.ImportId,
                job.JobId,
                job.RouterName,
                job.RouterIp,
                attempt,
                maximum,
                reason);
            return false;
        }

        DateTimeOffset nextRetry = now + delay;
        job.NextRetryUtc = nextRetry;

        _logger.LogWarning(
            error,
            "Transient {FailureCategory} failure; requeueing job. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, Attempt={Attempt}, MaximumUnsuccessfulAttempts={MaximumUnsuccessfulAttempts}, RetryDelay={RetryDelay}, NextRetryUtc={NextRetryUtc}, Reason={Reason}",
            category,
            job.ImportId,
            job.JobId,
            job.RouterName,
            job.RouterIp,
            attempt,
            maximum,
            delay,
            nextRetry,
            reason);

        if (!TryRequeueDispatchedJob(entry))
        {
            job.NextRetryUtc = null;
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public bool IsImportAbortedOrStopping(Guid importId)
    {
        if (_stoppingCts.IsCancellationRequested)
        {
            return true;
        }

        if (!_imports.TryGetValue(importId, out ImportBatch? batch))
        {
            return true;
        }

        return Volatile.Read(ref batch.AbortedFlag) != 0;
    }

    /// <summary>
    /// Shared terminal path: outcome logging under ServiceOnly, capacity release, CSV persistence, Remaining decrement.
    /// </summary>
    private void CompleteTerminalJob(
        JobEntry entry,
        ScheduledJobTerminalState state,
        TimeSpan? executionDuration,
        Exception? error)
    {
        MikrotikScheduledJob job = entry.Job;
        // Terminal outcomes and import completion (CSV archive) are service-lifecycle events.
        // Keep ServiceOnly active through OnJobTerminal so ambient DeviceLogScope cannot
        // divert ImportId-bearing archive messages into a device file.
        using (DeviceLogging.BeginServiceOnly())
        {
            switch (state)
            {
                case ScheduledJobTerminalState.Expired:
                    _logger.LogWarning(
                        "Job expired. ImportId={ImportId}, JobId={JobId}, RowNumber={RowNumber}, RouterName={RouterName}, RouterIp={RouterIp}, SourceFileName={SourceFileName}, DateStartUtc={DateStartUtc}, DateStopUtc={DateStopUtc}, UtcNow={UtcNow}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        job.RowNumber,
                        job.RouterName,
                        job.RouterIp,
                        job.SourceFileName,
                        job.DateStartUtc,
                        job.DateStopUtc,
                        _timeProvider.GetUtcNow().UtcDateTime,
                        nameof(ScheduledJobTerminalState.Expired));
                    break;

                case ScheduledJobTerminalState.Succeeded:
                    _logger.LogInformation(
                        "Job terminal Succeeded. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, ExecutionDuration={ExecutionDuration}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp,
                        executionDuration,
                        nameof(ScheduledJobTerminalState.Succeeded));
                    break;

                case ScheduledJobTerminalState.AlreadyUpToDate:
                    _logger.LogInformation(
                        "Job terminal AlreadyUpToDate. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, ExecutionDuration={ExecutionDuration}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp,
                        executionDuration,
                        nameof(ScheduledJobTerminalState.AlreadyUpToDate));
                    break;

                case ScheduledJobTerminalState.Blocked:
                    _logger.LogWarning(
                        error,
                        "Job terminal Blocked (device was not upgraded). ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, ExecutionDuration={ExecutionDuration}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp,
                        executionDuration,
                        nameof(ScheduledJobTerminalState.Blocked));
                    break;

                case ScheduledJobTerminalState.Skipped:
                    _logger.LogWarning(
                        error,
                        "Job terminal Skipped. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, ExecutionDuration={ExecutionDuration}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp,
                        executionDuration,
                        nameof(ScheduledJobTerminalState.Skipped));
                    break;

                case ScheduledJobTerminalState.Failed:
                    _logger.LogError(
                        error,
                        "Job terminal Failed. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, ExecutionDuration={ExecutionDuration}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp,
                        executionDuration,
                        nameof(ScheduledJobTerminalState.Failed));
                    break;

                case ScheduledJobTerminalState.Cancelled:
                    _logger.LogWarning(
                        "Job terminal Cancelled. ImportId={ImportId}, JobId={JobId}, RouterName={RouterName}, RouterIp={RouterIp}, ExecutionDuration={ExecutionDuration}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        job.RouterName,
                        job.RouterIp,
                        executionDuration,
                        nameof(ScheduledJobTerminalState.Cancelled));
                    break;

                case ScheduledJobTerminalState.ObservedEligible:
                    _logger.LogInformation(
                        "Job terminal ObservedEligible (legacy). ImportId={ImportId}, JobId={JobId}, JobOutcome={JobOutcome}",
                        job.ImportId,
                        job.JobId,
                        nameof(ScheduledJobTerminalState.ObservedEligible));
                    break;
            }

            ReleaseCapacity(entry);

            // Persist CSV results before import completion may move Accepted → Completed.
            PersistCsvCompletion(job, state, error);

            OnJobTerminal(job.ImportId);
        }
    }

    /// <summary>
    /// Synchronously persists CSV completion; failures are logged and do not change the in-memory outcome.
    /// </summary>
    private void PersistCsvCompletion(MikrotikScheduledJob job, ScheduledJobTerminalState state, Exception? error)
    {
        try
        {
            _csvResultWriter
                .RecordJobCompletedAsync(
                    job,
                    _timeProvider.GetUtcNow(),
                    state,
                    error?.Message,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected failure while persisting CSV completion. ImportId={ImportId}, JobId={JobId}. In-memory job outcome is unchanged.",
                job.ImportId,
                job.JobId);
        }
    }

    /// <summary>
    /// Moves jobs buffered in <see cref="ImportBatch.HeldForDispatch"/> into the priority queue after AcceptedOnDisk.
    /// </summary>
    private void ReleaseHeldJobsForDispatch(ImportBatch batch)
    {
        List<(JobEntry Entry, JobPriority Priority)> held;
        lock (batch.Sync)
        {
            if (batch.HeldForDispatch.Count == 0)
            {
                return;
            }

            held = [.. batch.HeldForDispatch];
            batch.HeldForDispatch.Clear();
        }

        lock (_gate)
        {
            foreach ((JobEntry entry, JobPriority priority) in held)
            {
                if (entry.IsRemovedOrTerminalOrDispatched)
                {
                    continue;
                }

                _queue.Enqueue(entry, priority);
            }
        }

        SignalWake();
    }

    /// <summary>
    /// Decrements Remaining for a terminal scheduled job and attempts closure when work and AcceptedOnDisk allow.
    /// </summary>
    private void OnJobTerminal(Guid importId)
    {
        if (!_imports.TryGetValue(importId, out ImportBatch? batch))
        {
            return;
        }

        bool shouldAttemptClosure;
        lock (batch.Sync)
        {
            if (batch.Aborted)
            {
                return;
            }

            if (batch.Remaining > 0)
            {
                batch.Remaining--;
            }

            shouldAttemptClosure = batch.Remaining == 0
                                   && batch.AcceptedPath is not null
                                   && batch.AcceptedOnDisk
                                   && !batch.Archived;
        }

        if (shouldAttemptClosure)
        {
            AttemptImportClosure(batch);
        }
    }

    /// <summary>
    /// Periodic sweep: retries closure for any import that is fully terminal, accepted on disk, and not mid-completion.
    /// </summary>
    private void TryCompletePendingImports()
    {
        foreach (ImportBatch batch in _imports.Values)
        {
            bool shouldAttempt;
            lock (batch.Sync)
            {
                shouldAttempt = !batch.Aborted
                                && !batch.Archived
                                && batch.Remaining == 0
                                && batch.AcceptedPath is not null
                                && batch.AcceptedOnDisk
                                && !batch.CompletionInProgress;
            }

            if (shouldAttempt)
            {
                AttemptImportClosure(batch);
            }
        }
    }

    /// <summary>
    /// Closes an import whose outstanding scheduled work is zero: either archive Accepted→Completed
    /// (full activation) or retire tracking while leaving the Accepted CSV in place (incomplete activation).
    /// </summary>
    private void AttemptImportClosure(ImportBatch batch)
    {
        bool retireWithoutArchive;
        lock (batch.Sync)
        {
            if (batch.Aborted
                || batch.Archived
                || batch.Remaining != 0
                || batch.AcceptedPath is null
                || !batch.AcceptedOnDisk
                || batch.CompletionInProgress)
            {
                return;
            }

            retireWithoutArchive = batch.ActivationFinalized
                                   && batch.ActivationIncomplete
                                   && batch.ScheduledCount < batch.ValidatedCount;
        }

        if (retireWithoutArchive)
        {
            RetireImportLeavingAcceptedFile(batch);
        }
        else
        {
            TryCompleteImport(batch);
        }
    }

    /// <summary>
    /// Drops import tracking and path-store entry while leaving the Accepted CSV on disk for manual re-import.
    /// Used when activation was incomplete and Remaining has reached zero.
    /// </summary>
    private void RetireImportLeavingAcceptedFile(ImportBatch batch)
    {
        string? acceptedPath;
        int validated;
        int scheduled;
        lock (batch.Sync)
        {
            if (batch.Aborted
                || batch.Archived
                || batch.Remaining != 0
                || batch.AcceptedPath is null
                || !batch.AcceptedOnDisk
                || batch.CompletionInProgress)
            {
                return;
            }

            batch.CompletionInProgress = true;
            acceptedPath = batch.AcceptedPath;
            validated = batch.ValidatedCount;
            scheduled = batch.ScheduledCount;
            batch.HeldForDispatch.Clear();
        }

        try
        {
            lock (batch.Sync)
            {
                batch.Archived = true;
            }

            _imports.TryRemove(batch.ImportId, out _);
            _csvPathStore.Clear(batch.ImportId);

            _logger.LogWarning(
                "Import activation incomplete; leaving Accepted CSV in place for inspection/manual re-import. ImportId={ImportId}, Path={Path}, ValidatedRows={ValidatedRows}, ScheduledRows={ScheduledRows}, NeverScheduledRows={NeverScheduledRows}. File was not moved to Completed or Rejected.",
                batch.ImportId,
                acceptedPath,
                validated,
                scheduled,
                validated - scheduled);
        }
        finally
        {
            lock (batch.Sync)
            {
                if (!batch.Archived)
                {
                    batch.CompletionInProgress = false;
                }
            }
        }
    }

    /// <summary>
    /// Archives Accepted→Completed when activation was complete (or fully scheduled).
    /// On move failure, clears CompletionInProgress and wakes the loop for retry.
    /// </summary>
    private void TryCompleteImport(ImportBatch batch)
    {
        string? acceptedPath;
        lock (batch.Sync)
        {
            if (batch.Aborted
                || batch.Archived
                || batch.Remaining != 0
                || batch.AcceptedPath is null
                || !batch.AcceptedOnDisk)
            {
                return;
            }

            // Incomplete activation must not be archived as a successful Completed import.
            if (batch.ActivationFinalized
                && batch.ActivationIncomplete
                && batch.ScheduledCount < batch.ValidatedCount)
            {
                return;
            }

            if (batch.CompletionInProgress)
            {
                return;
            }

            batch.CompletionInProgress = true;
            acceptedPath = batch.AcceptedPath;
        }

        bool scheduleRetry = false;
        try
        {
            if (!File.Exists(acceptedPath))
            {
                _logger.LogDebug(
                    "Import {ImportId} is ready to complete but accepted CSV is not visible yet: {Path}",
                    batch.ImportId,
                    acceptedPath);
                return;
            }

            string fileName = Path.GetFileName(acceptedPath);
            string completedPath = _fileMover.MoveSafely(
                acceptedPath,
                _paths.Completed,
                fileName,
                _logger);

            lock (batch.Sync)
            {
                batch.Archived = true;
            }

            _imports.TryRemove(batch.ImportId, out _);
            _csvPathStore.Clear(batch.ImportId);

            _logger.LogInformation(
                "CSV archived to Completed: {FileName}. ImportId={ImportId}, Path={Path}",
                fileName,
                batch.ImportId,
                completedPath);
        }
        catch (Exception ex)
        {
            scheduleRetry = true;
            _logger.LogError(
                ex,
                "Failed to move accepted CSV to Completed for ImportId={ImportId}, Path={Path}. Import remains tracked for retry.",
                batch.ImportId,
                acceptedPath);
        }
        finally
        {
            lock (batch.Sync)
            {
                if (!batch.Archived)
                {
                    batch.CompletionInProgress = false;
                }
            }

            if (scheduleRetry)
            {
                SignalWake();
            }
        }
    }

    /// <summary>
    /// Caller must hold <c>_gate</c>. Returns the next non-stale queue head's eligible time, or null when empty.
    /// </summary>
    private DateTimeOffset? PeekNextWakeUtc_NoLock()
    {
        while (_queue.Count > 0)
        {
            if (!_queue.TryPeek(out JobEntry? entry, out JobPriority priority))
            {
                return null;
            }

            if (entry.IsRemovedOrTerminalOrDispatched)
            {
                _queue.Dequeue();
                continue;
            }

            return new DateTimeOffset(DateTime.SpecifyKind(priority.DateStartUtc, DateTimeKind.Utc));
        }

        return null;
    }

    /// <summary>Clamps delays to a positive range compatible with <see cref="Task.Delay(TimeSpan)"/>.</summary>
    private static TimeSpan CapDelay(TimeSpan delay)
    {
        TimeSpan max = TimeSpan.FromMilliseconds(int.MaxValue - 1);
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > max ? max : delay;
    }

    /// <summary>
    /// Waits until <paramref name="due"/>, <paramref name="wakeTask"/>, or cancellation.
    /// Avoids linking delay cancellation to wake so a wake cannot tear down <see cref="RunAsync"/>.
    /// </summary>
    private async Task WaitUntilAsync(DateTimeOffset due, Task wakeTask, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (now >= due)
            {
                return;
            }

            TimeSpan delay = CapDelay(due - now);
            if (delay <= TimeSpan.Zero)
            {
                return;
            }

            // Link only to the caller token. Do not Cancel/await the delay CTS on wake —
            // disposing it via `using` is enough, and awaiting a cancelled delay Task can
            // surface OperationCanceledException that is NOT tied to the scheduler token,
            // which would tear down RunAsync and discard pending jobs.
            using CancellationTokenSource delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delayTask = Task.Delay(delay, _timeProvider, delayCts.Token);

            // Re-check after timer registration: a clock jump may have landed between the
            // relative-delay computation and CreateTimer (FakeTimeProvider.SetUtcNow).
            if (_timeProvider.GetUtcNow() >= due)
            {
                return;
            }

            _ = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (_timeProvider.GetUtcNow() >= due)
            {
                return;
            }

            // Wake without reaching due — re-evaluate; otherwise timer fired early relative
            // to an absolute clock jump and the loop rechecks.
            if (wakeTask.IsCompleted)
            {
                return;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Waits for a wake signal or cancellation when the queue has no next due time.</summary>
    private static async Task WaitForSignalAsync(Task wakeTask, CancellationToken cancellationToken)
    {
        Task cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        _ = await Task.WhenAny(wakeTask, cancelTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Captures the current wake TCS task under <c>_wakeGate</c> for lost-wakeup-safe waits.</summary>
    private Task SnapshotWakeTask()
    {
        lock (_wakeGate)
        {
            return _wakeSignal.Task;
        }
    }

    /// <summary>Completes the current wake signal and installs a fresh one.</summary>
    private void SignalWake()
    {
        TaskCompletionSource previous;
        lock (_wakeGate)
        {
            previous = _wakeSignal;
            _wakeSignal = NewWakeSignal();
        }

        previous.TrySetResult();
    }

    /// <summary>Creates a wake TCS that runs continuations asynchronously.</summary>
    private static TaskCompletionSource NewWakeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Releases one capacity slot once per entry (idempotent via <see cref="JobEntry.TryReleaseCapacity"/>).</summary>
    private void ReleaseCapacity(JobEntry entry)
    {
        if (entry.TryReleaseCapacity())
        {
            try
            {
                _capacity.Release();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown completed releases after semaphore disposal should not occur; ignore if raced.
            }
        }
    }

    /// <summary>Throws when lifecycle is not <see cref="Lifecycle.Running"/>.</summary>
    private void ThrowIfNotRunning()
    {
        lock (_lifecycleGate)
        {
            if (_lifecycle != Lifecycle.Running)
            {
                throw new InvalidOperationException("Scheduler is stopping or disposed.");
            }
        }
    }

    /// <summary>Increments the in-flight schedule counter while still Running.</summary>
    private void EnterScheduleOperation()
    {
        lock (_lifecycleGate)
        {
            if (_lifecycle != Lifecycle.Running)
            {
                throw new InvalidOperationException("Scheduler is stopping or disposed.");
            }

            _activeScheduleOperations++;
        }
    }

    /// <summary>Decrements the in-flight schedule counter and pulses stop waiters when zero during Stopping.</summary>
    private void ExitScheduleOperation()
    {
        lock (_lifecycleGate)
        {
            _activeScheduleOperations--;
            if (_lifecycle == Lifecycle.Stopping && _activeScheduleOperations == 0)
            {
                Monitor.PulseAll(_lifecycleGate);
            }
        }
    }

    /// <summary>
    /// Transitions Running→Stopping→Disposed, waits for schedule ops, discards pending work, disposes capacity once.
    /// </summary>
    private async Task StopCoreAsync()
    {
        bool shouldDisposeCapacity = false;
        lock (_lifecycleGate)
        {
            if (_lifecycle == Lifecycle.Disposed)
            {
                return;
            }

            if (_lifecycle == Lifecycle.Running)
            {
                _lifecycle = Lifecycle.Stopping;
                try
                {
                    _stoppingCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                SignalWake();
            }

            while (_activeScheduleOperations > 0)
            {
                Monitor.Wait(_lifecycleGate, 50);
            }

            if (_lifecycle == Lifecycle.Stopping)
            {
                shouldDisposeCapacity = true;
                _lifecycle = Lifecycle.Disposed;
            }
        }

        DiscardAllPendingOnShutdown();

        if (shouldDisposeCapacity)
        {
            _capacity.Dispose();
            _stoppingCts.Dispose();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Clears queues/tables, completes the execution channel, marks entries removed, and drops import tracking.
    /// </summary>
    private void DiscardAllPendingOnShutdown()
    {
        List<JobEntry> pending;
        lock (_gate)
        {
            pending = _entries.Values.Concat(_dispatched.Values).ToList();
            _entries.Clear();
            _dispatched.Clear();
            _queue.Clear();
        }

        _executionQueue.Complete();

        foreach (JobEntry entry in pending)
        {
            if (entry.TryMarkRemoved())
            {
                ReleaseCapacity(entry);
            }
        }

        _imports.Clear();
    }

    /// <summary>
    /// Per-import tracking: Remaining work, Accepted path/on-disk flags, held pre-dispatch jobs, and closure state.
    /// </summary>
    /// <param name="importId">Import identifier.</param>
    /// <param name="jobCount">Validated row count; seeds <see cref="Remaining"/>.</param>
    private sealed class ImportBatch(Guid importId, int jobCount)
    {
        /// <summary>Import identifier shared with scheduled jobs.</summary>
        public Guid ImportId { get; } = importId;

        /// <summary>Fine-grained lock for Remaining/path/activation fields (nested under scheduler <c>_gate</c> when both needed).</summary>
        public object Sync { get; } = new();

        /// <summary>Validated CSV row count at registration (immutable).</summary>
        public int ValidatedCount { get; } = jobCount;

        /// <summary>
        /// Outstanding work. Starts as <see cref="ValidatedCount"/>; decrements on terminal scheduled
        /// jobs; reduced by never-scheduled rows when activation is finalized.
        /// </summary>
        public int Remaining { get; set; } = jobCount;

        /// <summary>Rows successfully accepted by <see cref="ScheduleAsync"/> (set at activation finalize).</summary>
        public int ScheduledCount { get; set; }

        /// <summary>True after <see cref="FinalizeImportActivation"/> (or equivalent) has run once.</summary>
        public bool ActivationFinalized { get; set; }

        /// <summary>
        /// True when activation stopped early; with <see cref="ScheduledCount"/> &lt; <see cref="ValidatedCount"/>
        /// prevents Accepted→Completed archival.
        /// </summary>
        public bool ActivationIncomplete { get; set; }

        /// <summary>Intended or confirmed Accepted path string (see <see cref="AcceptedOnDisk"/>).</summary>
        public string? AcceptedPath { get; set; }

        /// <summary>
        /// True once the CSV is known to be on disk under Accepted (not merely an intent path).
        /// </summary>
        public bool AcceptedOnDisk { get; set; }

        /// <summary>
        /// Jobs scheduled before <see cref="AcceptedOnDisk"/>; released into the dispatch queue
        /// only from <see cref="SetAcceptedPath"/>.
        /// </summary>
        public List<(JobEntry Entry, JobPriority Priority)> HeldForDispatch { get; } = [];

        /// <summary>
        /// True after the import lifecycle is closed (Completed archive or retire-leaving-Accepted).
        /// </summary>
        public bool Archived { get; set; }

        /// <summary>Prevents concurrent Accepted→Completed / retire attempts for the same batch.</summary>
        public bool CompletionInProgress { get; set; }

        /// <summary>True after <see cref="AbortImportAsync"/> marked the batch aborted under <see cref="Sync"/>.</summary>
        public bool Aborted { get; set; }

        /// <summary>
        /// Lock-free abort signal for schedule/dispatch checks (<c>0</c> active, non-zero aborted).
        /// </summary>
        public int AbortedFlag;
    }

    /// <summary>
    /// Atomic lifecycle wrapper around one <see cref="MikrotikScheduledJob"/> (pending/dispatched/terminal/removed).
    /// </summary>
    /// <param name="job">Scheduled job payload.</param>
    private sealed class JobEntry(MikrotikScheduledJob job)
    {
        // 0 = pending, 1 = terminal, 2 = removed, 3 = dispatched
        /// <summary>Entry lifecycle state accessed with interlocked operations: 0 pending, 1 terminal, 2 removed, 3 dispatched.</summary>
        private int _state;
        /// <summary>Set to 1 once the entry's capacity slot has been released, making release idempotent.</summary>
        private int _capacityReleased;

        /// <summary>Job payload shared with the execution pool.</summary>
        public MikrotikScheduledJob Job { get; } = job;

        /// <summary>True when state is not pending (stale priority-queue heads can be skipped).</summary>
        public bool IsRemovedOrTerminalOrDispatched => Volatile.Read(ref _state) != 0;

        /// <summary>CAS pending→dispatched.</summary>
        public bool TryMarkDispatched() => Interlocked.CompareExchange(ref _state, 3, 0) == 0;

        /// <summary>CAS dispatched→pending for requeue/retry.</summary>
        public bool TryMarkRequeued() => Interlocked.CompareExchange(ref _state, 0, 3) == 3;

        /// <summary>
        /// CAS pending or dispatched → terminal. The <paramref name="state"/> argument is unused for CAS
        /// (outcome is recorded by the caller); returns false if already terminal/removed.
        /// </summary>
        public bool TryMarkTerminal(ScheduledJobTerminalState state)
        {
            _ = state;
            while (true)
            {
                int current = Volatile.Read(ref _state);
                if (current is 1 or 2)
                {
                    return false;
                }

                // Terminal from pending (expired before dispatch) or from dispatched.
                if (current is 0 or 3)
                {
                    if (Interlocked.CompareExchange(ref _state, 1, current) == current)
                    {
                        return true;
                    }

                    continue;
                }

                return false;
            }
        }

        /// <summary>CAS any non-terminal/non-removed state → removed (abort/shutdown).</summary>
        public bool TryMarkRemoved()
        {
            while (true)
            {
                int current = Volatile.Read(ref _state);
                if (current is 1 or 2)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, 2, current) == current)
                {
                    return true;
                }
            }
        }

        /// <summary>Returns true exactly once so capacity is released a single time per entry.</summary>
        public bool TryReleaseCapacity() => Interlocked.Exchange(ref _capacityReleased, 1) == 0;
    }

    /// <summary>
    /// Priority-queue key: earlier <see cref="DateStartUtc"/> first, then insertion <see cref="Sequence"/>.
    /// </summary>
    /// <param name="DateStartUtc">Effective eligibility instant (schedule start or retry).</param>
    /// <param name="Sequence">Monotonic tie-breaker from the scheduler.</param>
    private readonly record struct JobPriority(DateTime DateStartUtc, int Sequence) : IComparable<JobPriority>
    {
        /// <inheritdoc />
        public int CompareTo(JobPriority other)
        {
            int cmp = DateStartUtc.CompareTo(other.DateStartUtc);
            return cmp != 0 ? cmp : Sequence.CompareTo(other.Sequence);
        }
    }
}

/// <summary>
/// Hosts the in-memory scheduler loop as a <see cref="BackgroundService"/>.
/// </summary>
/// <param name="scheduler">Scheduler whose <see cref="IInMemoryJobScheduler.RunAsync"/> is hosted.</param>
/// <param name="logger">Service start/stop logging.</param>
internal sealed class InMemoryJobSchedulerService(
    IInMemoryJobScheduler scheduler,
    ILogger<InMemoryJobSchedulerService> logger) : BackgroundService
{
    /// <summary>Runs <see cref="IInMemoryJobScheduler.RunAsync"/> until host cancellation.</summary>
    /// <param name="stoppingToken">Host stop token.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Scheduled job scheduler service starting");
        try
        {
            await scheduler.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        finally
        {
            logger.LogInformation("Scheduled job scheduler service stopped");
        }
    }
}
