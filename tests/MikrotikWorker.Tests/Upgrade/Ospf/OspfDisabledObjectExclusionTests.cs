// -----------------------------------------------------------------------------
// File: OspfDisabledObjectExclusionTests.cs
//
// Description:
// Global fail-closed regressions: disabled/unknown-admin-state RouterOS objects
// never influence OSPF migration decisions, while diagnostics remain consistent.
//
// -----------------------------------------------------------------------------

using System.Net;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Comprehensive disabled-object exclusion coverage for OSPF migration.
/// </summary>
public sealed class OspfDisabledObjectExclusionTests
{
    /// <summary>
    /// Scenario: one enabled management address plus a disabled management address on another
    /// interface (Cashane-shaped /27,/26,/24 variants).
    /// Invariant: management derivation selects only the enabled contributor; the disabled address
    /// is recorded in ignored diagnostics and never appears as a candidate or /24 expansion source.
    /// </summary>
    [Theory]
    [InlineData("10.16.6.62/27", "10.16.6.32", "Bridge-VLAN-197_MGMT")]
    [InlineData("10.16.6.94/26", "10.16.6.64", "Bridge-VLAN-99_MGMT")]
    [InlineData("10.16.6.10/24", "10.16.6.0", "Bridge-VLAN-199_MGMT")]
    public void DisabledManagementAddresses_DoNotContributeCandidates(
        string address,
        string network,
        string iface)
    {
        OspfLoopbackRouterIdResult loopback = RequireLoopback("10.16.0.2");
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.2.30/27", "10.16.2.0", "Bridge-VLAN-199_MGMT"),
                Addr(address, network, iface, disabled: true),
            ],
            loopback,
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.2.0/24", result.ManagementCidr);
        Assert.DoesNotContain(result.ContributingAddresses, c => c.Contains("10.16.6.0/24", StringComparison.Ordinal));
        Assert.Contains(result.IgnoredDisabledAddresses, s => s.Contains(address, StringComparison.Ordinal));
        Assert.Contains(result.SelectionDiagnostics, d => d.StartsWith("Excluded disabled:", StringComparison.Ordinal));
        Assert.DoesNotContain(result.SelectionDiagnostics, d => d.StartsWith("candidate", StringComparison.OrdinalIgnoreCase) && d.Contains(address, StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: disabled Loopback-OSPF /32 and an enabled Loopback /32 with the same RID.
    /// Invariant: router-id derivation prefers the enabled loopback interface and ignores the
    /// disabled one.
    /// </summary>
    [Fact]
    public void DisabledLoopback_IsNotSelectedAsRouterId()
    {
        OspfLoopbackRouterIdResult result = OspfLoopbackRouterId.Derive(
        [
            Addr("10.16.0.2/32", "10.16.0.2", "Loopback-OSPF", disabled: true),
            Addr("10.16.0.2/32", "10.16.0.2", "Loopback"),
        ]);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.0.2", result.RouterId);
        Assert.Equal("Loopback", result.LoopbackInterfaceName);
    }

    /// <summary>
    /// Scenario: enabled management /27 plus a disabled transport /30 on the planned backhaul
    /// interface; isolation check also sees a second disabled /30 on another interface.
    /// Invariant: management CIDR stays on the enabled /27 expansion, and backhaul isolation
    /// succeeds without treating disabled transport addresses as conflicting inventory.
    /// </summary>
    [Fact]
    public void DisabledTransportSlash30_DoesNotAffectIsolationOrManagement()
    {
        OspfLoopbackRouterIdResult loopback = RequireLoopback("10.16.0.2");
        OspfSiteManagementSummaryResult mgmt = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.2.30/27", "10.16.2.0", "Bridge-VLAN-199_MGMT"),
                Addr("10.16.248.2/30", "10.16.248.0", "ether1", disabled: true),
            ],
            loopback,
            backhauls:
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "ether1",
                    NetworkCidr = "10.16.248.0/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
            ]);

