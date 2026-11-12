// -----------------------------------------------------------------------------
// File: RouterJobExecutionQueue.cs
//
// Description:
// Bounded channel handing eligible jobs from the in-memory scheduler to the
// fixed-concurrency router execution workers. Capacity matches
// MaximumPendingJobs so dispatch cannot create a second unbounded backlog.
//
// -----------------------------------------------------------------------------

using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Bounded handoff from the scheduler to router execution workers.
/// Capacity matches <see cref="ScheduledJobsOptions.MaximumPendingJobs"/> so dispatched
/// jobs stay within the existing in-memory capacity model (no second unbounded queue).
/// </summary>
internal sealed class RouterJobExecutionQueue
{
    /// <summary>
    /// Bounded channel that holds router jobs waiting for a worker.
    /// </summary>
    private readonly Channel<RouterJobWorkItem> _channel;

    /// <summary>
    /// Creates a multi-reader/multi-writer bounded channel with Wait full-mode semantics.
    /// </summary>
    /// <param name="options">Supplies <see cref="ScheduledJobsOptions.MaximumPendingJobs"/> (clamped to at least 1).</param>
    public RouterJobExecutionQueue(IOptions<ScheduledJobsOptions> options)
    {
        int capacity = Math.Max(1, options.Value.MaximumPendingJobs);
        _channel = Channel.CreateBounded<RouterJobWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Reader consumed by <see cref="RouterJobExecutionService"/> workers.</summary>
    public ChannelReader<RouterJobWorkItem> Reader => _channel.Reader;

    /// <summary>Writer surface (prefer <see cref="TryWrite"/> from the scheduler).</summary>
    public ChannelWriter<RouterJobWorkItem> Writer => _channel.Writer;

    /// <summary>
    /// Attempts a non-blocking enqueue. Returns <c>false</c> when at capacity so the scheduler can requeue.
    /// </summary>
    /// <param name="item">Dispatched work item.</param>
    /// <returns><c>true</c> when the item was accepted into the channel.</returns>
    public bool TryWrite(RouterJobWorkItem item) => _channel.Writer.TryWrite(item);

    /// <summary>
    /// Completes the writer so workers drain and exit (shutdown / scheduler stop).
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();
}
