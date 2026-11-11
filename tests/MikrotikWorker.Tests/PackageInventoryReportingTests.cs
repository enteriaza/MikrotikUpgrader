using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests;

public sealed class PackageInventoryReportingTests
{
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

    private static InstalledPackageInfo Indep(string name, string version) =>
        new(name, version, Disabled: false, Bundle: null);

    private static InstalledPackageInfo Bundled(string name, string version, string bundle) =>
        new(name, version, Disabled: false, Bundle: bundle);
}
