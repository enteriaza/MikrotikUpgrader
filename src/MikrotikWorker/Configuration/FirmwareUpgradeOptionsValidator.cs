using System.Net;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;

namespace MikrotikWorker.Configuration;

internal sealed class FirmwareUpgradeOptionsValidator : IValidateOptions<FirmwareUpgradeOptions>
{
    public ValidateOptionsResult Validate(string? name, FirmwareUpgradeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];

        if (!RouterOsVersion.TryParse(options.TargetRouterOsVersion, out RouterOsVersion target)
            || target.IsV7OrLater
            || !target.IsV6)
        {
            errors.Add("FirmwareUpgrade:TargetRouterOsVersion must be a RouterOS v6 release.");
        }

        if (options.FetchTimeout <= TimeSpan.Zero)
        {
            errors.Add("FirmwareUpgrade:FetchTimeout must be greater than zero.");
        }

        if (options.ReconnectInterval <= TimeSpan.Zero)
        {
            errors.Add("FirmwareUpgrade:ReconnectInterval must be greater than zero.");
        }

        if (options.RebootTimeout <= TimeSpan.Zero)
        {
            errors.Add("FirmwareUpgrade:RebootTimeout must be greater than zero.");
        }

        if (options.PostUpgradeVerifyTimeout <= TimeSpan.Zero)
        {
            errors.Add("FirmwareUpgrade:PostUpgradeVerifyTimeout must be greater than zero.");
        }

        if (options.BackupFileReadyTimeout <= TimeSpan.Zero)
        {
            errors.Add("FirmwareUpgrade:BackupFileReadyTimeout must be greater than zero.");
        }

        if (options.ScpPort is < 1 or > 65535)
        {
            errors.Add("FirmwareUpgrade:ScpPort must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.BackupDirectory))
        {
            errors.Add("FirmwareUpgrade:BackupDirectory must be configured.");
        }

        if (options.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.AdvertisedBaseUrl))
            {
                errors.Add("FirmwareUpgrade:AdvertisedBaseUrl is required when FirmwareUpgrade:Enabled is true.");
            }
            else if (!Uri.TryCreate(options.AdvertisedBaseUrl, UriKind.Absolute, out Uri? uri)
                     || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("FirmwareUpgrade:AdvertisedBaseUrl must be an absolute http:// URL (HTTPS is not served by the firmware endpoint).");
            }
            else if (IsLoopbackHost(uri.Host))
            {
                errors.Add("FirmwareUpgrade:AdvertisedBaseUrl must be reachable from routers; localhost/loopback addresses are not allowed.");
            }
            else if (!uri.AbsolutePath.TrimEnd('/').EndsWith(
                         FirmwareFilesOptions.RequestPath,
                         StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"FirmwareUpgrade:AdvertisedBaseUrl path must end with '{FirmwareFilesOptions.RequestPath}'.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
}
