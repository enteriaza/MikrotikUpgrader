namespace MikrotikWorker.Configuration;

/// <summary>
/// Filesystem-based scheduled job ingestion settings.
/// </summary>
public sealed class ScheduledJobsOptions
{
    public const string SectionName = "ScheduledJobs";

    public string Path { get; set; } = "ScheduledJobs";

    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan FileReadyCheckInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan FileReadyTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public long MaximumFileSizeBytes { get; set; } = 52_428_800;

    public int MaximumRecords { get; set; } = 500_000;

    /// <summary>
    /// Maximum number of jobs held by the in-memory scheduler at once.
    /// </summary>
    public int MaximumPendingJobs { get; set; } = 10_000;

    /// <summary>
    /// Maximum number of CSV files ingested concurrently.
    /// </summary>
    public int MaxConcurrentCsvImports { get; set; } = 2;

    /// <summary>
    /// Maximum number of detailed validation errors retained in memory and written to reports.
    /// </summary>
    public int MaxValidationErrorsRetained { get; set; } = 100;

    /// <summary>
    /// Maximum number of pending CSV discovery paths buffered for ingestion workers.
    /// </summary>
    public int DiscoveryQueueCapacity { get; set; } = 1_024;

    /// <summary>
    /// Maximum number of concurrent RouterOS connect/disconnect operations.
    /// Default and minimum is 12. Increase only after considering CPU, memory, sockets, and latency.
    /// </summary>
    public int MaxConcurrentRouterJobs { get; set; } = 12;
}
