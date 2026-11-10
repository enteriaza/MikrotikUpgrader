using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;

namespace MikrotikWorker.Configuration;

internal sealed class FirmwareAssessmentOptionsValidator : IValidateOptions<FirmwareAssessmentOptions>
{
    public ValidateOptionsResult Validate(string? name, FirmwareAssessmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.TargetRouterOsVersion))
        {
            return ValidateOptionsResult.Fail("FirmwareAssessment:TargetRouterOsVersion must be configured.");
        }

        if (!RouterOsVersion.TryParse(options.TargetRouterOsVersion, out RouterOsVersion target))
        {
            return ValidateOptionsResult.Fail(
                $"FirmwareAssessment:TargetRouterOsVersion '{options.TargetRouterOsVersion}' is not a valid RouterOS version.");
        }

        if (target.IsV7OrLater || !target.IsV6)
        {
            return ValidateOptionsResult.Fail(
                "FirmwareAssessment:TargetRouterOsVersion must be a RouterOS v6 release. Automatic v6-to-v7 migration is not supported.");
        }

        return ValidateOptionsResult.Success;
    }
}
