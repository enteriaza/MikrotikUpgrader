// -----------------------------------------------------------------------------
// File: JobRetryOptionsValidatorTests.cs
//
// Description:
// Unit tests for JobRetryOptionsValidator: positive delays and non-negative
// MaximumUnsuccessfulAttempts (including zero-disables-retry).
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Tests.Configuration;

/// <summary>
/// Validates JobRetryOptionsValidator accept/reject rules.
/// </summary>
public sealed class JobRetryOptionsValidatorTests
{
    /// <summary>
    /// Fresh JobRetryOptions.
    /// Expects 10-minute connection/auth delays, 10 max attempts, and validator success.
    /// </summary>
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

    /// <summary>
    /// ConnectionRetryDelay of 0 or negative minutes.
    /// Expects validation failure citing ConnectionRetryDelay.
    /// </summary>
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

    /// <summary>
    /// MaximumUnsuccessfulAttempts=-1.
    /// Expects validation failure citing MaximumUnsuccessfulAttempts.
    /// </summary>
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

    /// <summary>
    /// MaximumUnsuccessfulAttempts=0.
    /// Expects validator success — zero means terminal on first failure.
    /// </summary>
    [Fact]
    public void ZeroMaximumAttempts_IsAllowed_AndDisablesRetry()
    {
        JobRetryOptions options = new() { MaximumUnsuccessfulAttempts = 0 };
        ValidateOptionsResult result = new JobRetryOptionsValidator().Validate(null, options);
        Assert.False(result.Failed);
    }
}
