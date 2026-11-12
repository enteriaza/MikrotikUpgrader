// -----------------------------------------------------------------------------
// File: SyntheticPackageInventoryFixtures.cs
//
// Description:
// Synthetic lab mirror of 198.18.240.11 (identity Mikrotik, 6.49.19 mipsbe,
// main-only with /// ten bundled components). Not a live router observation.
// Identity remains Unknown/v7-eligible; /// a v6 device below the stepping
// release is stepped to 6.49.23 before the RouterOS 7 stage.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Clearly labelled synthetic RouterOS package inventories for non-live compatibility auditing.
/// These are not captured from production routers.
/// </summary>
internal static class SyntheticPackageInventoryFixtures
{
    /// <summary>
    /// Inventory-source marker stamped on every fixture so reports can never mistake them for live captures.
    /// </summary>
    public const string InventorySource = "SyntheticFixture";

    /// <summary>
    /// Returns every synthetic fixture in a stable order for auditing the whole set at once.
    /// </summary>
    /// <returns>All fixture devices defined in this class.</returns>
    public static IReadOnlyList<PackageCompatibilityAuditDevice> All() =>
    [
        MainOnlyMipsbeOlderV6(),
        MainPlusIndependentOptionalsMipsbe(),
        BundledComponentsMipsbe(),
        MissingOptionalInCatalogueSmips(),
        UnsupportedArchitecture(),
        InvalidEmptyInventory(),
        EmptyIdentityEligible(),
        UnknownIdentityEligible(),
        AceIdentity(),
        BrIdentity(),
        ApIdentity(),
        DclIdentity(),
        X86ArchlessMain(),
        PowerPcAlias(),
        AmbiguousDuplicateMainCandidates(),
        LabV6SteppingMikrotik1981824011(),
        LabV7PostUpgradeMikrotik1981824011(),
    ];

