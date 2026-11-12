// -----------------------------------------------------------------------------
// File: ScheduledJobsOptions.cs
//
// Description:
// Options bound from the "ScheduledJobs" section for CSV drop-folder ingestion,
// readiness polling, import concurrency, in-memory job capacity, and the
// concurrent RouterOS worker pool size.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Configuration;

/// <summary>
/// Filesystem-based scheduled job ingestion settings.
/// </summary>
public sealed class ScheduledJobsOptions
{
    /// <summary>Configuration section name: <c>ScheduledJobs</c>.</summary>
    public const string SectionName = "ScheduledJobs";

    /// <summary>
    /// Root folder for Incoming/Accepted/Rejected/Processing CSV layout.
    /// Absolute, or relative to the application base directory. Default: <c>ScheduledJobs</c>.
    /// </summary>
    public string Path { get; set; } = "ScheduledJobs";

    /// <summary>
    /// Interval between Incoming folder discovery scans.
    /// Default: 1 minute.
    /// </summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Poll interval while waiting for a discovered CSV to stop growing (stable size).
    /// Default: 2 seconds.
    /// </summary>
    public TimeSpan FileReadyCheckInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Maximum time to wait for a CSV to become ready before rejecting it.
    /// Default: 2 minutes.
    /// </summary>
    public TimeSpan FileReadyTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Maximum accepted CSV file size in bytes.
    /// Default: 52_428_800 (50 MiB).
    /// </summary>
    public long MaximumFileSizeBytes { get; set; } = 52_428_800;

    /// <summary>
    /// Maximum number of data rows accepted from a single CSV import.
    /// Default: 500_000.
    /// </summary>
    public int MaximumRecords { get; set; } = 500_000;

    /// <summary>
    /// Maximum number of jobs held by the in-memory scheduler at once.
    /// Default: 10_000.
    /// </summary>
    public int MaximumPendingJobs { get; set; } = 10_000;

    /// <summary>
    /// Maximum number of CSV files ingested concurrently.
    /// Default: 2.
    /// </summary>
    public int MaxConcurrentCsvImports { get; set; } = 2;

    /// <summary>
    /// Maximum number of detailed validation errors retained in memory and written to reports.
    /// Default: 100.
    /// </summary>
    public int MaxValidationErrorsRetained { get; set; } = 100;

    /// <summary>
    /// Maximum number of pending CSV discovery paths buffered for ingestion workers.
    /// Default: 1_024.
    /// </summary>
    public int DiscoveryQueueCapacity { get; set; } = 1_024;

    /// <summary>
    /// Maximum number of concurrent RouterOS connect/disconnect operations.
    /// Default and minimum is 12. Increase only after considering CPU, memory, sockets, and latency.
    /// </summary>
    public int MaxConcurrentRouterJobs { get; set; } = 12;
}
