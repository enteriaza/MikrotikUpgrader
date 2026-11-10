using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

internal sealed class SerilogOptionsValidator : IValidateOptions<SerilogOptions>
{
    private static readonly HashSet<string> AllowedIntervals = new(StringComparer.OrdinalIgnoreCase)
    {
        "Infinite",
        "Year",
        "Month",
        "Day",
        "Hour",
        "Minute",
    };

    private static readonly HashSet<string> AllowedLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Verbose",
        "Debug",
        "Information",
        "Warning",
        "Error",
        "Fatal",
    };

    public ValidateOptionsResult Validate(string? name, SerilogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.LogDirectory))
        {
            errors.Add("Logging:Serilog:LogDirectory must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.FileName))
        {
            errors.Add("Logging:Serilog:FileName must not be empty.");
        }
        else if (options.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errors.Add("Logging:Serilog:FileName contains invalid file name characters.");
        }

        if (!AllowedIntervals.Contains(options.RollingInterval))
        {
            errors.Add(
                "Logging:Serilog:RollingInterval must be one of: Infinite, Year, Month, Day, Hour, Minute.");
        }

        if (!AllowedLevels.Contains(options.MinimumLevel))
        {
            errors.Add(
                "Logging:Serilog:MinimumLevel must be one of: Verbose, Debug, Information, Warning, Error, Fatal.");
        }

        if (options.RetainedFileCountLimit < 1)
        {
            errors.Add("Logging:Serilog:RetainedFileCountLimit must be at least 1.");
        }

        if (options.FileSizeLimitBytes < 1)
        {
            errors.Add("Logging:Serilog:FileSizeLimitBytes must be at least 1.");
        }

        if (options.AsyncBufferCapacity < 1)
        {
            errors.Add("Logging:Serilog:AsyncBufferCapacity must be at least 1.");
        }

        if (options.DeviceLogAsyncCapacity < 1)
        {
            errors.Add("Logging:Serilog:DeviceLogAsyncCapacity must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(options.DeviceLogDirectory))
        {
            errors.Add("Logging:Serilog:DeviceLogDirectory must not be empty.");
        }

        try
        {
            _ = options.GetAbsoluteLogDirectory();
        }
        catch (Exception ex)
        {
            errors.Add($"Logging:Serilog:LogDirectory is invalid: {ex.Message}");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
