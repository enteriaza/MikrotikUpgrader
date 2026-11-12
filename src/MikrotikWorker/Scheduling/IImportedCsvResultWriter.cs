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
    Task RecordUpgradeStartedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <see cref="ImportedCsvResultColumns.UpgradeCompleted"/> and
    /// <see cref="ImportedCsvResultColumns.Result"/> for the job's CSV row.
    /// Leaves <see cref="ImportedCsvResultColumns.UpgradeStarted"/> unchanged when already set.
    /// </summary>
    Task RecordJobCompletedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset completedAt,
        ScheduledJobTerminalState state,
        string? detail,
        CancellationToken cancellationToken = default);
}

internal sealed class NullImportedCsvResultWriter : IImportedCsvResultWriter
{
    public static NullImportedCsvResultWriter Instance { get; } = new();

    public Task RecordUpgradeStartedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RecordJobCompletedAsync(
        MikrotikScheduledJob job,
        DateTimeOffset completedAt,
        ScheduledJobTerminalState state,
        string? detail,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
