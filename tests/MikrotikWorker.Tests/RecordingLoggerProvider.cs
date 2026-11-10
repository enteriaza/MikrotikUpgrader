using System.Collections.Concurrent;

namespace MikrotikWorker.Tests;

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

    public void Dispose()
    {
    }

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

    private int CountMatches(Func<string, bool> predicate) => _messages.Count(predicate);

    private void Add(string message) => _messages.Enqueue(message);

    private sealed class RecordingLogger(string categoryName, RecordingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

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
