using System.Net;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// One router job derived from a scheduled CSV row.
/// </summary>
public sealed class MikrotikScheduledJob
{
    public required Guid ImportId { get; init; }

    public required string SourceFileName { get; init; }

    public required int RowNumber { get; init; }

    public required string RouterName { get; init; }

    public required IPAddress RouterIp { get; init; }

    public required DateTime DateStartUtc { get; init; }

    public required DateTime DateStopUtc { get; init; }

    /// <summary>
    /// Stable identifier for structured logging (import + row).
    /// </summary>
    public string JobId => $"{ImportId:N}:{RowNumber}";
}

/// <summary>
/// Lifecycle outcomes for in-memory scheduled jobs.
/// </summary>
public enum ScheduledJobTerminalState
{
    /// <summary>
    /// Legacy alias: job was observed inside the window before execution existed.
    /// New code uses <see cref="Succeeded"/> after connect/disconnect.
    /// </summary>
    ObservedEligible = 1,

    /// <summary>The job's scheduled window elapsed before execution started.</summary>
    Expired = 2,

    /// <summary>
    /// The device was verified on the configured RouterOS 7 target (or the full workflow,
    /// including any applicable OSPF migration and RouterBOARD firmware step, completed).
    /// </summary>
    Succeeded = 3,

    /// <summary>The workflow failed (including administratively disabled upgrades).</summary>
    Failed = 4,

    /// <summary>The job was cancelled or interrupted (shutdown, import abort).</summary>
    Cancelled = 5,

    /// <summary>
    /// The device is already on the configured RouterOS 7 target and was verified as such;
    /// no upgrade was necessary. Success-like terminal state.
    /// </summary>
    AlreadyUpToDate = 6,

    /// <summary>
    /// A prerequisite or safety policy prevented the upgrade (for example identity policy,
    /// or the device is newer than the target and downgrades are never performed).
    /// The device was not upgraded; this is not a success.
    /// </summary>
    Blocked = 7,

    /// <summary>
    /// Reserved for documented non-upgrade cases. Never used for administratively disabled upgrades.
    /// </summary>
    Skipped = 8,
}

/// <summary>
/// Work item handed from the scheduler to the router execution pool.
/// </summary>
internal sealed class RouterJobWorkItem(MikrotikScheduledJob job, object entryToken)
{
    public MikrotikScheduledJob Job { get; } = job;

    /// <summary>
    /// Opaque scheduler entry token used for terminal / requeue callbacks.
    /// </summary>
    public object EntryToken { get; } = entryToken;
}
