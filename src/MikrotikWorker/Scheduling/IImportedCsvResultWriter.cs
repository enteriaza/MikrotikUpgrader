// -----------------------------------------------------------------------------
// File: IImportedCsvResultWriter.cs
//
// Description:
// Contract for persisting per-router upgrade start/completion into the imported
// Accepted CSV without affecting in-memory job outcomes. Also provides a no-op
// implementation used when CSV result writing is not wired into the scheduler.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Persists per-router upgrade start/completion into the original imported Accepted CSV.
/// Failures are logged and must not alter in-memory job outcomes.
/// </summary>
internal interface IImportedCsvResultWriter
{
    /// <summary>
    /// Writes <see cref="ImportedCsvResultColumns.UpgradeStarted"/> for the job's CSV row.
    /// No-op when the Accepted CSV path is not yet bound on disk.
    /// </summary>
    /// <param name="job">Job whose CSV row should receive the start timestamp.</param>
    /// <param name="startedAt">UTC (or offset-aware) instant when the upgrade/workflow started.</param>
    /// <param name="cancellationToken">Propagates cancellation into the file update gate and I/O.</param>
    Task RecordUpgradeStartedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <see cref="ImportedCsvResultColumns.UpgradeCompleted"/> and
    /// <see cref="ImportedCsvResultColumns.Result"/> for the job's CSV row.
    /// Leaves <see cref="ImportedCsvResultColumns.UpgradeStarted"/> unchanged when already set.
    /// </summary>
    /// <param name="job">Job whose CSV row should receive completion columns.</param>
    /// <param name="completedAt">Instant when the job reached a terminal state.</param>
    /// <param name="state">Terminal outcome used to format the Result column.</param>
    /// <param name="detail">Optional failure/detail text; may be null for success outcomes.</param>
    /// <param name="cancellationToken">Propagates cancellation into the file update gate and I/O.</param>
    Task RecordJobCompletedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset completedAt,
        ScheduledJobTerminalState state,
        string? detail,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op <see cref="IImportedCsvResultWriter"/> used when CSV persistence is optional or unset.
/// </summary>
internal sealed class NullImportedCsvResultWriter : IImportedCsvResultWriter
{
    /// <summary>Shared singleton instance.</summary>
    public static NullImportedCsvResultWriter Instance { get; } = new();

    /// <inheritdoc />
    public Task RecordUpgradeStartedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task RecordJobCompletedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset completedAt,
        ScheduledJobTerminalState state,
        string? detail,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
