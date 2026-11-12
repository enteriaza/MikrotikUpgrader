using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

internal sealed class FirmwareFilesOptionsValidator : IValidateOptions<FirmwareFilesOptions>
{
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
