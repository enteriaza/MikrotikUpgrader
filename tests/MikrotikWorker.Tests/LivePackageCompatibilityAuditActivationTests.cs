using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests;

public sealed class LivePackageCompatibilityAuditActivationTests
{
    [Fact]
    public void LiveAudit_DisabledByDefault()
    {
        Assert.False(new LivePackageCompatibilityAuditOptions().Enabled);
    }

    [Fact]
    public void MissingCliOption_IsNotLiveAuditRequest()
    {
        Assert.False(LivePackageCompatibilityAuditRunner.IsLiveAuditRequested([]));
        Assert.False(LivePackageCompatibilityAuditRunner.IsLiveAuditRequested(
            ["--audit-package-compatibility", "--input", "a.csv"]));
    }

    [Fact]
    public void ExplicitCliOption_IsDetected()
    {
        Assert.True(LivePackageCompatibilityAuditRunner.IsLiveAuditRequested(
            ["--audit-package-compatibility-live", "--input", "routers.csv"]));
    }

    [Fact]
    public async Task RunLiveAudit_WithoutCliOption_FailsBeforeConnect()
    {
        int code = await LivePackageCompatibilityAuditRunner.RunLiveAuditAsync(
            ["--input", "routers.csv"]);
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task RunLiveAudit_WithCliButMissingInput_FailsBeforeConnect()
    {
        int code = await LivePackageCompatibilityAuditRunner.RunLiveAuditAsync(
            ["--audit-package-compatibility-live"]);
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task RunLiveAudit_WithCliButDisabledConfig_FailsBeforeConnect()
    {
        string csv = Path.Combine(Path.GetTempPath(), $"live-audit-disabled-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csv, "routerip\n10.0.0.1\n");
        try
        {
            int code = await LivePackageCompatibilityAuditRunner.RunLiveAuditAsync(
                ["--audit-package-compatibility-live", "--input", csv]);
            Assert.Equal(2, code);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public void InvalidConcurrency_FailsValidation()
    {
        LivePackageCompatibilityAuditOptionsValidator validator = new();
        ValidateOptionsResult result = validator.Validate(
            null,
            new LivePackageCompatibilityAuditOptions { MaxConcurrentConnections = 0 });
        Assert.True(result.Failed);
    }

    [Fact]
    public void InvalidTimeouts_FailValidation()
    {
        LivePackageCompatibilityAuditOptionsValidator validator = new();
        ValidateOptionsResult result = validator.Validate(
            null,
            new LivePackageCompatibilityAuditOptions
            {
                ConnectionTimeout = TimeSpan.Zero,
                TotalAuditTimeout = TimeSpan.FromSeconds(-1),
            });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static f => f.Contains("ConnectionTimeout", StringComparison.Ordinal));
        Assert.Contains(result.Failures, static f => f.Contains("TotalAuditTimeout", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidOptions_PassValidation()
    {
        LivePackageCompatibilityAuditOptionsValidator validator = new();
        ValidateOptionsResult result = validator.Validate(
            null,
            new LivePackageCompatibilityAuditOptions
            {
                Enabled = true,
                MaxConcurrentConnections = 5,
                ConnectionTimeout = TimeSpan.FromSeconds(10),
            });
        Assert.False(result.Failed);
    }

    [Fact]
    public void FirmwareUpgrade_RemainsDisabledByDefault()
    {
        Assert.False(new FirmwareUpgradeOptions().Enabled);
    }
}
