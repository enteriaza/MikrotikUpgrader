using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

internal interface IInMemoryJobScheduler
{
    int MaximumPendingJobs { get; }

    int PendingCount { get; }

    bool IsTracked(Guid importId);

    void RegisterImport(Guid importId, int jobCount);

    void SetAcceptedPath(Guid importId, string acceptedPath);

    /// <summary>
    /// Registers the intended Accepted path before the filesystem move completes.
    /// Completion to Completed is blocked until <see cref="SetAcceptedPath"/> confirms the on-disk path.
    /// </summary>
    void BindAcceptedPathIntent(Guid importId, string intendedAcceptedPath);

    ValueTask ScheduleAsync(MikrotikScheduledJob job, CancellationToken cancellationToken);

    Task AbortImportAsync(Guid importId, CancellationToken cancellationToken);

    Task RunAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Time-aware bounded in-memory scheduler. Future jobs never block eligible jobs.
/// </summary>
internal sealed class InMemoryJobScheduler : IInMemoryJobScheduler, IRouterJobExecutionCompleter, IAsyncDisposable
{
    private enum Lifecycle
    {
        Running = 0,
        Stopping = 1,
        Disposed = 2,
    }

    private readonly ScheduledJobsPaths _paths;
    private readonly IScheduledJobFileMover _fileMover;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InMemoryJobScheduler> _logger;
    private readonly RouterJobExecutionQueue _executionQueue;
    private readonly SemaphoreSlim _capacity;
    private readonly object _gate = new();
    private readonly PriorityQueue<JobEntry, JobPriority> _queue = new();
    private readonly Dictionary<(Guid ImportId, int RowNumber), JobEntry> _entries = new();
    private readonly Dictionary<(Guid ImportId, int RowNumber), JobEntry> _dispatched = new();
    private readonly ConcurrentDictionary<Guid, ImportBatch> _imports = new();
    private readonly object _wakeGate = new();
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _stoppingCts = new();
    private TaskCompletionSource _wakeSignal = NewWakeSignal();
    private int _sequence;
    private int _activeScheduleOperations;
    private Lifecycle _lifecycle = Lifecycle.Running;

    public InMemoryJobScheduler(
        IOptions<ScheduledJobsOptions> options,
        ScheduledJobsPaths paths,
        IScheduledJobFileMover fileMover,
        RouterJobExecutionQueue executionQueue,
        TimeProvider timeProvider,
        ILogger<InMemoryJobScheduler> logger)
    {
        MaximumPendingJobs = options.Value.MaximumPendingJobs;
        _capacity = new SemaphoreSlim(MaximumPendingJobs, MaximumPendingJobs);
        _paths = paths;
        _fileMover = fileMover;
        _executionQueue = executionQueue;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public int MaximumPendingJobs { get; }

    /// <summary>
    /// Test seam: invoked after the wake task is snapshotted and before the queue is inspected.
    /// Used to prove lost-wakeup-free signalling.
    /// </summary>
    internal Func<Task>? AfterWakeSnapshotBeforeQueueCheckAsync { get; set; }

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

        _logger.LogInformation(
            "Import accepted path set: ImportId={ImportId}, Path={Path}, Remaining={Remaining}",
            importId,
            acceptedPath,
            batch.Remaining);

        if (shouldAttemptCompletion)
        {
            TryCompleteImport(batch);
        }
    }

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
                _entries[key] = entry;
                _queue.Enqueue(entry, new JobPriority(job.DateStartUtc, sequence));
                capacityHeld = false;
            }

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
        SignalWake();

        _logger.LogWarning(
            "Import aborted: ImportId={ImportId}. ReleasedPending={ReleasedPending}. CSV remains in Processing if not yet Accepted.",
            importId,
            toRelease.Count);

