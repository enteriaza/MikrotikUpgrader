using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

internal sealed class MikroTikOptionsValidator : IValidateOptions<MikroTikOptions>
{
    public ValidateOptionsResult Validate(string? name, MikroTikOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];

        if (options.Port is < 1 or > 65535)
        {
            errors.Add($"Mikrotik:Port '{options.Port}' must be between 1 and 65535.");
        }

        if (options.ConnectionTimeout <= TimeSpan.Zero)
        {
            errors.Add("Mikrotik:ConnectionTimeout must be greater than zero.");
        }

        if (options.ReconnectInterval <= TimeSpan.Zero)
        {
            errors.Add("Mikrotik:ReconnectInterval must be greater than zero.");
        }

        if (options.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.Username))
            {
                errors.Add("Mikrotik:Username must be configured when Mikrotik:Enabled is true.");
            }

            if (string.IsNullOrEmpty(options.Password))
            {
                errors.Add("Mikrotik:Password must be configured when Mikrotik:Enabled is true.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
