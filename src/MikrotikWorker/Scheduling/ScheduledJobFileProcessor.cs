// -----------------------------------------------------------------------------
// File: ScheduledJobFileProcessor.cs
//
// Description:
// Owns CSV ingestion: readiness wait, Processing move, validation, Accepted move,
// job scheduling, activation finalize, and reject reporting. On cancel/failure
// after Accept it finalizes incomplete activation (never Rejects Accepted files);
// before Accept it aborts the import. Also warns about leftover Processing files.
//
// -----------------------------------------------------------------------------

using System.Text;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Processes one Incoming CSV through the ScheduledJobs lifecycle into the in-memory scheduler.
/// </summary>
/// <param name="paths">Lifecycle folder paths.</param>
/// <param name="parser">Validation and job enumeration.</param>
/// <param name="readinessChecker">Stable-file gate before moving from Incoming.</param>
/// <param name="scheduler">Import registration, scheduling, abort/finalize.</param>
/// <param name="fileMover">Safe inter-folder moves.</param>
/// <param name="options">Limits such as <see cref="ScheduledJobsOptions.MaximumPendingJobs"/> (logged).</param>
/// <param name="timeProvider">Timestamps for reject reports and validation "now".</param>
/// <param name="logger">Ingestion diagnostics.</param>
internal sealed class ScheduledJobFileProcessor(
    ScheduledJobsPaths paths,
    ScheduledJobCsvParser parser,
    CsvFileReadinessChecker readinessChecker,
    IInMemoryJobScheduler scheduler,
    IScheduledJobFileMover fileMover,
    IOptions<ScheduledJobsOptions> options,
    TimeProvider timeProvider,
    ILogger<ScheduledJobFileProcessor> logger)
{
    /// <summary>
    /// Tracks file names being processed so duplicate concurrent requests are skipped.
    /// </summary>
    private readonly ConcurrentProcessingGate _gate = new();

    /// <summary>
    /// Test seam: invoked after Accepted path registration and before job scheduling begins.
    /// </summary>
    internal Func<Guid, string, CancellationToken, Task>? AfterAcceptedPathRegisteredForTestsAsync { get; set; }

    /// <summary>
    /// Test seam: invoked after each successful <see cref="IInMemoryJobScheduler.ScheduleAsync"/> with the
    /// running scheduled count.
    /// </summary>
    internal Func<Guid, int, CancellationToken, Task>? AfterJobScheduledForTestsAsync { get; set; }

    /// <summary>
    /// Ingests a single Incoming <c>.csv</c>: readiness, Processing move, then
    /// <see cref="ProcessProcessingFileAsync"/>. Duplicate concurrent requests for the same file name are skipped.
    /// Non-cancel failures reject the file; cancel leaves it for manual re-import.
    /// </summary>
    /// <param name="filePath">Incoming CSV path.</param>
    /// <param name="cancellationToken">Cancels readiness/validation/scheduling.</param>
    public async Task ProcessIncomingAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        if (!string.Equals(Path.GetExtension(fileName), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_gate.TryBegin(fileName))
        {
            logger.LogDebug("Skipping duplicate processing request for {FileName}", fileName);
            return;
        }

        string? processingPath = null;
        try
        {
            logger.LogInformation("CSV detected: {FileName}", fileName);

            await readinessChecker.WaitUntilReadyAsync(filePath, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("CSV ready for processing: {FileName}", fileName);

            processingPath = fileMover.MoveSafely(filePath, paths.Processing, fileName, logger);
            logger.LogInformation("CSV moved to Processing: {FileName}", fileName);

            await ProcessProcessingFileAsync(processingPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "CSV ingestion cancelled for {FileName}. File left in {Location}. Inspect it, then manually move it back to Incoming to re-import. Partial in-memory state is not reconstructed.",
                fileName,
                DescribeCsvLocation(fileName, processingPath));
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed while processing CSV {FileName}", fileName);
            string rejectSource = processingPath is not null && File.Exists(processingPath)
                ? processingPath
                : filePath;
            if (File.Exists(rejectSource))
            {
                await RejectAsync(rejectSource, fileName, [
                    new CsvValidationError
                    {
                        FileName = fileName,
                        Reason = ex.Message,
                    },
                ], totalErrorCount: 1, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.End(fileName);
        }
    }

    /// <summary>
    /// Validates a Processing CSV, moves it to Accepted, schedules every row, then finalizes activation.
    /// Cancel/failure after Accept keeps the Accepted file and calls incomplete finalize; before Accept aborts.
    /// </summary>
    /// <param name="processingPath">CSV already under Processing.</param>
    /// <param name="cancellationToken">Cancels validation and per-row scheduling.</param>
    public async Task ProcessProcessingFileAsync(string processingPath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(processingPath);
        logger.LogInformation("CSV validation started: {FileName}", fileName);

        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
        CsvParseResult validation = await parser.ValidateAsync(processingPath, utcNow, cancellationToken)
            .ConfigureAwait(false);

        if (!validation.IsAccepted)
        {
            await RejectAsync(
                    processingPath,
                    fileName,
                    validation.Errors,
                    validation.TotalErrorCount,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        logger.LogInformation(
            "CSV accepted for scheduling: {FileName}. Records={RecordCount}, DateStartUtc={DateStartUtc}, DateStopUtc={DateStopUtc}",
            fileName,
            validation.RecordCount,
            validation.DateStartUtc,
            validation.DateStopUtc);

        Guid importId = Guid.NewGuid();
        HashSet<int> scheduledRows = [];
        scheduler.RegisterImport(importId, validation.RecordCount);
        string? acceptedPath = null;
        int queued = 0;

        try
        {
            // Register the actual Accepted path before any job can be dispatched. The scheduler
            // also holds pre-accept schedules until SetAcceptedPath; production ordering still
            // moves and registers first so workers never race the path store.
            string intendedAcceptedPath = Path.Combine(paths.Accepted, fileName);
            scheduler.BindAcceptedPathIntent(importId, intendedAcceptedPath);

            acceptedPath = fileMover.MoveSafely(processingPath, paths.Accepted, fileName, logger);
            scheduler.SetAcceptedPath(importId, acceptedPath);

            if (AfterAcceptedPathRegisteredForTestsAsync is not null)
            {
                await AfterAcceptedPathRegisteredForTestsAsync(importId, acceptedPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            await foreach (MikrotikScheduledJob job in parser.EnumerateJobsAsync(acceptedPath, importId, cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!scheduledRows.Add(job.RowNumber))
                {
                    logger.LogDebug(
                        "Skipping duplicate row while scheduling {FileName} row {RowNumber}",
                        fileName,
                        job.RowNumber);
                    continue;
                }

                await scheduler.ScheduleAsync(job, cancellationToken).ConfigureAwait(false);
                queued++;
                logger.LogDebug(
                    "Job scheduled from {FileName}: ImportId={ImportId}, RowNumber={RowNumber}, RouterName={RouterName}, RouterIp={RouterIp}",
                    fileName,
                    job.ImportId,
                    job.RowNumber,
                    job.RouterName,
                    job.RouterIp);

                if (AfterJobScheduledForTestsAsync is not null)
                {
                    await AfterJobScheduledForTestsAsync(importId, queued, cancellationToken).ConfigureAwait(false);
                }
            }

            if (queued != validation.RecordCount)
            {
                throw new InvalidOperationException(
                    $"Scheduled {queued} jobs but validation counted {validation.RecordCount} records for {fileName}.");
            }

            scheduler.FinalizeImportActivation(importId, queued, activationIncomplete: false);

            logger.LogInformation(
                "CSV moved to Accepted: {FileName}. ImportId={ImportId}, JobsScheduled={JobsScheduled}, MaximumPendingJobs={MaximumPendingJobs}",
                fileName,
                importId,
                queued,
                options.Value.MaximumPendingJobs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await HandleIngestFailureAsync(
                    importId,
                    acceptedPath,
                    queued,
                    abortedByCancellation: true,
                    validation.RecordCount)
                .ConfigureAwait(false);
            scheduledRows.Clear();
            throw;
        }
        catch (Exception)
        {
            await HandleIngestFailureAsync(
                    importId,
                    acceptedPath,
                    queued,
                    abortedByCancellation: false,
                    validation.RecordCount)
                .ConfigureAwait(false);
            scheduledRows.Clear();
            throw;
        }
        finally
        {
            scheduledRows.Clear();
        }
    }

    /// <summary>
    /// Shared cancel/exception path after RegisterImport: Abort when Accepted is missing;
    /// otherwise re-bind Accepted path and <see cref="IInMemoryJobScheduler.FinalizeImportActivation"/> with incomplete=true.
    /// Never moves Accepted to Rejected.
    /// </summary>
    private async Task HandleIngestFailureAsync(
        Guid importId,
        string? acceptedPath,
        int scheduledCount,
        bool abortedByCancellation,
        int validatedCount)
    {
        // Only treat the CSV as Accepted when the move actually produced a path that exists.
        // An intent-only bind (pre-move) must still Abort so we do not keep a phantom Accepted path.
        if (acceptedPath is null || !File.Exists(acceptedPath))
        {
            await scheduler.AbortImportAsync(importId, CancellationToken.None).ConfigureAwait(false);
            logger.LogWarning(
                "CSV import activation aborted before Accepted move. ImportId={ImportId}, ValidatedRows={ValidatedRows}, ScheduledRows={ScheduledRows}, Cancelled={Cancelled}",
                importId,
                validatedCount,
                scheduledCount,
                abortedByCancellation);
            return;
        }

        // File is already in Accepted: keep path registration while any scheduled jobs need it,
        // reconcile Remaining for never-scheduled rows, and never Reject/delete the CSV.
        try
        {
            if (scheduler.IsTracked(importId))
            {
                scheduler.SetAcceptedPath(importId, acceptedPath);
                scheduler.FinalizeImportActivation(importId, scheduledCount, activationIncomplete: true);
            }
            else
            {
                logger.LogCritical(
                    "Accepted CSV has no in-memory import batch after failure. ImportId={ImportId}, Path={Path}. Manual re-import may be required after restart.",
                    importId,
                    acceptedPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Failed to finalize accepted import after activation failure. ImportId={ImportId}, Path={Path}, ScheduledRows={ScheduledRows}, ValidatedRows={ValidatedRows}, Cancelled={Cancelled}",
                importId,
                acceptedPath,
                scheduledCount,
                validatedCount,
                abortedByCancellation);
        }
    }

    /// <summary>
    /// Best-effort folder label (Processing/Accepted/Incoming/unknown) for cancel-warning messages.
    /// </summary>
    private string DescribeCsvLocation(string fileName, string? processingPath)
    {
        if (processingPath is not null && File.Exists(processingPath))
        {
            return "Processing";
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (Directory.Exists(paths.Accepted)
            && Directory.EnumerateFiles(paths.Accepted, stem + "*.csv").Any())
        {
            return "Accepted";
        }

        string incoming = Path.Combine(paths.Root, fileName);
        if (File.Exists(incoming))
        {
            return "Incoming";
        }

        return "unknown";
    }

    /// <summary>
    /// Logs a warning for each CSV left in Processing after an interrupted prior run (not auto-imported).
    /// </summary>
    public void WarnAboutInterruptedProcessingFiles()
    {
        foreach (string processingFile in Directory.EnumerateFiles(paths.Processing, "*.csv"))
        {
            string fileName = Path.GetFileName(processingFile);
            logger.LogWarning(
                "Interrupted Processing CSV left in place (not auto-imported): {FileName}. Inspect the file, verify it is complete, then manually move it to the ScheduledJobs Incoming folder to re-import. Partial in-memory job state is not reconstructed.",
                fileName);
        }
    }

    /// <summary>
    /// Writes a <c>.error.txt</c> report under Rejected and moves the CSV there when still present.
    /// </summary>
    private async Task RejectAsync(
        string sourcePath,
        string fileName,
        IReadOnlyList<CsvValidationError> errors,
        int totalErrorCount,
        CancellationToken cancellationToken)
    {
        string reportPath = Path.Combine(paths.Rejected, Path.GetFileNameWithoutExtension(fileName) + ".error.txt");

        StringBuilder report = new();
        report.AppendLine($"Rejected CSV: {fileName}");
        report.AppendLine($"Utc: {timeProvider.GetUtcNow().UtcDateTime:O}");
        report.AppendLine($"TotalErrors: {totalErrorCount}");
        if (totalErrorCount > errors.Count)
        {
            report.AppendLine($"RetainedErrors: {errors.Count}");
            report.AppendLine($"OmittedErrors: {totalErrorCount - errors.Count}");
        }

        foreach (CsvValidationError error in errors)
        {
            CsvValidationError normalized = string.IsNullOrWhiteSpace(error.FileName)
                ? new CsvValidationError
                {
                    FileName = fileName,
                    RowNumber = error.RowNumber,
                    ColumnName = error.ColumnName,
                    Reason = error.Reason,
                }
                : error;
            report.AppendLine(normalized.ToString());
        }

        if (totalErrorCount > errors.Count)
        {
            report.AppendLine($"{totalErrorCount - errors.Count} additional error(s) were omitted.");
        }

        await File.WriteAllTextAsync(reportPath, report.ToString(), cancellationToken).ConfigureAwait(false);

        if (File.Exists(sourcePath))
        {
            fileMover.MoveSafely(sourcePath, paths.Rejected, fileName, logger);
        }

        logger.LogWarning(
            "CSV rejected: {FileName}. TotalErrors={TotalErrorCount}, RetainedErrors={RetainedErrors}. Report={ReportPath}",
            fileName,
            totalErrorCount,
            errors.Count,
            reportPath);
    }
}

/// <summary>
/// Case-insensitive per-file-name gate so the same Incoming CSV is not processed concurrently.
/// </summary>
internal sealed class ConcurrentProcessingGate
{
    /// <summary>
    /// File names currently in flight, compared case-insensitively. Guarded by <c>_gate</c>.
    /// </summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Lock guarding <c>_inFlight</c>.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Attempts to mark <paramref name="fileName"/> as in-flight.
    /// </summary>
    /// <returns><c>false</c> when that name is already being processed.</returns>
    public bool TryBegin(string fileName)
    {
        lock (_gate)
        {
            return _inFlight.Add(fileName);
        }
    }

    /// <summary>Clears the in-flight mark for <paramref name="fileName"/>.</summary>
    public void End(string fileName)
    {
        lock (_gate)
        {
            _inFlight.Remove(fileName);
        }
    }
}
