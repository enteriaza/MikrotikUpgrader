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
    private readonly Channel<RouterJobWorkItem> _channel;

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

    public ChannelReader<RouterJobWorkItem> Reader => _channel.Reader;

    public ChannelWriter<RouterJobWorkItem> Writer => _channel.Writer;

    public bool TryWrite(RouterJobWorkItem item) => _channel.Writer.TryWrite(item);

    public void Complete() => _channel.Writer.TryComplete();
}
