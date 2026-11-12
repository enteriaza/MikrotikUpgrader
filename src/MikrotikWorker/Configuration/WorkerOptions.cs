// -----------------------------------------------------------------------------
// File: WorkerOptions.cs
//
// Description:
// Options bound from the "Worker" section for the background hosted Worker loop.
// Currently only controls the heartbeat delay between idle iterations.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Configuration;

/// <summary>
/// Configuration for the background worker loop.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Configuration section name: <c>Worker</c>.</summary>
    public const string SectionName = "Worker";

    /// <summary>
    /// Delay between worker heartbeat iterations.
    /// Default: 5 seconds. Must be greater than zero (validated at host start).
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
}
