using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

internal sealed class ScheduledJobsOptionsValidator : IValidateOptions<ScheduledJobsOptions>
{
    public ValidateOptionsResult Validate(string? name, ScheduledJobsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.Path))
        {
            errors.Add("ScheduledJobs:Path must be configured.");
        }

        if (options.ScanInterval <= TimeSpan.Zero)
        {
            errors.Add("ScheduledJobs:ScanInterval must be greater than zero.");
        }

        if (options.FileReadyCheckInterval <= TimeSpan.Zero)
        {
            errors.Add("ScheduledJobs:FileReadyCheckInterval must be greater than zero.");
        }

        if (options.FileReadyTimeout <= TimeSpan.Zero)
        {
            errors.Add("ScheduledJobs:FileReadyTimeout must be greater than zero.");
        }

        if (options.MaximumFileSizeBytes <= 0)
        {
            errors.Add("ScheduledJobs:MaximumFileSizeBytes must be greater than zero.");
        }

        if (options.MaximumRecords <= 0)
        {
            errors.Add("ScheduledJobs:MaximumRecords must be greater than zero.");
        }

        if (options.MaximumPendingJobs <= 0)
        {
            errors.Add("ScheduledJobs:MaximumPendingJobs must be greater than zero.");
        }

        if (options.MaxConcurrentCsvImports <= 0)
        {
            errors.Add("ScheduledJobs:MaxConcurrentCsvImports must be greater than zero.");
        }

        if (options.MaxValidationErrorsRetained <= 0)
        {
            errors.Add("ScheduledJobs:MaxValidationErrorsRetained must be greater than zero.");
        }

        if (options.DiscoveryQueueCapacity <= 0)
        {
            errors.Add("ScheduledJobs:DiscoveryQueueCapacity must be greater than zero.");
        }

        if (options.MaxConcurrentRouterJobs < 12)
        {
            errors.Add("ScheduledJobs:MaxConcurrentRouterJobs must be at least 12.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