        return Task.CompletedTask;
    }

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

    public Task StopAsync(CancellationToken cancellationToken = default) => StopCoreAsync();

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
    }

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

    public bool TryRequeueDispatchedJob(object entryToken)
    {
        if (entryToken is not JobEntry entry)
        {
            throw new ArgumentException("Invalid scheduler entry token.", nameof(entryToken));
        }

        return TryRequeueDispatchedJob(entry);
    }

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
            _queue.Enqueue(entry, new JobPriority(entry.Job.DateStartUtc, sequence));
        }

        SignalWake();
        return true;
    }

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

    private void CompleteTerminalJob(
        JobEntry entry,
        ScheduledJobTerminalState state,
        TimeSpan? executionDuration,
        Exception? error)
    {
        MikrotikScheduledJob job = entry.Job;
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
        OnJobTerminal(job.ImportId);
    }

    private void OnJobTerminal(Guid importId)
    {
        if (!_imports.TryGetValue(importId, out ImportBatch? batch))
        {
            return;
        }

        bool shouldAttemptCompletion = false;
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

            shouldAttemptCompletion = batch.Remaining == 0
                                      && batch.AcceptedPath is not null
                                      && batch.AcceptedOnDisk
                                      && !batch.Archived;
        }

        if (shouldAttemptCompletion)
        {
            TryCompleteImport(batch);
        }
    }

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
                TryCompleteImport(batch);
            }
        }
    }

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

    private static TimeSpan CapDelay(TimeSpan delay)
    {
        TimeSpan max = TimeSpan.FromMilliseconds(int.MaxValue - 1);
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return delay > max ? max : delay;
    }

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

    private static async Task WaitForSignalAsync(Task wakeTask, CancellationToken cancellationToken)
    {
        Task cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        _ = await Task.WhenAny(wakeTask, cancelTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private Task SnapshotWakeTask()
    {
        lock (_wakeGate)
        {
            return _wakeSignal.Task;
        }
    }

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

    private static TaskCompletionSource NewWakeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private sealed class ImportBatch(Guid importId, int jobCount)
    {
        public Guid ImportId { get; } = importId;

        public object Sync { get; } = new();

        public int Remaining { get; set; } = jobCount;

        public string? AcceptedPath { get; set; }

        /// <summary>
        /// True once the CSV is known to be on disk under Accepted (not merely an intent path).
        /// </summary>
        public bool AcceptedOnDisk { get; set; }

        /// <summary>
        /// True after the CSV has been successfully moved to Completed.
        /// </summary>
        public bool Archived { get; set; }

        public bool CompletionInProgress { get; set; }

        public bool Aborted { get; set; }

        public int AbortedFlag;
    }

    private sealed class JobEntry(MikrotikScheduledJob job)
    {
        // 0 = pending, 1 = terminal, 2 = removed, 3 = dispatched
        private int _state;
        private int _capacityReleased;

        public MikrotikScheduledJob Job { get; } = job;

        public bool IsRemovedOrTerminalOrDispatched => Volatile.Read(ref _state) != 0;

        public bool TryMarkDispatched() => Interlocked.CompareExchange(ref _state, 3, 0) == 0;

        public bool TryMarkRequeued() => Interlocked.CompareExchange(ref _state, 0, 3) == 3;

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

        public bool TryReleaseCapacity() => Interlocked.Exchange(ref _capacityReleased, 1) == 0;
    }

    private readonly record struct JobPriority(DateTime DateStartUtc, int Sequence) : IComparable<JobPriority>
    {
        public int CompareTo(JobPriority other)
        {
            int cmp = DateStartUtc.CompareTo(other.DateStartUtc);
            return cmp != 0 ? cmp : Sequence.CompareTo(other.Sequence);
        }
    }
}

/// <summary>
/// Hosts the in-memory scheduler loop.
/// </summary>
internal sealed class InMemoryJobSchedulerService(
    IInMemoryJobScheduler scheduler,
    ILogger<InMemoryJobSchedulerService> logger) : BackgroundService
{
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
