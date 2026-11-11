using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Tests;

public sealed class FirmwareUpgradeOptionsValidatorTests
{
    [Fact]
    public void DefaultRebootTimeout_IsExactlyFiveMinutes()
    {
        FirmwareUpgradeOptions options = new();
        Assert.Equal(TimeSpan.FromMinutes(5), options.RebootTimeout);
    }

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

    [Fact]
    public void Validator_AcceptsEqualRebootAndPostUpgradeVerifyTimeouts()
    {
        FirmwareUpgradeOptions options = ValidDisabledOptions();
        options.RebootTimeout = TimeSpan.FromMinutes(5);
        options.PostUpgradeVerifyTimeout = TimeSpan.FromMinutes(5);

        ValidateOptionsResult result = new FirmwareUpgradeOptionsValidator().Validate(null, options);

        Assert.False(result.Failed);
    }

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
