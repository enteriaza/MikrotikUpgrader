// -----------------------------------------------------------------------------
// File: PackageInventoryReportingTests.cs
//
// Description:
// Tests package inventory reporting helpers/logging used during assessment and upgrade
// preflight.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Logging;
/// <summary>
/// Covers package inventory reporting output.
/// </summary>
public sealed class PackageInventoryReportingTests
{
    /// <summary>
    /// Scenario: An 11-entry inventory captured from a live router is summarised: one independent
    /// <c>routeros-mipsbe</c> package and ten packages (system, ipv6, wireless, hotspot, mpls, routing, ppp, dhcp,
    /// security, advanced-tools) bundled into it.
    /// Expected: The independent count is 1 and the bundled count is 10. The formatted independent list is exactly
    /// <c>routeros-mipsbe/6.49.23</c>, and neither <c>system</c> nor <c>wireless</c> appears as independent.
    /// Invariant: Bundled sub-packages are never reported as independently installed packages.
    /// </summary>
    [Fact]
    public void Summarize_LiveElevenEntryInventory_ReportsOneIndependentAndTenBundled()
    {
        InstalledPackageInfo[] inventory =
        [
            Indep("routeros-mipsbe", "6.49.23"),
            Bundled("system", "6.49.23", "routeros-mipsbe"),
            Bundled("ipv6", "6.49.23", "routeros-mipsbe"),
            Bundled("wireless", "6.49.23", "routeros-mipsbe"),
            Bundled("hotspot", "6.49.23", "routeros-mipsbe"),
            Bundled("mpls", "6.49.23", "routeros-mipsbe"),
            Bundled("routing", "6.49.23", "routeros-mipsbe"),
            Bundled("ppp", "6.49.23", "routeros-mipsbe"),
            Bundled("dhcp", "6.49.23", "routeros-mipsbe"),
            Bundled("security", "6.49.23", "routeros-mipsbe"),
            Bundled("advanced-tools", "6.49.23", "routeros-mipsbe"),
        ];

        PackageInventorySummary summary = PackageInventoryReporting.Summarize(inventory);

        Assert.Equal(11, inventory.Length);
        Assert.Equal(1, summary.IndependentCount);
        Assert.Equal(10, summary.BundledCount);
        Assert.Equal("routeros-mipsbe/6.49.23", PackageInventoryReporting.FormatIndependentPackages(summary.IndependentPackages));
        Assert.DoesNotContain(summary.IndependentPackages, static p => p.Name == "system");
        Assert.DoesNotContain(summary.IndependentPackages, static p => p.Name == "wireless");
    }

    /// <summary>
    /// Scenario: An inventory holds <c>routeros-mipsbe</c> and <c>user-manager</c> as independent packages and
    /// <c>system</c> bundled into routeros-mipsbe.
    /// Expected: The independent count is 2 (including user-manager and routeros-mipsbe) and the bundled count is 1.
    /// Invariant: Optional standalone packages are counted as independent alongside the main RouterOS package.
    /// </summary>
    [Fact]
    public void Summarize_IndependentOptional_IsCountedSeparately()
    {
        InstalledPackageInfo[] inventory =
        [
            Indep("routeros-mipsbe", "6.49.23"),
            Bundled("system", "6.49.23", "routeros-mipsbe"),
            Indep("user-manager", "6.49.23"),
        ];

        PackageInventorySummary summary = PackageInventoryReporting.Summarize(inventory);

        Assert.Equal(2, summary.IndependentCount);
        Assert.Equal(1, summary.BundledCount);
        Assert.Contains(summary.IndependentPackages, static p => p.Name == "user-manager");
        Assert.Contains(summary.IndependentPackages, static p => p.Name == "routeros-mipsbe");
    }

    /// <summary>
    /// Private helper <c>Indep</c> supporting the scenarios in this file.
    /// </summary>
    private static InstalledPackageInfo Indep(string name, string version) =>
        new(name, version, Disabled: false, Bundle: null);

    /// <summary>
    /// Private helper <c>Bundled</c> supporting the scenarios in this file.
    /// </summary>
    private static InstalledPackageInfo Bundled(string name, string version, string bundle) =>
        new(name, version, Disabled: false, Bundle: bundle);
}