    /// <summary>
    /// Older v6 mipsbe router that has only the independent main package installed.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice MainOnlyMipsbeOlderV6() =>
        Device(
            "main-only-mipsbe-older-v6",
            "AP01.POP001.001.access",
            "10.0.0.10",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    /// <summary>
    /// v6 mipsbe router with the main package, bundled components, and independently installed optional packages.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice MainPlusIndependentOptionalsMipsbe() =>
        Device(
            "main-plus-optionals-mipsbe",
            "AP01.POP001.001.access",
            "10.0.0.11",
            "6.49.10",
            "mipsbe",
            [
                Indep("routeros-mipsbe", "6.49.10"),
                Bundled("system", "6.49.10", "routeros-mipsbe"),
                Bundled("ipv6", "6.49.10", "routeros-mipsbe"),
                Indep("user-manager", "6.49.10"),
                Indep("tr069-client", "6.49.10"),
            ]);

    /// <summary>
    /// v6 mipsbe core router whose extra components all ship inside the main package bundle.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice BundledComponentsMipsbe() =>
        Device(
            "bundled-components-mipsbe",
            "DCL01.POP001.001.core",
            "10.0.0.12",
            "6.49.18",
            "mipsbe",
            [
                Indep("routeros-mipsbe", "6.49.18"),
                Bundled("system", "6.49.18", "routeros-mipsbe"),
                Bundled("wireless", "6.49.18", "routeros-mipsbe"),
                Bundled("hotspot", "6.49.18", "routeros-mipsbe"),
            ]);

    /// <summary>
    /// smips catalogue has sparse optionals; an uncommon independent package should fail closed.
    /// </summary>
    public static PackageCompatibilityAuditDevice MissingOptionalInCatalogueSmips() =>
        Device(
            "missing-optional-smips",
            "AP02.POP002.002.access",
            "10.0.0.13",
            "6.49.10",
            "smips",
            [
                Indep("routeros-smips", "6.49.10"),
                Indep("user-manager", "6.49.10"),
            ]);

    /// <summary>
    /// Router reporting an architecture that has no firmware catalogue entry, so the audit must fail closed.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice UnsupportedArchitecture() =>
        Device(
            "unsupported-architecture",
            "AP01.POP001.001.access",
            "10.0.0.14",
            "6.49.10",
            "riscv",
            [Indep("routeros-riscv", "6.49.10")]);

    /// <summary>
    /// Router with an empty package list, which is an invalid inventory that cannot be audited.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice InvalidEmptyInventory() =>
        Device(
            "invalid-empty-inventory",
            "AP01.POP001.001.access",
            "10.0.0.15",
            "6.49.10",
            "mipsbe",
            []);

    /// <summary>
    /// Router whose identity is blank; it is treated as unknown and remains eligible for RouterOS 7.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice EmptyIdentityEligible() =>
        Device(
            "empty-identity",
            identity: " ",
            "10.0.0.16",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    /// <summary>
    /// Router with the default <c>MikroTik</c> identity that cannot be matched to a naming prefix but remains eligible.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice UnknownIdentityEligible() =>
        Device(
            "unknown-identity",
            "MikroTik",
            "10.0.0.17",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    /// <summary>
    /// Router whose identity carries the <c>ACE</c> prefix used for core devices.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice AceIdentity() =>
        Device(
            "ace-identity",
            "ACE01.POP001.001.core",
            "10.0.0.18",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    /// <summary>
    /// Router whose identity carries the <c>BR</c> prefix used for edge devices.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice BrIdentity() =>
        Device(
            "br-identity",
            "BR12.POP123.456.edge",
            "10.0.0.19",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    /// <summary>
    /// Router whose identity carries the <c>AP</c> prefix used for access devices.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice ApIdentity() =>
        Device(
            "ap-identity",
            "AP01.POP001.001.access",
            "10.0.0.20",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    /// <summary>
    /// Router whose identity carries the <c>DCL</c> prefix with extra dotted segments.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice DclIdentity() =>
        Device(
            "dcl-identity",
            "DCL12.POP123.456.some.device",
            "10.0.0.21",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    /// <summary>
    /// x86 router whose main package is the plain <c>routeros</c> name with no architecture suffix.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice X86ArchlessMain() =>
        Device(
            "x86-archless-main",
            "AP01.POP001.001.access",
            "10.0.0.22",
            "6.49.10",
            "x86",
            [Indep("routeros", "6.49.10")]);

    /// <summary>
    /// Router reporting the <c>ppc</c> architecture alias while its main package uses the <c>powerpc</c> name.
    /// </summary>
    /// <returns>The synthetic fixture device.</returns>
    public static PackageCompatibilityAuditDevice PowerPcAlias() =>
        Device(
            "powerpc-alias",
            "AP01.POP001.001.access",
            "10.0.0.23",
            "6.49.10",
            "ppc",
            [Indep("routeros-powerpc", "6.49.10")]);

    /// <summary>
    /// Inventory that is valid; ambiguity is exercised via a duplicate catalogue in tests.
    /// </summary>
    public static PackageCompatibilityAuditDevice AmbiguousDuplicateMainCandidates() =>
        Device(
            "ambiguous-main-candidates",
            "AP01.POP001.001.access",
            "10.0.0.24",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    /// <summary>
    /// Synthetic lab mirror of 198.18.240.11 (identity Mikrotik, 6.49.19 mipsbe, main-only with
    /// ten bundled components). Not a live router observation. Identity remains Unknown/v7-eligible;
    /// a v6 device below the stepping release is stepped to 6.49.23 before the RouterOS 7 stage.
    /// </summary>
    public static PackageCompatibilityAuditDevice LabV6SteppingMikrotik1981824011() =>
        Device(
            "lab-v6-stepping-198.18.240.11",
            "Mikrotik",
            "198.18.240.11",
            "6.49.19",
            "mipsbe",
            [
                Indep("routeros-mipsbe", "6.49.19"),
                Bundled("system", "6.49.19", "routeros-mipsbe"),
                Bundled("wireless", "6.49.19", "routeros-mipsbe"),
                Bundled("hotspot", "6.49.19", "routeros-mipsbe"),
                Bundled("dhcp", "6.49.19", "routeros-mipsbe"),
                Bundled("routing", "6.49.19", "routeros-mipsbe"),
                Bundled("mpls", "6.49.19", "routeros-mipsbe"),
                Bundled("ppp", "6.49.19", "routeros-mipsbe"),
                Bundled("security", "6.49.19", "routeros-mipsbe"),
                Bundled("advanced-tools", "6.49.19", "routeros-mipsbe"),
                Bundled("ipv6", "6.49.19", "routeros-mipsbe"),
            ]);

    /// <summary>
    /// Synthetic mirror of the observed post-RouterOS-7 inventory on 198.18.240.11
    /// (identity Mikrotik, 7.23.8 mipsbe, independent main <c>routeros</c>, zero bundled).
    /// </summary>
    public static PackageCompatibilityAuditDevice LabV7PostUpgradeMikrotik1981824011() =>
        Device(
            "lab-v7-post-upgrade-198.18.240.11",
            "Mikrotik",
            "198.18.240.11",
            "7.23.8",
            "mipsbe",
            [Indep("routeros", "7.23.8")]);

    /// <summary>
    /// Builds a fixture device, stamping it with <see cref="InventorySource"/>.
    /// </summary>
    /// <param name="fixtureName">Stable fixture identifier used in audit output.</param>
    /// <param name="identity">RouterOS identity string.</param>
    /// <param name="ip">Router IP address.</param>
    /// <param name="version">Raw RouterOS version string.</param>
    /// <param name="architecture">RouterOS architecture name.</param>
    /// <param name="packages">Installed package inventory.</param>
    /// <returns>The populated fixture device.</returns>
    private static PackageCompatibilityAuditDevice Device(
        string fixtureName,
        string identity,
        string ip,
        string version,
        string architecture,
        IReadOnlyList<InstalledPackageInfo> packages) =>
        new()
        {
            FixtureName = fixtureName,
            InventorySource = InventorySource,
            RouterIdentity = identity,
            RouterIp = ip,
            RouterOsVersionRaw = version,
            Architecture = architecture,
            Packages = packages,
        };

    /// <summary>
    /// Creates an enabled package that is installed independently, not as part of a bundle.
    /// </summary>
    /// <param name="name">Package name.</param>
    /// <param name="version">Package version.</param>
    /// <returns>The package record.</returns>
    private static InstalledPackageInfo Indep(string name, string version) =>
        new(name, version, Disabled: false, Bundle: null);

    /// <summary>
    /// Creates an enabled package that ships inside the named main-package bundle.
    /// </summary>
    /// <param name="name">Package name.</param>
    /// <param name="version">Package version.</param>
    /// <param name="bundle">Name of the main package that contains this component.</param>
    /// <returns>The package record.</returns>
    private static InstalledPackageInfo Bundled(string name, string version, string bundle) =>
        new(name, version, Disabled: false, Bundle: bundle);
}
