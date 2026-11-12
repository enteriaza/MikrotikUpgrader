using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

internal sealed class JobRetryOptionsValidator : IValidateOptions<JobRetryOptions>
{
    public ValidateOptionsResult Validate(string? name, JobRetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> errors = [];

        if (options.ConnectionRetryDelay <= TimeSpan.Zero)
        {
            errors.Add("JobRetry:ConnectionRetryDelay must be greater than zero.");
        }

        if (options.AuthenticationRetryDelay <= TimeSpan.Zero)
        {
            errors.Add("JobRetry:AuthenticationRetryDelay must be greater than zero.");
        }

        if (options.MaximumUnsuccessfulAttempts < 0)
        {
            errors.Add("JobRetry:MaximumUnsuccessfulAttempts must be zero or greater.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
