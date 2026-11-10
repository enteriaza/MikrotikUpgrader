using System.Text;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

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
    private readonly ConcurrentProcessingGate _gate = new();

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
                processingPath is not null && File.Exists(processingPath) ? "Processing" : "incoming/unknown");
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

        try
        {
            int queued = 0;
            await foreach (MikrotikScheduledJob job in parser.EnumerateJobsAsync(processingPath, importId, cancellationToken)
                               .ConfigureAwait(false))
            {
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
            }

            if (queued != validation.RecordCount)
            {
                throw new InvalidOperationException(
                    $"Scheduled {queued} jobs but validation counted {validation.RecordCount} records for {fileName}.");
            }

            // Bind the intended Accepted path before the filesystem move so shutdown/exceptions
            // cannot leave an Accepted CSV without an in-memory batch. Completion stays blocked
            // until SetAcceptedPath confirms the on-disk path after the move.
            string intendedAcceptedPath = Path.Combine(paths.Accepted, fileName);
            scheduler.BindAcceptedPathIntent(importId, intendedAcceptedPath);

            acceptedPath = fileMover.MoveSafely(processingPath, paths.Accepted, fileName, logger);
            scheduler.SetAcceptedPath(importId, acceptedPath);

            logger.LogInformation(
                "CSV moved to Accepted: {FileName}. ImportId={ImportId}, JobsScheduled={JobsScheduled}, MaximumPendingJobs={MaximumPendingJobs}",
                fileName,
                importId,
                queued,
                options.Value.MaximumPendingJobs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await HandleIngestFailureAsync(importId, acceptedPath, abortedByCancellation: true).ConfigureAwait(false);
            scheduledRows.Clear();
            throw;
        }
        catch (Exception)
        {
            await HandleIngestFailureAsync(importId, acceptedPath, abortedByCancellation: false).ConfigureAwait(false);
            scheduledRows.Clear();
            throw;
        }
        finally
        {
            scheduledRows.Clear();
        }
    }

    private async Task HandleIngestFailureAsync(Guid importId, string? acceptedPath, bool abortedByCancellation)
    {
        // Only treat the CSV as Accepted when the move actually produced a path that exists.
        // An intent-only bind (pre-move) must still Abort so we do not keep a phantom Accepted path.
        if (acceptedPath is null || !File.Exists(acceptedPath))
        {
            await scheduler.AbortImportAsync(importId, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // File is already in Accepted: confirm the path so terminal jobs can complete the batch.
        // Do not abort — that would drop tracking while leaving the CSV in Accepted.
        try
        {
            if (scheduler.IsTracked(importId))
            {
                scheduler.SetAcceptedPath(importId, acceptedPath);
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
                "Failed to bind accepted path after movement. ImportId={ImportId}, Path={Path}, Cancelled={Cancelled}",
                importId,
                acceptedPath,
                abortedByCancellation);
        }
    }

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

internal sealed class ConcurrentProcessingGate
{
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public bool TryBegin(string fileName)
    {
        lock (_gate)
        {
            return _inFlight.Add(fileName);
        }
    }

    public void End(string fileName)
    {
        lock (_gate)
        {
            _inFlight.Remove(fileName);
        }
    }
}
