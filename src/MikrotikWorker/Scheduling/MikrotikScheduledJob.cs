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
    /// Immutable per-job stage cap captured at CSV import. Defaults to <see cref="UpgradeExecutionMode.FullUpgrade"/>
    /// when the optional <c>executionmode</c> column is absent.
    /// </summary>
    public UpgradeExecutionMode ExecutionMode { get; init; } = UpgradeExecutionMode.FullUpgrade;

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

    Expired = 2,

    Succeeded = 3,

    Failed = 4,

    Cancelled = 5,
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
