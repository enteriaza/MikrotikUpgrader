using System.Diagnostics;

namespace MikrotikWorker.Tests;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> that advances timers when time is set/advanced.
/// Timer callbacks are invoked outside the provider lock to avoid reentrancy deadlocks.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow;
    private readonly List<FakeTimer> _timers = [];

    public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long TimestampFrequency => Stopwatch.Frequency;

    public override long GetTimestamp()
    {
        TimeSpan elapsed = GetUtcNow() - DateTimeOffset.UnixEpoch;
        return (long)(elapsed.TotalSeconds * TimestampFrequency);
    }

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

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        FakeTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void EvaluateTimers()
    {
        List<Action> callbacks;
        lock (_gate)
        {
            callbacks = CollectDueCallbacks_NoLock();
        }

        Invoke(callbacks);
    }

    private void Register(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Add(timer);
        }
    }

    private void Unregister(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

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

    private static void Invoke(List<Action> callbacks)
    {
        foreach (Action callback in callbacks)
        {
            callback();
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _provider;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly object _gate = new();
        private DateTimeOffset? _nextDue;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public FakeTimer(FakeTimeProvider provider, TimerCallback callback, object? state)
        {
            _provider = provider;
            _callback = callback;
            _state = state;
            _provider.Register(this);
        }

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

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
