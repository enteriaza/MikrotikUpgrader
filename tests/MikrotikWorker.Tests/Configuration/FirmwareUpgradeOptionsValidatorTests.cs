// -----------------------------------------------------------------------------
// File: FirmwareUpgradeOptionsValidatorTests.cs
//
// Description:
// Unit tests for FirmwareUpgradeOptionsValidator covering timeouts, SCP port,
// backup directory, and AdvertisedBaseUrl rules when FirmwareUpgrade:Enabled is true.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Tests.Configuration;
/// <summary>
/// Validates FirmwareUpgradeOptionsValidator rules and Enabled-gated URL checks.
/// </summary>
public sealed class FirmwareUpgradeOptionsValidatorTests
{
    /// <summary>
    /// Fresh FirmwareUpgradeOptions.
    /// Expects RebootTimeout exactly five minutes.
    /// </summary>
    [Fact]
    public void DefaultRebootTimeout_IsExactlyFiveMinutes()
    {
        FirmwareUpgradeOptions options = new();
        Assert.Equal(TimeSpan.FromMinutes(5), options.RebootTimeout);
    }

    /// <summary>
    /// RebootTimeout of 0 or negative seconds on otherwise valid disabled options.
    /// Expects failure citing RebootTimeout.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60)]
    public void Validator_RejectsNonPositiveRebootTimeout(int seconds)
    {
        FirmwareUpgradeOptions options = ValidDisabledOptions();
        options.RebootTimeout = TimeSpan.FromSeconds(seconds);

        ValidateOptionsResult result = new FirmwareUpgradeOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            static f => f.Contains("RebootTimeout", StringComparison.Ordinal));
    }

    /// <summary>
    /// PostUpgradeVerifyTimeout longer than RebootTimeout.
    /// Expects failure mentioning both timeout properties.
    /// </summary>
    [Fact]
    public void Validator_RejectsPostUpgradeVerifyTimeoutExceedingRebootTimeout()
    {
        FirmwareUpgradeOptions options = ValidDisabledOptions();
        options.RebootTimeout = TimeSpan.FromMinutes(5);
        options.PostUpgradeVerifyTimeout = TimeSpan.FromMinutes(6);

        ValidateOptionsResult result = new FirmwareUpgradeOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            static f => f.Contains("PostUpgradeVerifyTimeout", StringComparison.Ordinal)
                && f.Contains("RebootTimeout", StringComparison.Ordinal));
    }

    /// <summary>
    /// Equal five-minute reboot and verify timeouts.
    /// Expects validation success.
    /// </summary>
    [Fact]
    public void Validator_AcceptsEqualRebootAndPostUpgradeVerifyTimeouts()
    {
        FirmwareUpgradeOptions options = ValidDisabledOptions();
        options.RebootTimeout = TimeSpan.FromMinutes(5);
        options.PostUpgradeVerifyTimeout = TimeSpan.FromMinutes(5);

        ValidateOptionsResult result = new FirmwareUpgradeOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }

    /// <summary>
    /// Enabled=true with empty AdvertisedBaseUrl.
    /// Expects failure citing AdvertisedBaseUrl.
    /// </summary>
    [Fact]
    public void Validator_WhenEnabled_RejectsMissingAdvertisedBaseUrl()
    {
        FirmwareUpgradeOptions options = ValidDisabledOptions();
        options.Enabled = true;
        options.AdvertisedBaseUrl = "";

        ValidateOptionsResult result = new FirmwareUpgradeOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            static f => f.Contains("AdvertisedBaseUrl", StringComparison.Ordinal));
    }

    /// <summary>
    /// Enabled=true with a non-URI AdvertisedBaseUrl.
    /// Expects failure citing AdvertisedBaseUrl.
    /// </summary>
    [Fact]
    public void Validator_WhenEnabled_RejectsInvalidAdvertisedBaseUrl()
    {
        FirmwareUpgradeOptions options = ValidDisabledOptions();
        options.Enabled = true;
        options.AdvertisedBaseUrl = "not-a-url";

        ValidateOptionsResult result = new FirmwareUpgradeOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            static f => f.Contains("AdvertisedBaseUrl", StringComparison.Ordinal));
    }

    /// <summary>
    /// Enabled=true with a valid http AdvertisedBaseUrl override.
    /// Expects validation success.
    /// </summary>
    [Fact]
    public void Validator_WhenEnabled_AcceptsDeploymentOverrideUrl()
    {
        FirmwareUpgradeOptions options = ValidDisabledOptions();
        options.Enabled = true;
        options.AdvertisedBaseUrl = "http://10.0.0.10:8080/firmware_files";

        ValidateOptionsResult result = new FirmwareUpgradeOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }

    /// <summary>
    /// Reads src/MikrotikWorker/appsettings.json from the repo.
    /// Expects no lab IP 198.18.0.66, empty AdvertisedBaseUrl, RedistributeSummaryviaStatics true,
    /// and Mikrotik Enabled false — prevents shipping lab endpoints.
    /// </summary>
    [Fact]
    public void ShippedAppsettings_DoesNotContainLabFirmwareEndpoint()
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MikrotikWorker",
            "appsettings.json"));
        Assert.True(File.Exists(path), $"Expected shipped settings at {path}");
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("198.18.0.66", json, StringComparison.Ordinal);
        Assert.Contains("\"AdvertisedBaseUrl\": \"\"", json, StringComparison.Ordinal);
        Assert.Contains("\"RedistributeSummaryviaStatics\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"Enabled\": false", json, StringComparison.Ordinal); // Mikrotik
    }

    /// <summary>
    /// Baseline FirmwareUpgradeOptions with Enabled=false and valid positive timeouts/SCP settings.
    /// </summary>
    private static FirmwareUpgradeOptions ValidDisabledOptions() =>
        new()
        {
            Enabled = false,
            FetchTimeout = TimeSpan.FromMinutes(10),
            ReconnectInterval = TimeSpan.FromSeconds(15),
            RebootTimeout = TimeSpan.FromMinutes(5),
            PostUpgradeVerifyTimeout = TimeSpan.FromMinutes(5),
            BackupFileReadyTimeout = TimeSpan.FromMinutes(2),
            BackupFileReadyPollInterval = TimeSpan.FromSeconds(1),
            BackupDirectory = "Backups",
            ScpPort = 22,
            ScpTimeout = TimeSpan.FromMinutes(5),
        };
}