        Assert.True(mgmt.Succeeded, mgmt.Detail);
        Assert.Equal("10.16.2.0/24", mgmt.ManagementCidr);

        IReadOnlyList<string> isolation = OspfMigrationPlanner.ValidateBackhaulTemplateIsolation(
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "ether1",
                    NetworkCidr = "10.16.248.0/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
            ],
            [
                Addr("10.16.248.1/30", "10.16.248.0", "ether1"),
                Addr("10.16.248.50/30", "10.16.248.48", "other", disabled: true),
            ]);
        Assert.Empty(isolation);
    }

    /// <summary>
    /// Scenario: same interface name carries one enabled and one disabled management address.
    /// Invariant: only the enabled address contributes; the disabled sibling is ignored and
    /// documented in <c>IgnoredDisabledAddresses</c>.
    /// </summary>
    [Fact]
    public void MixedEnabledDisabledSameInterfaceName_OnlyEnabledContributes()
    {
        OspfLoopbackRouterIdResult loopback = RequireLoopback("10.16.0.2");
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.2.30/27", "10.16.2.0", "Bridge-VLAN-197_MGMT"),
                Addr("10.16.6.62/27", "10.16.6.32", "Bridge-VLAN-197_MGMT", disabled: true),
            ],
            loopback,
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.2.0/24", result.ManagementCidr);
        Assert.Single(result.ContributingAddresses);
        Assert.Contains(result.IgnoredDisabledAddresses, s => s.Contains("10.16.6.62/27", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: one enabled management address and three unrelated disabled management addresses.
    /// Invariant: derivation still succeeds on the single enabled contributor; disabled rows do not
    /// create multi-candidate ambiguity.
    /// </summary>
    [Fact]
    public void MultipleDisabledAddresses_DoNotCreateAmbiguity()
    {
        OspfLoopbackRouterIdResult loopback = RequireLoopback("10.16.0.2");
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.2.30/27", "10.16.2.0", "Bridge-VLAN-199_MGMT"),
                Addr("10.16.6.62/27", "10.16.6.32", "Bridge-VLAN-197_MGMT", disabled: true),
                Addr("10.16.22.126/26", "10.16.22.64", "Bridge-VLAN-99_MGMT", disabled: true),
                Addr("10.16.9.1/24", "10.16.9.0", "Bridge-UNUSED", disabled: true),
            ],
            loopback,
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.2.0/24", result.ManagementCidr);
        Assert.Equal(3, result.IgnoredDisabledAddresses.Count);
    }

    /// <summary>
    /// Scenario: an address with <see cref="OspfAdminState.Unknown"/> alongside an enabled
    /// management address.
    /// Invariant: unknown admin state is ineligible and fail-closed excluded from candidates with
    /// an "Excluded unknown-disabled-state" diagnostic.
    /// </summary>
    [Fact]
    public void UnknownDisabledState_IsNotEligible_AndDoesNotContribute()
    {
        Assert.False(OspfInventoryEligibility.IsEligible(new OspfIpAddressRow
        {
            Address = "10.16.6.62/27",
            Interface = "Bridge-VLAN-197_MGMT",
            Disabled = false,
            AdminState = OspfAdminState.Unknown,
        }));

        OspfLoopbackRouterIdResult loopback = RequireLoopback("10.16.0.2");
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.2.30/27", "10.16.2.0", "Bridge-VLAN-199_MGMT"),
                new OspfIpAddressRow
                {
                    Address = "10.16.6.62/27",
                    Network = "10.16.6.32",
                    Interface = "Bridge-VLAN-197_MGMT",
                    Disabled = false,
                    AdminState = OspfAdminState.Unknown,
                },
            ],
            loopback,
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Equal("10.16.2.0/24", result.ManagementCidr);
        Assert.Contains(
            result.SelectionDiagnostics,
            d => d.StartsWith("Excluded unknown-disabled-state:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: Cashane-shaped capture plus disabled instance, area, range, template, and IP.
    /// Invariant: planner normalises away disabled/unknown-admin-state objects; they never become
    /// the migration target, planned areas/ranges, or passive site networks.
    /// </summary>
    [Fact]
    public void DisabledOspfObjects_DoNotParticipateInPlanning()
    {
        OspfCaptureSnapshot snapshot = BaselineCashaneShape();
        snapshot = WithDisabledExtras(snapshot);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("Excluded", StringComparison.Ordinal)
                 && n.Contains("disabled/unknown-admin-state", StringComparison.OrdinalIgnoreCase));

        // Disabled OSPFv2 instance must not become the migration target when an enabled one exists.
        // Target instance is always normalised to default-v2 on ROS7 captures.
        Assert.Equal("default-v2", result.Plan.InstanceName);
        Assert.DoesNotContain(result.Plan.Areas, a => string.Equals(a.Name, "disabled-area", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Plan.AreaRanges,
            r => string.Equals(r.RangeCidr, "10.16.99.0/24", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Plan.PassiveSiteNetworks.SelectMany(s => s.NetworkCidr.Split(',')),
            n => n.Contains("10.16.99.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: baseline Cashane capture plus a disabled OSPFv3 instance that is also listed in
    /// <c>OspfV3Objects</c>.
    /// Invariant: the v3 instance is never chosen as the OSPFv2 migration target, yet remains an
    /// OSPFv3 removal candidate.
    /// </summary>
    [Fact]
    public void DisabledOspfV3_DoesNotSelectAsOspfV2Target_ButRemainsRemovalCandidate()
    {
        OspfCaptureSnapshot snapshot = BaselineCashaneShape();
        List<OspfInstanceRow> instances = snapshot.Instances.ToList();
        instances.Add(new OspfInstanceRow
        {
            Name = "default-v3",
            Version = "3",
            RouterId = "10.16.0.2",
            Disabled = true,
            AdminState = OspfAdminState.Disabled,
        });
        snapshot = CloneSnapshot(
            snapshot,
            instances: instances,
            v3:
            [
                new OspfV3ObjectRef
                {
                    Path = "/routing/ospf/instance",
                    Id = "*v3",
                    Name = "default-v3",
                    Kind = "instance-v3",
                },
            ]);

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.NotEqual("default-v3", result.Plan.InstanceName);
        Assert.Contains(result.Plan.OspfV3ToRemove, o => o.Name == "default-v3");
    }

    /// <summary>
    /// Scenario: reconstructed Cashane production inventory where <c>10.16.6.62/27</c> is disabled.
    /// Invariant: management derivation never emits <c>10.16.6.0/24</c> as a contributor or CIDR;
    /// enabled 10.16.2/10.16.22 same-interface expansions still succeed.
    /// </summary>
    [Fact]
    public void Cashane_Disabled_10_16_6_62_NeverProduces_10_16_6_0_24()
    {
        // Inventory reconstructed from BR01.POP002.016.Cashane_10.16.0.2 production failure contributors,
        // with the observed disabled management address marked disabled.
        OspfLoopbackRouterIdResult loopback = RequireLoopback("10.16.0.2", "Loopback-OSPF");
        List<OspfIpAddressRow> addresses =
        [
            Addr("10.16.0.2/32", "10.16.0.2", "Loopback-OSPF"),
            Addr("10.16.2.30/27", "10.16.2.0", "Bridge-VLAN-199_MGMT"),
            Addr("10.16.2.62/27", "10.16.2.32", "Bridge-VLAN-197_MGMT"),
            Addr("10.16.2.126/26", "10.16.2.64", "Bridge-VLAN-99_MGMT"),
            Addr("10.16.2.190/26", "10.16.2.128", "Bridge-VLAN-99_MGMT"),
            Addr("10.16.2.254/26", "10.16.2.192", "Bridge-VLAN-99_MGMT"),
            Addr("10.16.22.126/26", "10.16.22.64", "Bridge-VLAN-99_MGMT"),
            Addr("10.16.22.190/26", "10.16.22.128", "Bridge-VLAN-99_MGMT"),
            Addr("10.16.22.254/26", "10.16.22.192", "Bridge-VLAN-99_MGMT"),
            Addr("10.16.6.62/27", "10.16.6.32", "Bridge-VLAN-197_MGMT", disabled: true),
        ];

        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            addresses,
            loopback,
            backhauls: []);

        Assert.DoesNotContain(
            result.ContributingAddresses,
            c => c.Contains("10.16.6.0/24", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.SelectionDiagnostics,
            d => d.Contains("candidate", StringComparison.OrdinalIgnoreCase)
                 && d.Contains("10.16.6.62/27", StringComparison.Ordinal));
        Assert.Contains(
            result.IgnoredDisabledAddresses,
            s => s.Contains("Bridge-VLAN-197_MGMT=10.16.6.62/27", StringComparison.Ordinal));

        // 10.16.2 and 10.16.22 both appear on Bridge-VLAN-99_MGMT → same-interface expansion.
        Assert.True(result.Succeeded, result.Detail);
        Assert.Contains("10.16.2.0/24", result.ManagementCidrs);
        Assert.Contains("10.16.22.0/24", result.ManagementCidrs);
        Assert.DoesNotContain(result.ManagementCidrs, c => c.Contains("10.16.6.0/24", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: Valskerm (.3) management/backhaul inventory with no Cashane addresses.
    /// Invariant: derivation succeeds on Valskerm CIDRs only and never imports Cashane-shaped
    /// 10.16.6.0/24 or Cashane identity strings.
    /// </summary>
    [Fact]
    public void Valskerm_Dot3_Fixture_IsIndependentOfCashaneInventory()
    {
        // Separate .3 router (BR01.POP003.016.Valskerm) evidence — must not reuse Cashane addresses.
        // 10.16.3 and 10.16.103 share Bridge-VLAN-99_MGMT → accepted expansion, not ambiguity.
        OspfLoopbackRouterIdResult loopback = RequireLoopback("10.16.0.3", "Loopback-OSPF");
        OspfSiteManagementSummaryResult result = OspfSiteManagementSummary.Derive(
            [
                Addr("10.16.0.3/32", "10.16.0.3", "Loopback-OSPF"),
                Addr("10.16.103.254/24", "10.16.103.0", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.3.30/27", "10.16.3.0", "Bridge-VLAN-199_MGMT"),
                Addr("10.16.3.62/27", "10.16.3.32", "Bridge-VLAN-197_MGMT"),
                Addr("10.16.3.126/26", "10.16.3.64", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.3.190/26", "10.16.3.128", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.3.254/26", "10.16.3.192", "Bridge-VLAN-99_MGMT"),
                Addr("10.16.248.10/30", "10.16.248.8", "sfpp-01"),
                Addr("10.16.248.13/30", "10.16.248.12", "eth-01"),
            ],
            loopback,
            backhauls: []);

        Assert.True(result.Succeeded, result.Detail);
        Assert.Contains("10.16.103.0/24", result.ManagementCidrs);
        Assert.Contains("10.16.3.0/24", result.ManagementCidrs);
        Assert.DoesNotContain(result.ManagementCidrs, c => c.Contains("10.16.6.0/24", StringComparison.Ordinal));
        Assert.DoesNotContain(result.ContributingAddresses, c => c.Contains("Cashane", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: raw RouterOS disabled strings including null, empty, yes/no/true/false, and junk.
    /// Invariant: parsing maps known values to Enabled/Disabled and unknown tokens to Unknown;
    /// only Enabled is eligible.
    /// </summary>
    [Theory]
    [InlineData(null, "Enabled")]
    [InlineData("", "Disabled")]
    [InlineData("yes", "Disabled")]
    [InlineData("true", "Disabled")]
    [InlineData("no", "Enabled")]
    [InlineData("false", "Enabled")]
    [InlineData("maybe", "Unknown")]
    public void ParseAdminState_FailClosedSemantics(string? raw, string expectedName)
    {
        OspfAdminState expected = Enum.Parse<OspfAdminState>(expectedName);
        Assert.Equal(expected, OspfInventoryEligibility.ParseAdminState(raw));
        Assert.Equal(expected == OspfAdminState.Enabled, OspfInventoryEligibility.IsEligible(expected));
    }

    /// <summary>
    /// Derives a successful loopback router-id result for the given /32, asserting success so
    /// callers can focus on management or planner behaviour.
    /// </summary>
    /// <param name="rid">Loopback host used as the /32 address and router-id.</param>
    /// <param name="iface">Loopback interface name; defaults to <c>Loopback</c>.</param>
    private static OspfLoopbackRouterIdResult RequireLoopback(string rid, string iface = "Loopback")
    {
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(
            [Addr($"{rid}/32", rid, iface)]);
        Assert.True(loopback.Succeeded, loopback.Detail);
        return loopback;
    }

    /// <summary>
    /// Builds an IP address inventory row with admin state tied to the disabled flag.
    /// </summary>
    /// <param name="address">Address with prefix length.</param>
    /// <param name="network">Network address.</param>
    /// <param name="iface">Interface the address is assigned to.</param>
    /// <param name="disabled">When true, marks the row disabled with <see cref="OspfAdminState.Disabled"/>.</param>
    private static OspfIpAddressRow Addr(
        string address,
        string network,
        string iface,
        bool disabled = false) =>
        new()
        {
            Address = address,
            Network = network,
            Interface = iface,
            Disabled = disabled,
            AdminState = disabled ? OspfAdminState.Disabled : OspfAdminState.Enabled,
        };

    /// <summary>
    /// Minimal Cashane-shaped ROS7 capture used as the enabled baseline before injecting disabled
    /// extras for planner exclusion tests.
    /// </summary>
    private static OspfCaptureSnapshot BaselineCashaneShape() =>
        new()
        {
            ImportId = Guid.Empty,
            JobId = "cashane",
            RouterIp = IPAddress.Parse("10.16.0.2"),
            Identity = "BR01.POP002.016.Cashane",
            RouterOsVersion = "7.23.8",
            Architecture = "arm",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances =
            [
                new OspfInstanceRow
                {
                    Name = "default",
                    Version = "2",
                    RouterId = "10.16.0.2",
                },
            ],
            Areas =
            [
                new OspfAreaRow
                {
                    Name = "backbone",
                    AreaId = "0.0.0.0",
                    Instance = "default",
                    Type = "default",
                },
                new OspfAreaRow
                {
                    Name = "BR01.POP002.016.Cashane",
                    AreaId = "10.16.0.2",
                    Instance = "default",
                    Type = "stub",
                    DefaultCost = "1",
                },
            ],
            InterfaceTemplates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "ether1",
                    Networks = "10.16.248.0/30",
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "backbone",
                    Interfaces = "Loopback-OSPF",
                    Networks = "10.16.0.2/32",
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = "BR01.POP002.016.Cashane",
                    Interfaces = "all",
                    Networks = "10.16.2.0/24",
                    Passive = true,
                },
            ],
            IpAddresses =
            [
                Addr("10.16.0.2/32", "10.16.0.2", "Loopback-OSPF"),
                Addr("10.16.248.1/30", "10.16.248.0", "ether1"),
                Addr("10.16.2.30/27", "10.16.2.0", "Bridge-VLAN-199_MGMT"),
            ],
            Neighbours =
            [
                new OspfNeighbourRow
                {
                    Address = "10.16.248.2",
                    RouterId = "198.18.255.253",
                    State = "Full",
                    Interface = "ether1",
                },
            ],
        };

    /// <summary>
    /// Clones <paramref name="baseline"/> and appends disabled instance, area, range, template, and
    /// management-address rows that must be excluded from planning.
    /// </summary>
    /// <param name="baseline">Enabled Cashane-shaped snapshot to extend.</param>
    private static OspfCaptureSnapshot WithDisabledExtras(OspfCaptureSnapshot baseline)
    {
        List<OspfInstanceRow> instances = baseline.Instances.ToList();
        instances.Add(new OspfInstanceRow
        {
            Name = "disabled-v2",
            Version = "2",
            RouterId = "10.16.0.99",
            Disabled = true,
            AdminState = OspfAdminState.Disabled,
        });

        List<OspfAreaRow> areas = baseline.Areas.ToList();
        areas.Add(new OspfAreaRow
        {
            Name = "disabled-area",
            AreaId = "10.16.0.99",
            Instance = "default",
            Type = "stub",
            Disabled = true,
            AdminState = OspfAdminState.Disabled,
        });

        List<OspfAreaRangeRow> ranges = baseline.AreaRanges.ToList();
        ranges.Add(new OspfAreaRangeRow
        {
            Area = "BR01.POP002.016.Cashane",
            Range = "10.16.99.0/24",
            Disabled = true,
            AdminState = OspfAdminState.Disabled,
        });

        List<OspfInterfaceTemplateRow> templates = baseline.InterfaceTemplates.ToList();
        templates.Add(new OspfInterfaceTemplateRow
        {
            Area = "BR01.POP002.016.Cashane",
            Interfaces = "all",
            Networks = "10.16.99.0/24",
            Passive = true,
            Disabled = true,
            AdminState = OspfAdminState.Disabled,
        });

        List<OspfIpAddressRow> addresses = baseline.IpAddresses.ToList();
        addresses.Add(Addr("10.16.6.62/27", "10.16.6.32", "Bridge-VLAN-197_MGMT", disabled: true));

        return CloneSnapshot(baseline, instances, areas, ranges, templates, addresses);
    }

    /// <summary>
    /// Shallow-copies a capture snapshot, optionally replacing individual inventory collections
    /// while preserving identity, version, and neighbour metadata.
    /// </summary>
    /// <param name="source">Snapshot to copy.</param>
    /// <param name="instances">Replacement instances, or null to keep <paramref name="source"/>.</param>
    /// <param name="areas">Replacement areas, or null to keep source.</param>
    /// <param name="ranges">Replacement area ranges, or null to keep source.</param>
    /// <param name="templates">Replacement interface templates, or null to keep source.</param>
    /// <param name="addresses">Replacement IP addresses, or null to keep source.</param>
    /// <param name="v3">Replacement OSPFv3 refs, or null to keep source.</param>
    private static OspfCaptureSnapshot CloneSnapshot(
        OspfCaptureSnapshot source,
        IReadOnlyList<OspfInstanceRow>? instances = null,
        IReadOnlyList<OspfAreaRow>? areas = null,
        IReadOnlyList<OspfAreaRangeRow>? ranges = null,
        IReadOnlyList<OspfInterfaceTemplateRow>? templates = null,
        IReadOnlyList<OspfIpAddressRow>? addresses = null,
        IReadOnlyList<OspfV3ObjectRef>? v3 = null) =>
        new()
        {
            ImportId = source.ImportId,
            JobId = source.JobId,
            RouterIp = source.RouterIp,
            Identity = source.Identity,
            RouterOsVersion = source.RouterOsVersion,
            Architecture = source.Architecture,
            SourceKind = source.SourceKind,
            Instances = instances ?? source.Instances,
            Areas = areas ?? source.Areas,
            AreaRanges = ranges ?? source.AreaRanges,
            Networks = source.Networks,
            Interfaces = source.Interfaces,
            InterfaceTemplates = templates ?? source.InterfaceTemplates,
            IpAddresses = addresses ?? source.IpAddresses,
            RoutingFilters = source.RoutingFilters,
            RoutingIds = source.RoutingIds,
            Neighbours = source.Neighbours,
            OspfV3Objects = v3 ?? source.OspfV3Objects,
        };
}
