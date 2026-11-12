using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Tests.Configuration;

public sealed class JobRetryOptionsValidatorTests
{
    [Fact]
    public void Defaults_AreTenMinutesAndTenAttempts()
    {
        JobRetryOptions options = new();
        Assert.Equal(TimeSpan.FromMinutes(10), options.ConnectionRetryDelay);
        Assert.Equal(TimeSpan.FromMinutes(10), options.AuthenticationRetryDelay);
        Assert.Equal(10, options.MaximumUnsuccessfulAttempts);

        ValidateOptionsResult result = new JobRetryOptionsValidator().Validate(null, options);
        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveDelays(int minutes)
    {
        JobRetryOptions options = new()
        {
            ConnectionRetryDelay = TimeSpan.FromMinutes(minutes),
            AuthenticationRetryDelay = TimeSpan.FromMinutes(10),
            MaximumUnsuccessfulAttempts = 10,
        };

        ValidateOptionsResult result = new JobRetryOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            static f => f.Contains("ConnectionRetryDelay", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsNegativeMaximumAttempts()
    {
        JobRetryOptions options = new() { MaximumUnsuccessfulAttempts = -1 };
        ValidateOptionsResult result = new JobRetryOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            static f => f.Contains("MaximumUnsuccessfulAttempts", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroMaximumAttempts_IsAllowed_AndDisablesRetry()
    {
        JobRetryOptions options = new() { MaximumUnsuccessfulAttempts = 0 };
        ValidateOptionsResult result = new JobRetryOptionsValidator().Validate(null, options);
        Assert.False(result.Failed);
    }
}
