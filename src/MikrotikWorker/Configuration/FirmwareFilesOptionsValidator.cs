// -----------------------------------------------------------------------------
// File: FirmwareFilesOptionsValidator.cs
//
// Description:
// IValidateOptions for FirmwareFilesOptions: non-empty Path that resolves to an
// existing directory on disk at validation time.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

/// <summary>
/// Validates <see cref="FirmwareFilesOptions"/> at options bind / host start.
/// </summary>
internal sealed class FirmwareFilesOptionsValidator : IValidateOptions<FirmwareFilesOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, FirmwareFilesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Path))
        {
            return ValidateOptionsResult.Fail("FirmwareFiles:Path must be configured.");
        }

        string absolutePath = options.GetAbsolutePath();
        if (!Directory.Exists(absolutePath))
        {
            return ValidateOptionsResult.Fail($"Firmware directory does not exist: {absolutePath}");
        }

        return ValidateOptionsResult.Success;
    }
}
