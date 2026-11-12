// -----------------------------------------------------------------------------
// File: RecordingLoggerProvider.cs
//
// Description:
// ILoggerProvider that records formatted log lines for assertions in hosting and
// scheduler tests (message text matching, wait-until-logged helpers).
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;

/// <summary>
/// Captures log output into an in-memory queue for deterministic test assertions.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    /// <summary>Thread-safe queue of formatted <c>category|level|text</c> log lines.</summary>
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>Snapshot of recorded messages as <c>category|level|text</c>.</summary>
    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>
    /// Polls until at least <paramref name="minimumCount"/> recorded messages match
    /// <paramref name="predicate"/>, or throws <see cref="TimeoutException"/>.
    /// </summary>
    public async Task WaitForAsync(
        Func<string, bool> predicate,
        int minimumCount,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (CountMatches(predicate) < minimumCount)
        {
            try
            {
                await Task.Delay(20, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                int actualCount = CountMatches(predicate);
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for {minimumCount} matching log message(s). Observed {actualCount}.");
            }
        }
    }

    /// <summary>Counts queued messages satisfying <paramref name="predicate"/>.</summary>
    private int CountMatches(Func<string, bool> predicate) => _messages.Count(predicate);

    /// <summary>Enqueues a formatted log line.</summary>
    private void Add(string message) => _messages.Enqueue(message);

    /// <summary>Logger that forwards every enabled level into the parent provider queue.</summary>
    private sealed class RecordingLogger(string categoryName, RecordingLoggerProvider provider) : ILogger
    {
        /// <inheritdoc />
        /// <typeparam name="TState">Type of the scope state; ignored because scopes are not recorded.</typeparam>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        /// <typeparam name="TState">Type of the log state passed to the formatter.</typeparam>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            provider.Add($"{categoryName}|{logLevel}|{formatter(state, exception)}");
        }
    }
}
