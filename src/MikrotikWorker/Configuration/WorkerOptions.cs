namespace MikrotikWorker.Configuration;

/// <summary>
/// Configuration for the background worker loop.
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Delay between worker heartbeat iterations.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
}
