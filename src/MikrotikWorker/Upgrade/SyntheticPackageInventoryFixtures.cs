using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Clearly labelled synthetic RouterOS package inventories for non-live compatibility auditing.
/// These are not captured from production routers.
/// </summary>
internal static class SyntheticPackageInventoryFixtures
{
    public const string InventorySource = "SyntheticFixture";

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
    ];

    public static PackageCompatibilityAuditDevice MainOnlyMipsbeOlderV6() =>
        Device(
            "main-only-mipsbe-older-v6",
            "AP01.POP001.001.access",
            "10.0.0.10",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

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

    public static PackageCompatibilityAuditDevice UnsupportedArchitecture() =>
        Device(
            "unsupported-architecture",
            "AP01.POP001.001.access",
            "10.0.0.14",
            "6.49.10",
            "riscv",
            [Indep("routeros-riscv", "6.49.10")]);

    public static PackageCompatibilityAuditDevice InvalidEmptyInventory() =>
        Device(
            "invalid-empty-inventory",
            "AP01.POP001.001.access",
            "10.0.0.15",
            "6.49.10",
            "mipsbe",
            []);

    public static PackageCompatibilityAuditDevice EmptyIdentityEligible() =>
        Device(
            "empty-identity",
            identity: " ",
            "10.0.0.16",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    public static PackageCompatibilityAuditDevice UnknownIdentityEligible() =>
        Device(
            "unknown-identity",
            "MikroTik",
            "10.0.0.17",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    public static PackageCompatibilityAuditDevice AceIdentity() =>
        Device(
            "ace-identity",
            "ACE01.POP001.001.core",
            "10.0.0.18",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    public static PackageCompatibilityAuditDevice BrIdentity() =>
        Device(
            "br-identity",
            "BR12.POP123.456.edge",
            "10.0.0.19",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    public static PackageCompatibilityAuditDevice ApIdentity() =>
        Device(
            "ap-identity",
            "AP01.POP001.001.access",
            "10.0.0.20",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    public static PackageCompatibilityAuditDevice DclIdentity() =>
        Device(
            "dcl-identity",
            "DCL12.POP123.456.some.device",
            "10.0.0.21",
            "6.49.10",
            "mipsbe",
            [Indep("routeros-mipsbe", "6.49.10")]);

    public static PackageCompatibilityAuditDevice X86ArchlessMain() =>
        Device(
            "x86-archless-main",
            "AP01.POP001.001.access",
            "10.0.0.22",
            "6.49.10",
            "x86",
            [Indep("routeros", "6.49.10")]);

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

    private static InstalledPackageInfo Indep(string name, string version) =>
        new(name, version, Disabled: false, Bundle: null);

    private static InstalledPackageInfo Bundled(string name, string version, string bundle) =>
        new(name, version, Disabled: false, Bundle: bundle);
}
