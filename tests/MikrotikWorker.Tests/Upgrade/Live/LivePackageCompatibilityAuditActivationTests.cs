// -----------------------------------------------------------------------------
// File: LivePackageCompatibilityAuditActivationTests.cs
//
// Description:
// Dual-gate activation tests. These must never open a live router connection.
// Ambient appsettings.Development.local.json in the test output may enable
// live audit; tests that assert the disabled gate force Enabled=false after
// local config via
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade.Live;
/// <summary>
/// Dual-gate activation tests. These must never open a live router connection.
/// Ambient <c>appsettings.Development.local.json</c> in the test output may enable live audit;
/// tests that assert the disabled gate force <c>Enabled=false</c> after local config via
/// <see cref="LivePackageCompatibilityAuditRunner.RunLiveAuditAsync(string[], Action{HostApplicationBuilder}?, CancellationToken)"/>.
/// </summary>
[Trait("Category", "LiveAuditGate")]
public sealed class LivePackageCompatibilityAuditActivationTests
{
    /// <summary>
    /// A default <c>LivePackageCompatibilityAuditOptions</c> has <c>Enabled</c> false.
    /// </summary>
    [Fact]
    public void LiveAudit_DisabledByDefault()
    {
        Assert.False(new LivePackageCompatibilityAuditOptions().Enabled);
    }

    /// <summary>
    /// <c>IsLiveAuditRequested</c> is false for an empty argument list and for the fixture-only
    /// <c>--audit-package-compatibility</c> flag with an input file.
    /// </summary>
    [Fact]
    public void MissingCliOption_IsNotLiveAuditRequest()
    {
        Assert.False(LivePackageCompatibilityAuditRunner.IsLiveAuditRequested([]));
        Assert.False(LivePackageCompatibilityAuditRunner.IsLiveAuditRequested(
            ["--audit-package-compatibility", "--input", "a.csv"]));
    }

    /// <summary>
    /// <c>IsLiveAuditRequested</c> is true for <c>--audit-package-compatibility-live --input routers.csv</c>.
    /// </summary>
    [Fact]
    public void ExplicitCliOption_IsDetected()
    {
        Assert.True(LivePackageCompatibilityAuditRunner.IsLiveAuditRequested(
            ["--audit-package-compatibility-live", "--input", "routers.csv"]));
    }

    /// <summary>
    /// Running with only <c>--input routers.csv</c> returns exit code 2 without connecting.
    /// </summary>
    [Fact]
    public async Task RunLiveAudit_WithoutCliOption_FailsBeforeConnect()
    {
        int code = await LivePackageCompatibilityAuditRunner.RunLiveAuditAsync(
            ["--input", "routers.csv"]);
        Assert.Equal(2, code);
    }

    /// <summary>
    /// Running with <c>--audit-package-compatibility-live</c> but no <c>--input</c> returns exit code 2 without
    /// connecting.
    /// </summary>
    [Fact]
    public async Task RunLiveAudit_WithCliButMissingInput_FailsBeforeConnect()
    {
        int code = await LivePackageCompatibilityAuditRunner.RunLiveAuditAsync(
            ["--audit-package-compatibility-live"]);
        Assert.Equal(2, code);
    }

    /// <summary>
    /// With the CLI flag and an input CSV but the configuration forced to disabled, the runner returns exit code 2.
    /// The test forces <c>Enabled</c> false after the local settings files load, because
    /// <c>appsettings.Development.local.json</c> copied into the test output could otherwise enable the audit and
    /// let it attempt connections. That makes the assertion independent of the machine and connection-free.
    /// </summary>
    [Fact]
    public async Task RunLiveAudit_WithCliButDisabledConfig_FailsBeforeConnect()
    {
        // Root cause of prior flake: Host.CreateApplicationBuilder loads
        // appsettings.Development.local.json from the test output (copied from MikrotikWorker),
        // which may set LivePackageCompatibilityAudit:Enabled=true and allow the runner to
        // proceed past the gate (and potentially attempt connections). Force Enabled=false
        // after local files so this assertion is environment-independent and connection-free.
        string csv = Path.Combine(Path.GetTempPath(), $"live-audit-disabled-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csv, "routerip\n10.0.0.1\n");
        try
        {
            int code = await LivePackageCompatibilityAuditRunner.RunLiveAuditAsync(
                ["--audit-package-compatibility-live", "--input", csv],
                ForceLiveAuditDisabled);
            Assert.Equal(2, code);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    /// <summary>
    /// An in-memory configuration source added after the local files sets
    /// <c>LivePackageCompatibilityAudit:Enabled</c> to false (with dummy credentials). The runner still returns
    /// exit code 2 without connecting, which shows that later sources win over <c>Development.local.json</c> without
    /// enabling a live run.
    /// </summary>
    [Fact]
    public async Task RunLiveAudit_AmbientLocalEnable_IsOverridableToDisabledWithoutConnecting()
    {
        string csv = Path.Combine(Path.GetTempPath(), $"live-audit-override-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csv, "routerip\n10.0.0.1\n");
        try
        {
            int code = await LivePackageCompatibilityAuditRunner.RunLiveAuditAsync(
                ["--audit-package-compatibility-live", "--input", csv],
                builder =>
                {
                    // Prove later sources win over Development.local.json without enabling a live run.
                    builder.Configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["LivePackageCompatibilityAudit:Enabled"] = "false",
                            ["Mikrotik:Username"] = "must-not-connect",
                            ["Mikrotik:Password"] = "must-not-connect",
                        });
                });
            Assert.Equal(2, code);
        }
        finally
        {
            File.Delete(csv);
        }
    }

    /// <summary>
    /// <c>MaxConcurrentConnections</c> of 0 fails options validation.
    /// </summary>
    [Fact]
    public void InvalidConcurrency_FailsValidation()
    {
        LivePackageCompatibilityAuditOptionsValidator validator = new();
        ValidateOptionsResult result = validator.Validate(
            null,
            new LivePackageCompatibilityAuditOptions { MaxConcurrentConnections = 0 });
        Assert.True(result.Failed);
    }

    /// <summary>
    /// A zero <c>ConnectionTimeout</c> and a negative <c>TotalAuditTimeout</c> fail validation, with one failure
    /// message naming each option.
    /// </summary>
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

    /// <summary>
    /// Options with <c>Enabled</c> true, 5 concurrent connections and a 10 second connection timeout pass
    /// validation.
    /// </summary>
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

    /// <summary>
    /// A default <c>FirmwareUpgradeOptions</c> has <c>Enabled</c> false.
    /// </summary>
    [Fact]
    public void FirmwareUpgrade_RemainsDisabledByDefault()
    {
        Assert.False(new FirmwareUpgradeOptions().Enabled);
    }

    /// <summary>Forces <c>LivePackageCompatibilityAudit:Enabled</c> to <c>false</c> in the builder configuration.</summary>
    /// <param name="builder">Host builder to configure.</param>
    private static void ForceLiveAuditDisabled(HostApplicationBuilder builder) =>
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LivePackageCompatibilityAudit:Enabled"] = "false",
            });
}
