// -----------------------------------------------------------------------------
// File: CsvOptionsValidator.cs
//
// Description:
// IValidateOptions for CsvOptions: resolvable CultureInfo and TimeZoneInfo, and
// a non-empty DateTimeFormats array without blank entries.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

/// <summary>
/// Validates <see cref="CsvOptions"/> at options bind / host start.
/// </summary>
internal sealed class CsvOptionsValidator : IValidateOptions<CsvOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CsvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.Culture))
        {
            errors.Add("Csv:Culture must be configured.");
        }
        else
        {
            try
            {
                _ = CultureInfo.GetCultureInfo(options.Culture);
            }
            catch (CultureNotFoundException)
            {
                errors.Add($"Csv:Culture '{options.Culture}' is not a valid culture.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.TimeZoneId))
        {
            errors.Add("Csv:TimeZoneId must be configured.");
        }
        else
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                errors.Add($"Csv:TimeZoneId '{options.TimeZoneId}' was not found on this system.");
            }
            catch (InvalidTimeZoneException)
            {
                errors.Add($"Csv:TimeZoneId '{options.TimeZoneId}' is invalid.");
            }
        }

        if (options.DateTimeFormats is null || options.DateTimeFormats.Length == 0)
        {
            errors.Add("Csv:DateTimeFormats must contain at least one format.");
        }
        else if (options.DateTimeFormats.Any(static format => string.IsNullOrWhiteSpace(format)))
        {
            errors.Add("Csv:DateTimeFormats must not contain empty entries.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
