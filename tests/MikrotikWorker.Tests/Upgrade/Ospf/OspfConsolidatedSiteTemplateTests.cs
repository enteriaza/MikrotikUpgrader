// -----------------------------------------------------------------------------
// File: OspfConsolidatedSiteTemplateTests.cs
//
// Description:
// Tests for consolidated site OSPF interface-template planning and matching.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
    /// <summary>
    /// Scenario: verifier against authoritative Tommy inventory, then the same inventory plus a legacy iface-bound template.
    /// Expected: authoritative inventory verifies successfully; the dirty inventory fails verification.
    /// Invariant: consolidated site templates must not accept leftover legacy interface-bound OSPF templates.
    /// </summary>
public sealed class OspfConsolidatedSiteTemplateTests
{
    /// <summary>
    /// Scenario: build the Tommy recovery plan (default-v2, router-id 10.16.0.11).
    /// Expected: exactly one active ptp backhaul (cost 10) on eth-01_BH01.Tommy-Shylock, one passive 10.16.0.11/32 loopback, and one passive 'all' site template for '10.16.11.0/24,100.64.0.0/10,172.16.0.0/12'; the site area is stub with summaries enabled and default cost 1; three area ranges (10.16.11.0/24 MGMT, 100.99.76.16/28, 172.26.197.4/30).
    /// Invariant: a site is represented by exactly three templates, with all site networks consolidated into one matcher.
    /// </summary>
    [Fact]
    public void RecoveryPlan_MatchesAuthoritativeThreeTemplateShape()
    {
        OspfMigrationPlan plan = BuildTommyRecoveryPlan();
        Assert.True(plan.IsValid, string.Join("; ", plan.ValidationErrors));

        Assert.Equal("default-v2", plan.InstanceName);
        Assert.Equal("10.16.0.11", plan.RouterId);
        Assert.Single(plan.ActiveBackhauls);
        Assert.False(plan.ActiveBackhauls[0].Passive);
        Assert.Equal(10, plan.ActiveBackhauls[0].Cost);
        Assert.Equal("ptp", plan.ActiveBackhauls[0].Type);
        Assert.Equal("eth-01_BH01.Tommy-Shylock", plan.ActiveBackhauls[0].Interface);

        Assert.Single(plan.PassiveLoopbacks);
        Assert.True(plan.PassiveLoopbacks[0].Passive);
        Assert.Equal("10.16.0.11/32", plan.PassiveLoopbacks[0].NetworkCidr);
        Assert.Equal("Loopback", plan.PassiveLoopbacks[0].Interface);

        OspfPlanSiteNetworkTemplate site = Assert.Single(plan.PassiveSiteNetworks);
        Assert.Equal("all", site.Interfaces);
        Assert.True(site.Passive);
        Assert.Equal("10.16.11.0/24,100.64.0.0/10,172.16.0.0/12", site.NetworkCidr);

        OspfPlanArea siteArea = Assert.Single(plan.Areas, a => !a.IsBackbone);
        Assert.Equal("stub", siteArea.TargetType);
        Assert.False(siteArea.NoSummaries);
        Assert.Equal("1", siteArea.DefaultCost);

        Assert.Equal(3, plan.AreaRanges.Count);
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "10.16.11.0/24" && r.Comment == "MGMT");
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "100.99.76.16/28");
        Assert.Contains(plan.AreaRanges, r => r.RangeCidr == "172.26.197.4/30");
    }

    /// <summary>
    /// Scenario: verifier on authoritative Tommy inventory, then inventory plus a legacy iface-bound template.
    /// Expected: authoritative verifies; dirty inventory fails with leftover-template detail.
    /// Invariant: consolidated site templates reject legacy interface-bound OSPF templates.
    /// </summary>
    [Fact]
    public async Task Verifier_AcceptsAuthoritativeInventory_RejectsLegacyIfaceBound()
    {
        OspfMigrationPlan plan = BuildTommyRecoveryPlan();
        FakeOps okOps = new() { Inventory = AuthoritativeInventory(plan) };
        OspfVerificationResult ok = await new OspfV7MigrationVerifier(okOps, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.True(ok.Succeeded, ok.Detail);

        OspfV7Inventory dirty = AuthoritativeInventory(plan);
        dirty = new OspfV7Inventory
        {
            Instances = dirty.Instances,
            Areas = dirty.Areas,
            AreaRanges = dirty.AreaRanges,
            Templates =
            [
                ..dirty.Templates,
                new OspfInterfaceTemplateRow
                {
                    Area = "BR01.POP011.016.Tommy-v2",
                    Interfaces = "Bridge-VLAN-99_MGMT",
                    Networks = "10.16.11.0/27",
                    Type = "ptp",
                    Passive = true,
                    Disabled = false,
                },
            ],
        };

        FakeOps dirtyOps = new() { Inventory = dirty };
        OspfVerificationResult bad = await new OspfV7MigrationVerifier(dirtyOps, NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.False(bad.Succeeded);
        Assert.Contains(
            bad.Errors,
            e => e.Contains("Unexpected template remains", StringComparison.OrdinalIgnoreCase)
                 || e.Contains("legacy interface-bound", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: post-apply inventory (a) lacks 172.16.0.0/12 in the consolidated site matcher, and (b) has the site template flipped to non-passive.
    /// Expected: verification fails in both cases.
    /// Invariant: the verifier must catch an incomplete network list and a site template that is no longer passive.
    /// </summary>
    [Fact]
    public async Task Verifier_FailsMissingNetworkMatcher_AndActiveSite()
    {
        OspfMigrationPlan plan = BuildTommyRecoveryPlan();
        OspfV7Inventory missingNet = AuthoritativeInventory(plan);
        missingNet = WithSiteNetworks(missingNet, "10.16.11.0/24,100.64.0.0/10"); // missing 172.16/12
        OspfVerificationResult missing = await new OspfV7MigrationVerifier(
                new FakeOps { Inventory = missingNet },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.False(missing.Succeeded);

        OspfV7Inventory activeSite = AuthoritativeInventory(plan);
        activeSite = new OspfV7Inventory
        {
            Instances = activeSite.Instances,
            Areas = activeSite.Areas,
            AreaRanges = activeSite.AreaRanges,
            Templates = activeSite.Templates
                .Select(t =>
                    OspfSiteMatcherConsolidation.IsAllInterfaces(t.Interfaces)
                        ? new OspfInterfaceTemplateRow
                        {
                            Area = t.Area,
                            Interfaces = t.Interfaces,
                            Networks = t.Networks,
                            Passive = false,
                            Disabled = false,
                        }
                        : t)
                .ToList(),
        };
        OspfVerificationResult active = await new OspfV7MigrationVerifier(
                new FakeOps { Inventory = activeSite },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);
        Assert.False(active.Succeeded);
    }

    /// <summary>
    /// Scenario: the router holds the planned backhaul, loopback and site templates plus a legacy passive template bound to Bridge-VLAN-99_MGMT (10.16.11.0/27) that the plan marks as superseded.
    /// Expected: exactly one remove is issued, for the legacy template (*3); the backhaul template (*1) is never removed.
    /// Invariant: obsolete cleanup targets only the superseded interface-bound passive template and leaves planned templates alone.
    /// </summary>
    [Fact]
    public async Task RemoveObsolete_RemovesLegacyPassiveIfaceBound_NotBackhaul()
    {
        OspfMigrationPlan basePlan = BuildTommyRecoveryPlan();
        OspfMigrationPlan plan = WithSuperseded(
            basePlan,
            OspfTemplateSemanticIdentity.FromCaptured(
                basePlan.PassiveSiteNetworks[0].AreaName,
                "Bridge-VLAN-99_MGMT",
                "10.16.11.0/27",
                passive: true,
                new OspfCapturedTemplateSemantics { AreaInstanceName = "default-v2" }));
        int removeCount = 0;
        List<Dictionary<string, string>> planned =
        [
            new Dictionary<string, string>
            {
                [".id"] = "*1",
                ["area"] = "backbone-v2",
                ["interfaces"] = "eth-01_BH01.Tommy-Shylock",
                ["networks"] = "10.16.248.76/30",
                ["type"] = "ptp",
                ["cost"] = "10",
                ["disabled"] = "false",
            },
            new Dictionary<string, string>
            {
                [".id"] = "*9",
                ["area"] = "backbone-v2",
                ["interfaces"] = "Loopback",
                ["networks"] = "10.16.0.11/32",
                ["passive"] = "",
                ["disabled"] = "false",
            },
            new Dictionary<string, string>
            {
                [".id"] = "*A",
                ["area"] = "BR01.POP011.016.Tommy-v2",
                ["interfaces"] = "all",
                ["networks"] = plan.PassiveSiteNetworks[0].NetworkCidr,
                ["passive"] = "",
                ["disabled"] = "false",
            },
        ];
        Dictionary<string, string> obsolete = new()
        {
            [".id"] = "*3",
            ["area"] = "BR01.POP011.016.Tommy-v2",
            ["interfaces"] = "Bridge-VLAN-99_MGMT",
            ["networks"] = "10.16.11.0/27",
            ["passive"] = "",
            ["disabled"] = "false",
            ["cost"] = "1",
            ["priority"] = "128",
            ["use-bfd"] = "false",
        };

        ParameterCapturingConnection capturing = new(
            new TikFakeConnection()
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/area/print",
                    static _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*ab",
                            ["name"] = "backbone-v2",
                            ["instance"] = "default-v2",
                        }),
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*as",
                            ["name"] = "BR01.POP011.016.Tommy-v2",
                            ["instance"] = "default-v2",
                        }),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        List<Dictionary<string, string>> view = [..planned];
                        if (removeCount == 0)
                        {
                            view.Add(obsolete);
                        }

                        return
                        [
                            ..view.Select(static row => new TikFakeReSentence(new Dictionary<string, string>(row))),
                            new TikFakeDoneSentence(),
                        ];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/remove",
                    _ =>
                    {
                        removeCount++;
                        return [new TikFakeDoneSentence()];
                    }));

        await new OspfMikrotikOperations(NullLogger<OspfMikrotikOperations>.Instance)
            .RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(1, removeCount);
        Assert.DoesNotContain(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*1"));
        Assert.Contains(capturing.Removes, r => r.Any(p => p.Name == ".id" && p.Value == "*3"));
    }

    /// <summary>
    /// Scenario: post-apply inventory still holds a disabled legacy Bridge-VLAN-99_MGMT template next to the three planned ones.
    /// Expected: verification fails with an error about a disabled legacy template remaining (or not exactly 3 templates).
    /// Invariant: disabling a legacy template is not a substitute for removing it.
    /// </summary>
    [Fact]
    public async Task Verifier_FailsWhenDisabledLegacyTemplateRemains()
    {
        OspfMigrationPlan plan = BuildTommyRecoveryPlan();
        OspfV7Inventory inventory = AuthoritativeInventory(plan);
        inventory = new OspfV7Inventory
        {
            Instances = inventory.Instances,
            Areas = inventory.Areas,
            AreaRanges = inventory.AreaRanges,
            Templates =
            [
                ..inventory.Templates,
                new OspfInterfaceTemplateRow
                {
                    Area = "BR01.POP011.016.Tommy-v2",
                    Interfaces = "Bridge-VLAN-99_MGMT",
                    Networks = "10.16.11.0/27",
                    Passive = true,
                    Disabled = true,
                },
            ],
        };

        OspfVerificationResult result = await new OspfV7MigrationVerifier(
                new FakeOps { Inventory = inventory },
                NullLogger<OspfV7MigrationVerifier>.Instance)
            .VerifyAsync(FakeMikrotikConnections.CreateFullyPopulated(), plan, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            e => e.Contains("Disabled legacy template remains", StringComparison.OrdinalIgnoreCase)
                 || e.Contains("expected exactly 3", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: a v6 snapshot whose site stub area has no-summaries=true and networks 10.10.0.0/16, 100.99.0.0/16 and 172.26.0.0/16.
    /// Expected: the plan succeeds, NoSummaries is false, a note records the clearing, and the single passive matcher on 'all' is '10.10.0.0/16,100.64.0.0/10,172.16.0.0/12'.
    /// Invariant: the planned site area never carries no-summaries and fleet networks fold into the fleet aggregates.
    /// </summary>
    [Fact]
    public void Planner_ClearsNoSummariesFromV6SourceArea()
    {
        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = Guid.NewGuid(),
            JobId = "job",
            RouterIp = IPAddress.Parse("10.0.0.1"),
            Identity = "AP01",
            RouterOsVersion = "6.49.23",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            Instances = [new OspfInstanceRow { Name = "default", RouterId = "10.255.0.1", IsDefault = true }],
            Areas =
            [
                new OspfAreaRow { Name = "backbone", AreaId = "0.0.0.0", Type = "default" },
                new OspfAreaRow
                {
                    Name = "site",
                    AreaId = "10.255.0.1",
                    Type = "stub",
                    DefaultCost = "1",
                    NoSummaries = true,
                },
            ],
            AreaRanges = [new OspfAreaRangeRow { Area = "site", Range = "10.10.0.0/16" }],
            Networks =
            [
                new OspfNetworkRow { Area = "backbone", Network = "10.255.0.1/32" },
                new OspfNetworkRow { Area = "backbone", Network = "10.0.0.0/30" },
                new OspfNetworkRow { Area = "site", Network = "10.10.0.0/16" },
                new OspfNetworkRow { Area = "site", Network = "100.99.0.0/16" },
                new OspfNetworkRow { Area = "site", Network = "172.26.0.0/16" },
            ],
            Interfaces =
            [
                new OspfInterfaceRow { Interface = "ether1", NetworkType = "ptp", Cost = "10", Passive = false },
            ],
            IpAddresses =
            [
                new OspfIpAddressRow { Address = "10.255.0.1/32", Network = "10.255.0.1", Interface = "lo" },
                new OspfIpAddressRow { Address = "10.0.0.1/30", Network = "10.0.0.0", Interface = "ether1" },
                new OspfIpAddressRow { Address = "10.10.1.1/24", Network = "10.10.1.0", Interface = "bridge-local" },
            ],
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        Assert.True(result.Succeeded, string.Join("; ", result.Plan.ValidationErrors));
        OspfPlanArea site = Assert.Single(result.Plan.Areas, a => a.Name == "site");
        Assert.False(site.NoSummaries);
        Assert.Contains(
            result.Plan.NormalisationNotes,
            n => n.Contains("no-summaries", StringComparison.OrdinalIgnoreCase)
                 && n.Contains("clearing", StringComparison.OrdinalIgnoreCase));
        OspfPlanSiteNetworkTemplate matcher = Assert.Single(result.Plan.PassiveSiteNetworks);
        Assert.Equal("all", matcher.Interfaces);
        Assert.Equal("10.10.0.0/16,100.64.0.0/10,172.16.0.0/12", matcher.NetworkCidr);
    }

    /// <summary>
    /// Copies a migration plan field for field and appends the extra superseded template identities to its existing
    /// list.
    /// </summary>
    private static OspfMigrationPlan WithSuperseded(
        OspfMigrationPlan plan,
        params OspfSupersededTemplateIdentity[] extra) =>
        new()
        {
            ImportId = plan.ImportId,
            JobId = plan.JobId,
            RouterIp = plan.RouterIp,
            Identity = plan.Identity,
            SourceRouterOsVersion = plan.SourceRouterOsVersion,
            Architecture = plan.Architecture,
            SourceKind = plan.SourceKind,
            ContentHash = plan.ContentHash,
            InstanceName = plan.InstanceName,
            RouterId = plan.RouterId,
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            SupersededTemplates = plan.SupersededTemplates.Concat(extra).ToList(),
            RemoveAllRoutingFilters = plan.RemoveAllRoutingFilters,
            ClearAllRedistribution = plan.ClearAllRedistribution,
            RemoveAllOspfV3 = plan.RemoveAllOspfV3,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = plan.NormalisationNotes,
            ValidationErrors = plan.ValidationErrors,
        };

    /// <summary>
    /// Builds the Tommy (BR01.POP011.016.Tommy) recovery plan from a temporary reference <c>.rsc</c> file and a live v7 inventory/IP address set, asserting the plan is valid before returning it.
    /// </summary>
    private static OspfMigrationPlan BuildTommyRecoveryPlan()
    {
        string path = Path.Combine(Path.GetTempPath(), "tommy-consol-" + Guid.NewGuid().ToString("N") + ".rsc");
        File.WriteAllText(
            path,
            """
            /routing ospf area range
            add area=BR01.POP011.016.Tommy comment=MGMT range=10.16.11.0/24
            add area=BR01.POP011.016.Tommy comment=HEROTEL_ACTIVE_POOL range=100.99.76.16/28
            add area=BR01.POP011.016.Tommy comment=HEROTEL_INACTIVE_POOL range=172.26.197.4/30
            /routing ospf network
            add area=backbone network=10.16.248.76/30
            add area=backbone network=10.16.0.11/32
            add area=BR01.POP011.016.Tommy network=10.16.11.0/24
            add area=BR01.POP011.016.Tommy comment="ACS-Provisioning Subnet" network=100.67.16.0/24
            add area=BR01.POP011.016.Tommy comment="Active PPPoE Range" network=100.99.0.0/16
            add area=BR01.POP011.016.Tommy comment="Inactive PPPoE Range" network=172.26.0.0/16
            """);
        try
        {
            OspfPlanValidationResult built = OspfLabRecoveryPlanBuilder.BuildFromLiveAndReference(
                Guid.NewGuid(),
                "job",
                IPAddress.Parse("198.18.240.11"),
                "BR01.POP011.016.Tommy",
                "7.23.8",
                "mipsbe",
                new OspfV7Inventory
                {
                    Instances = [new OspfInstanceRow { Name = "default-v2", RouterId = "10.16.0.11", Version = "2" }],
                    Areas =
                    [
                        new OspfAreaRow { Name = "backbone-v2", AreaId = "0.0.0.0", Instance = "default-v2", Type = "default" },
                        new OspfAreaRow
                        {
                            Name = "BR01.POP011.016.Tommy-v2",
                            AreaId = "10.16.0.11",
                            Instance = "default-v2",
                            Type = "stub",
                            DefaultCost = "1",
                        },
                    ],
                },
                [
                    new OspfIpAddressRow { Address = "10.16.248.78/30", Network = "10.16.248.76", Interface = "eth-01_BH01.Tommy-Shylock" },
                    new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                    new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "VLAN-99_MGMT_wlan" },
                    new OspfIpAddressRow { Address = "198.18.240.11/24", Network = "198.18.240.0", Interface = "eth-01_BH01.Tommy-Shylock" },
                ],
                path);
            Assert.True(built.Succeeded, string.Join("; ", built.Plan.ValidationErrors));
            return built.Plan;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Builds a v7 inventory that exactly matches the plan's authoritative three-template shape: one active backhaul, one passive loopback and one consolidated passive site template.
    /// </summary>
    private static OspfV7Inventory AuthoritativeInventory(OspfMigrationPlan plan)
    {
        OspfV7Inventory baseInv = OspfTestBaselines.InventoryMatchingPlan(plan);
        string backbone = string.IsNullOrWhiteSpace(plan.TargetBackboneAreaName)
            ? OspfFleetNaming.Ros7BackboneAreaName
            : plan.TargetBackboneAreaName;
        string site = string.IsNullOrWhiteSpace(plan.TargetSiteAreaName)
            ? OspfFleetNaming.Ros7SiteAreaName(plan.Identity)
            : plan.TargetSiteAreaName;

        return new OspfV7Inventory
        {
            Instances = baseInv.Instances,
            Areas = baseInv.Areas,
            AreaRanges = plan.AreaRanges.Select(r => new OspfAreaRangeRow
            {
                Area = plan.Areas.FirstOrDefault(a => a.Name == r.AreaName)?.IsBackbone == true
                    ? backbone
                    : site,
                Range = r.RangeCidr,
                Comment = r.Comment,
                Advertise = true,
            }).ToList(),
            Templates =
            [
                new OspfInterfaceTemplateRow
                {
                    Area = backbone,
                    Interfaces = plan.ActiveBackhauls[0].Interface,
                    Networks = plan.ActiveBackhauls[0].NetworkCidr,
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = backbone,
                    Interfaces = plan.PassiveLoopbacks[0].Interface,
                    Networks = plan.PassiveLoopbacks[0].NetworkCidr,
                    Passive = true,
                },
                new OspfInterfaceTemplateRow
                {
                    Area = site,
                    Interfaces = plan.PassiveSiteNetworks[0].Interfaces,
                    Networks = plan.PassiveSiteNetworks[0].NetworkCidr,
                    Passive = true,
                },
            ],
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="source"/> in which every all-interfaces site template carries <paramref name="networks"/>, leaving other templates untouched.
    /// <param name="source">Inventory to copy.</param>
    /// <param name="networks">Comma-separated network list to place on the site template.</param>
    /// </summary>
    private static OspfV7Inventory WithSiteNetworks(OspfV7Inventory source, string networks) =>
        new()
        {
            Instances = source.Instances,
            Areas = source.Areas,
            AreaRanges = source.AreaRanges,
            Templates = source.Templates
                .Select(t =>
                    OspfSiteMatcherConsolidation.IsAllInterfaces(t.Interfaces)
                        ? new OspfInterfaceTemplateRow
                        {
                            Area = t.Area,
                            Interfaces = t.Interfaces,
                            Networks = networks,
                            Passive = t.Passive,
                            Disabled = t.Disabled,
                        }
                        : t)
                .ToList(),
        };

    /// <summary>
    /// Ops fake that returns a preconfigured v7 inventory plus Tommy loopback and management addresses; all other
    /// operations are no-ops or unsupported.
    /// </summary>
    private sealed class FakeOps : IOspfMikrotikOperations
    {
        /// <summary>
        /// Inventory returned by <see cref="ReadV7InventoryAsync"/>.
        /// </summary>
        public required OspfV7Inventory Inventory { get; init; }

        /// <summary>
        /// Returns the preconfigured inventory instead of reading from the connection.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(Inventory);

        /// <summary>
        /// Reports zero routing filters.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        /// <summary>
        /// Not supported; capture is not part of the verification path under test.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op; routing filter removal is not under test.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; redistribution clearing is not under test.
        /// </summary>
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; instance filter reference clearing is not under test.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; area creation is not under test.
        /// </summary>
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; area range creation is not under test.
        /// </summary>
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; backhaul template creation is not under test.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; loopback template creation is not under test.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; site network template creation is not under test.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;


        /// <summary>
        /// No-op; site network template state assertion is not under test.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        /// <summary>
        /// No-op; obsolete template removal is not under test.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Not supported; management-plane baseline capture is not part of these scenarios.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>
        /// No-op; the backbone guard is not under test.
        /// </summary>
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Reports no OSPF neighbours.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        /// <summary>
        /// No-op; OSPFv3 removal is not under test.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op; router-id changes are not under test.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Returns the loopback and management VLAN addresses matching the Tommy plan.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OspfIpAddressRow>>(
            [
                new OspfIpAddressRow { Address = "10.16.0.11/32", Network = "10.16.0.11", Interface = "Loopback" },
                new OspfIpAddressRow { Address = "10.16.11.30/27", Network = "10.16.11.0", Interface = "VLAN-99_MGMT_wlan" },
            ]);
    }

    /// <summary>
    /// Connection wrapper that captures interface-template remove commands so their parameters can be inspected
    /// after the production code has added them.
    /// </summary>
    private sealed class ParameterCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        /// <summary>
        /// Interface-template remove commands issued through this connection, in creation order.
        /// </summary>
        private readonly List<ITikCommand> _removes = [];

        /// <summary>
        /// Parameters of each tracked interface-template remove command, as name/value pairs.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(string Name, string? Value)>> Removes =>
            _removes
                .Select(static c => (IReadOnlyList<(string Name, string? Value)>)c.Parameters
                    .Select(static p => (p.Name, p.Value))
                    .ToList())
                .ToList();

        /// <summary>
        /// Creates the command via the inner connection and records it when it is an interface-template remove.
        /// </summary>
        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        /// <summary>
        /// Creates the command (with an explicit parameter format) via the inner connection and records it when it is an interface-template remove.
        /// </summary>
        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        /// <summary>
        /// Remembers <paramref name="cmd"/> when <paramref name="commandText"/> is <c>/routing/ospf/interface-template/remove</c>.
        /// <param name="commandText">RouterOS command path being created.</param>
        /// <param name="cmd">Command instance created for that path.</param>
        /// </summary>
        private void Track(string commandText, ITikCommand cmd)
        {
            if (commandText.Equals("/routing/ospf/interface-template/remove", StringComparison.Ordinal))
            {
                _removes.Add(cmd);
            }
        }
    }
}
