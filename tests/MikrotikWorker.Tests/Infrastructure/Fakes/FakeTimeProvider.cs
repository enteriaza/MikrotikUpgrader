// -----------------------------------------------------------------------------
// File: FakeTimeProvider.cs
//
// Description:
// Controllable TimeProvider for deterministic scheduler and backup timestamp tests.
//
// -----------------------------------------------------------------------------

using System.Diagnostics;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> for scheduler and backup tests.
/// </summary>
/// <remarks>
/// Advancing or setting UTC time collects due timer callbacks under a lock, then invokes them
/// outside the lock to avoid reentrancy deadlocks with timer <c>Change</c> calls.
/// </remarks>
internal sealed class FakeTimeProvider : TimeProvider
{
    /// <summary>Guards <see cref="_utcNow"/> and <see cref="_timers"/>.</summary>
    private readonly object _gate = new();

    /// <summary>Current fake UTC time.</summary>
    private DateTimeOffset _utcNow;

    /// <summary>Timers created by this provider that have not been disposed.</summary>
    private readonly List<FakeTimer> _timers = [];

    /// <summary>Creates a provider starting at <paramref name="utcNow"/>.</summary>
    /// <param name="utcNow">Initial fake UTC time.</param>
    public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    /// <inheritdoc />
    public override long TimestampFrequency => Stopwatch.Frequency;

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        TimeSpan elapsed = GetUtcNow() - DateTimeOffset.UnixEpoch;
        return (long)(elapsed.TotalSeconds * TimestampFrequency);
    }

    /// <summary>Sets absolute UTC time and fires any timers that become due.</summary>
    /// <param name="utcNow">New fake UTC time.</param>
    public void SetUtcNow(DateTimeOffset utcNow)
    {
        List<Action> callbacks;
        lock (_gate)
        {
            _utcNow = utcNow;
            callbacks = CollectDueCallbacks_NoLock();
        }

        Invoke(callbacks);
    }

    /// <summary>Advances UTC time by <paramref name="delta"/> and fires due timers.</summary>
    /// <param name="delta">Non-negative amount of time to advance.</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="delta"/> is negative.</exception>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        List<Action> callbacks;
        lock (_gate)
        {
            _utcNow += delta;
            callbacks = CollectDueCallbacks_NoLock();
        }

        Invoke(callbacks);
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        FakeTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Re-evaluates registered timers against the current UTC time.</summary>
    internal void EvaluateTimers()
    {
        List<Action> callbacks;
        lock (_gate)
        {
            callbacks = CollectDueCallbacks_NoLock();
        }

        Invoke(callbacks);
    }

    /// <summary>Registers a timer for due-time evaluation.</summary>
    /// <param name="timer">Timer to track.</param>
    private void Register(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Add(timer);
        }
    }

    /// <summary>Removes a disposed timer from evaluation.</summary>
    /// <param name="timer">Timer to stop tracking.</param>
    private void Unregister(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    /// <summary>
    /// Collects fire callbacks for all due timers. Caller must hold <see cref="_gate"/>.
    /// </summary>
    /// <returns>Callbacks to invoke once the lock is released.</returns>
    private List<Action> CollectDueCallbacks_NoLock()
    {
        List<Action> callbacks = [];
        bool fired;
        do
        {
            fired = false;
            foreach (FakeTimer timer in _timers.ToArray())
            {
                if (timer.TryCollectFire(_utcNow, out Action? callback))
                {
                    callbacks.Add(callback!);
                    fired = true;
                }
            }
        }
        while (fired);

        return callbacks;
    }

    /// <summary>Invokes collected timer callbacks outside the provider lock.</summary>
    /// <param name="callbacks">Callbacks gathered by <see cref="CollectDueCallbacks_NoLock"/>.</param>
    private static void Invoke(List<Action> callbacks)
    {
        foreach (Action callback in callbacks)
        {
            callback();
        }
    }

    /// <summary>Single fake timer bound to this provider.</summary>
    private sealed class FakeTimer : ITimer
    {
        /// <summary>Owning provider that supplies the current time and tracks this timer.</summary>
        private readonly FakeTimeProvider _provider;

        /// <summary>Callback invoked when the timer fires.</summary>
        private readonly TimerCallback _callback;

        /// <summary>State object passed to <see cref="_callback"/>.</summary>
        private readonly object? _state;

        /// <summary>Guards the mutable scheduling fields of this timer.</summary>
        private readonly object _gate = new();

        /// <summary>Next UTC time at which the timer is due, or <see langword="null"/> when not scheduled.</summary>
        private DateTimeOffset? _nextDue;

        /// <summary>Repeat interval; infinite or negative means one-shot.</summary>
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        /// <summary>True once the timer has been disposed.</summary>
        private bool _disposed;

        /// <summary>Creates and registers the timer with <paramref name="provider"/>.</summary>
        /// <param name="provider">Owning provider.</param>
        /// <param name="callback">Callback to invoke when the timer fires.</param>
        /// <param name="state">State object passed to <paramref name="callback"/>.</param>
        public FakeTimer(FakeTimeProvider provider, TimerCallback callback, object? state)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
            _provider.Register(this);
        }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _nextDue = null;
                }
                else
                {
                    _nextDue = _provider.GetUtcNow() + dueTime;
                }
            }

            _provider.EvaluateTimers();
            return true;
        }

        /// <summary>
        /// If due at <paramref name="utcNow"/>, schedules the callback and advances/clears next due.
        /// </summary>
        /// <param name="utcNow">Current fake UTC time.</param>
        /// <param name="callback">The deferred callback invocation when the timer is due; otherwise <see langword="null"/>.</param>
        /// <returns><see langword="true"/> when the timer was due and <paramref name="callback"/> is set.</returns>
        public bool TryCollectFire(DateTimeOffset utcNow, out Action? callback)
        {
            lock (_gate)
            {
                if (_disposed || _nextDue is null || _nextDue > utcNow)
                {
                    callback = null;
                    return false;
                }

                TimerCallback cb = _callback;
                object? state = _state;
                if (_period == Timeout.InfiniteTimeSpan || _period < TimeSpan.Zero)
                {
                    _nextDue = null;
                }
                else
                {
                    _nextDue = utcNow + _period;
                }

                callback = () => cb(state);
                return true;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _nextDue = null;
            }

            _provider.Unregister(this);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
