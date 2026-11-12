// -----------------------------------------------------------------------------
// File: LivePackageCompatibilityAuditOptionsValidator.cs
//
// Description:
// IValidateOptions for LivePackageCompatibilityAuditOptions: concurrent
// connection bounds (1–64) and positive optional timeouts when set.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

/// <summary>
/// Validates <see cref="LivePackageCompatibilityAuditOptions"/> at options bind / host start.
/// </summary>
internal sealed class LivePackageCompatibilityAuditOptionsValidator
    : IValidateOptions<LivePackageCompatibilityAuditOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, LivePackageCompatibilityAuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> errors = [];

        if (options.MaxConcurrentConnections is < 1 or > 64)
        {
            errors.Add("LivePackageCompatibilityAudit:MaxConcurrentConnections must be between 1 and 64.");
        }

        if (options.ConnectionTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            errors.Add("LivePackageCompatibilityAudit:ConnectionTimeout must be greater than zero when set.");
        }

        if (options.TotalAuditTimeout is { } total && total <= TimeSpan.Zero)
        {
            errors.Add("LivePackageCompatibilityAudit:TotalAuditTimeout must be greater than zero when set.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
