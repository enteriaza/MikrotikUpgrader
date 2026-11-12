using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Fake-based coverage for OSPF interface-template cleanup classification, pre-deletion safety gate,
/// fresh .id resolution, partial-failure recovery, inventory mutation abort, and cleanup idempotency.
/// </summary>
public sealed class OspfTemplateCleanupSafetyTests
{
    [Theory]
    [InlineData("unexpected-area", "backbone-v2", "ether99", "10.255.255.0/30", true, false)]
    [InlineData("unrelated-network", "site", "Bridge-VLAN-99_MGMT", "192.0.2.0/24", true, false)]
    [InlineData("unrelated-active", "site", "Bridge-VLAN-50", "198.51.100.0/30", false, false)]
    [InlineData("unrelated-passive", "site", "all", "203.0.113.0/24", true, false)]
    [InlineData("disabled-unrelated", "backbone-v2", "ether88", "10.254.0.0/30", true, true)]
    public async Task UnknownTemplate_Aborts_WithZeroDeletes(
        string _,
        string area,
        string interfaces,
        string networks,
        bool passive,
        bool disabled)
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        Dictionary<string, string> unknown = Row(
            "*U",
            area,
            interfaces,
            networks,
            passive: passive,
            disabled: disabled,
            type: passive ? null : "ptp");
        store.Templates.Add(unknown);

        MutableTemplateConnection capturing = store.Connect();
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("*U", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
        Assert.Equal(4, store.Templates.Count);
    }

    [Fact]
    public async Task AmbiguousBackhaul_Aborts_WithZeroDeletes()
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row(
            "*DUP",
            plan.ActiveBackhauls[0].AreaName,
            plan.ActiveBackhauls[0].Interface,
            plan.ActiveBackhauls[0].NetworkCidr,
            passive: false,
            disabled: false,
            type: "ptp",
            cost: "10"));

        MutableTemplateConnection capturing = store.Connect();
        OspfBackboneMutationGuardException ex = await Assert.ThrowsAsync<OspfBackboneMutationGuardException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
    }

    [Fact]
    public void Classifier_IdentifiesUnknown_AndKnownLegacy()
    {
        OspfMigrationPlan plan = MinimalPlan();
        List<OspfTemplateInventoryRow> rows =
        [
            InventoryFrom(PlannedBackhaul(plan)),
            InventoryFrom(PlannedLoopback(plan)),
            InventoryFrom(PlannedSite(plan)),
            InventoryFrom(Row("*L1", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false)),
            InventoryFrom(Row("*U1", "backbone-v2", "ether99", "10.255.0.0/30", passive: false, disabled: false, type: "ptp")),
        ];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, rows));
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("*U1", ex.Message, StringComparison.Ordinal);

        List<OspfTemplateInventoryRow> legacyOnly =
        [
            InventoryFrom(PlannedBackhaul(plan)),
            InventoryFrom(PlannedLoopback(plan)),
            InventoryFrom(PlannedSite(plan)),
            InventoryFrom(Row("*L1", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false)),
            InventoryFrom(Row("*L2", "site", "vlan20", "10.16.11.0/27", passive: true, disabled: true)),
        ];
        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, legacyOnly);
        Assert.Equal(2, report.KnownLegacy.Count);
        Assert.Empty(report.Unknown);
        Assert.Single(report.Backhauls);
        Assert.Single(report.Loopbacks);
        Assert.Single(report.SiteMatchers);
        Assert.All(report.KnownLegacy, l => Assert.Equal(OspfTemplateClassification.KnownLegacy, l.Classification));
    }

    [Fact]
    public void Classifier_UnrelatedPrefixOutsideConsolidated_IsUnknown()
    {
        OspfMigrationPlan plan = MinimalPlan(includeLegacyProvenance: false);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfTemplateCleanupClassifier.ClassifyOrThrow(
                plan,
                [
                    InventoryFrom(PlannedBackhaul(plan)),
                    InventoryFrom(PlannedLoopback(plan)),
                    InventoryFrom(PlannedSite(plan)),
                    InventoryFrom(Row("*OP", "site", "VLAN-200", "192.0.2.0/24", passive: true, disabled: false)),
                ]));
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("*OP", ex.Message, StringComparison.Ordinal);
        Assert.Contains("consolidated-prefix replacement", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classifier_NeverClassifiesProtectedRolesAsLegacy()
    {
        OspfMigrationPlan plan = MinimalPlan();
        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(
            plan,
            [
                InventoryFrom(PlannedBackhaul(plan)),
                InventoryFrom(PlannedLoopback(plan)),
                InventoryFrom(PlannedSite(plan)),
            ]);

        Assert.DoesNotContain(report.Items, i => i.Classification == OspfTemplateClassification.KnownLegacy);
        Assert.Equal("*1", Assert.Single(report.Backhauls).Row.Id);
        Assert.Equal("*9", Assert.Single(report.Loopbacks).Row.Id);
        Assert.Equal("*A", Assert.Single(report.SiteMatchers).Row.Id);
    }

    [Theory]
    [InlineData("missing-backhaul")]
    [InlineData("duplicate-backhaul")]
    [InlineData("missing-loopback")]
    [InlineData("duplicate-loopback")]
    [InlineData("missing-site")]
    [InlineData("duplicate-site")]
    public async Task MissingOrDuplicateProtectedRole_Aborts_WithZeroDeletes(string scenario)
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);

        switch (scenario)
        {
            case "missing-backhaul":
                store.Templates.RemoveAll(t => t[".id"] == "*1");
                break;
            case "duplicate-backhaul":
                store.Templates.Add(Row("*1b", plan.ActiveBackhauls[0].AreaName, plan.ActiveBackhauls[0].Interface,
                    plan.ActiveBackhauls[0].NetworkCidr, false, false, "ptp", "10"));
                break;
            case "missing-loopback":
                store.Templates.RemoveAll(t => t[".id"] == "*9");
                break;
            case "duplicate-loopback":
                store.Templates.Add(Row("*9b", plan.PassiveLoopbacks[0].AreaName, plan.PassiveLoopbacks[0].Interface ?? "Loopback",
                    plan.PassiveLoopbacks[0].NetworkCidr, true, false));
                break;
            case "missing-site":
                store.Templates.RemoveAll(t => t[".id"] == "*A");
                break;
            case "duplicate-site":
                store.Templates.Add(Row("*Ab", plan.PassiveSiteNetworks[0].AreaName, "all",
                    plan.PassiveSiteNetworks[0].NetworkCidr, true, false));
                break;
        }

        MutableTemplateConnection capturing = store.Connect();
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
    }

    [Fact]
    public async Task KnownLegacy_MixedEnabledDisabled_DeletedWithFreshIds()
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row("*L1", "site", "Bridge-VLAN-10", "10.16.11.0/27", passive: true, disabled: false));
        store.Templates.Add(Row("*L2", "site", "Bridge-VLAN-20", "10.16.11.0/27", passive: true, disabled: true));
        store.Templates.Add(Row("*L3", "site", "", "10.16.11.0/24", passive: true, disabled: false));

        MutableTemplateConnection capturing = store.Connect();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(3, capturing.RemovedIds.Count);
        Assert.Contains("*L1", capturing.RemovedIds);
        Assert.Contains("*L2", capturing.RemovedIds);
        Assert.Contains("*L3", capturing.RemovedIds);
        Assert.DoesNotContain("*1", capturing.RemovedIds);
        Assert.DoesNotContain("*9", capturing.RemovedIds);
        Assert.DoesNotContain("*A", capturing.RemovedIds);
        Assert.Equal(3, store.Templates.Count);
        Assert.True(capturing.PrintCount >= 1 + (3 * 2) + 1); // initial + (refresh+verify)*3 + final
        Assert.All(store.Templates, t => Assert.Contains(t[".id"], (string[])["*1", "*9", "*A"]));
    }

    [Fact]
    public async Task PartialDeletionFailure_RecoversOnSubsequentRun()
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row("*L1", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false));
        store.Templates.Add(Row("*L2", "site", "vlan20", "10.16.11.0/27", passive: true, disabled: false));
        store.Templates.Add(Row("*L3", "site", "vlan30", "10.16.11.0/27", passive: true, disabled: false));

        MutableTemplateConnection capturing = store.Connect();
        capturing.FailRemoveAtOrdinal = 2; // second deletion fails

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Single(capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] is "*1" or "*9" or "*A");
        Assert.Equal(3, store.Templates.Count(t => t[".id"] is "*1" or "*9" or "*A"));
        Assert.Equal(2, store.Templates.Count(t => t[".id"] is "*L1" or "*L2" or "*L3"));
        Assert.DoesNotContain(capturing.RemovedIds, id => id is "*1" or "*9" or "*A");

        capturing.FailRemoveAtOrdinal = null;
        capturing.RemovedIds.Clear();
        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(2, capturing.RemovedIds.Count);
        Assert.Equal(3, store.Templates.Count);
        Assert.All(store.Templates, t => Assert.Contains(t[".id"], (string[])["*1", "*9", "*A"]));
    }

    [Fact]
    public async Task InventoryMutationBetweenRefresh_AbortsWithoutDeletingChangedCandidate()
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row("*L1", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false));

        MutableTemplateConnection capturing = store.Connect();
        capturing.BeforePrint = printOrdinal =>
        {
            // After initial classification (print 1), mutate legacy before the pre-delete refresh (print 2).
            if (printOrdinal == 2)
            {
                Dictionary<string, string>? legacy = store.Templates.FirstOrDefault(t => t[".id"] == "*L1");
                if (legacy is not null)
                {
                    legacy["networks"] = "198.51.100.0/24"; // no longer covered → Unknown / not legacy
                }
            }
        };

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*L1");
    }

    [Fact]
    public async Task CandidateIdChange_ResolvedViaFingerprint_UsesFreshId()
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row("*OLD", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false));

        MutableTemplateConnection capturing = store.Connect();
        capturing.BeforePrint = printOrdinal =>
        {
            if (printOrdinal == 2)
            {
                Dictionary<string, string>? legacy = store.Templates.FirstOrDefault(t => t[".id"] == "*OLD");
                if (legacy is not null)
                {
                    legacy[".id"] = "*NEW";
                }
            }
        };

        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);

        Assert.Equal(["*NEW"], capturing.RemovedIds);
        Assert.DoesNotContain("*OLD", capturing.RemovedIds);
        Assert.Equal(3, store.Templates.Count);
    }

    [Fact]
    public async Task CandidateDisappearsBeforeDelete_IsSkippedSafely()
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row("*L1", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false));

        MutableTemplateConnection capturing = store.Connect();
        capturing.BeforePrint = printOrdinal =>
        {
            if (printOrdinal == 2)
            {
                store.Templates.RemoveAll(t => t[".id"] == "*L1");
            }
        };

        await CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);
        Assert.Empty(capturing.RemovedIds);
        Assert.Equal(3, store.Templates.Count);
    }

    [Fact]
    public async Task DuplicateCandidateAppears_AbortsWithoutDelete()
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row("*L1", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false));

        MutableTemplateConnection capturing = store.Connect();
        capturing.BeforePrint = printOrdinal =>
        {
            if (printOrdinal == 2)
            {
                store.Templates.Add(Row("*L1b", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false));
            }
        };

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("matches 2 live rows", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(capturing.RemovedIds);
    }

    [Fact]
    public async Task ThreeConsecutiveCleanupRuns_AreIdempotent()
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row("*L1", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false));
        store.Templates.Add(Row("*L2", "site", "vlan20", "10.16.11.0/27", passive: true, disabled: true));

        MutableTemplateConnection capturing = store.Connect();
        OspfMikrotikOperations ops = CreateOps();

        await ops.RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);
        string[] idsAfterFirst = store.Templates.Select(t => t[".id"]).OrderBy(static x => x).ToArray();
        Assert.Equal(3, idsAfterFirst.Length);
        Assert.Equal(2, capturing.RemovedIds.Count);

        capturing.RemovedIds.Clear();
        int printsAfterFirst = capturing.PrintCount;
        await ops.RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);
        Assert.Empty(capturing.RemovedIds);
        Assert.Equal(idsAfterFirst, store.Templates.Select(t => t[".id"]).OrderBy(static x => x).ToArray());
        Assert.True(capturing.PrintCount > printsAfterFirst);

        capturing.RemovedIds.Clear();
        await ops.RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None);
        Assert.Empty(capturing.RemovedIds);
        Assert.Equal(idsAfterFirst, store.Templates.Select(t => t[".id"]).OrderBy(static x => x).ToArray());
        Assert.Equal(3, store.Templates.Count);
        Assert.DoesNotContain(store.Templates, t => t.TryGetValue("disabled", out string? d) && d is "true" or "yes");
    }

    [Fact]
    public async Task ThreeConsecutiveReconcilerRuns_NoFurtherDeletesOrAdds()
    {
        OspfMigrationPlan plan = MinimalPlan();
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row("*L1", "site", "vlan10", "10.16.11.0/27", passive: true, disabled: false));

        MutableTemplateConnection capturing = store.Connect();
        RecordingTemplateOps ops = new(CreateOps());
        OspfV7Reconciler reconciler = new(ops, NullLogger<OspfV7Reconciler>.Instance);

        await reconciler.ApplyAsync(capturing, plan, managementHost: "198.18.240.11", CancellationToken.None);
        Assert.Contains("*L1", capturing.RemovedIds);
        string[] stableIds = store.Templates.Select(t => t[".id"]).OrderBy(static x => x).ToArray();
        Assert.Equal(3, stableIds.Length);

        capturing.RemovedIds.Clear();
        int addsAfterFirst = capturing.AddCount;
        int setsAfterFirst = capturing.SetCount;
        int unsetsAfterFirst = capturing.UnsetCount;
        await reconciler.ApplyAsync(capturing, plan, managementHost: "198.18.240.11", CancellationToken.None);
        Assert.Empty(capturing.RemovedIds);
        Assert.Equal(addsAfterFirst, capturing.AddCount);
        Assert.Equal(setsAfterFirst, capturing.SetCount);
        Assert.Equal(unsetsAfterFirst, capturing.UnsetCount);
        Assert.Equal(stableIds, store.Templates.Select(t => t[".id"]).OrderBy(static x => x).ToArray());

        capturing.RemovedIds.Clear();
        await reconciler.ApplyAsync(capturing, plan, managementHost: "198.18.240.11", CancellationToken.None);
        Assert.Empty(capturing.RemovedIds);
        Assert.Equal(addsAfterFirst, capturing.AddCount);
        Assert.Equal(setsAfterFirst, capturing.SetCount);
        Assert.Equal(unsetsAfterFirst, capturing.UnsetCount);
        Assert.Equal(stableIds, store.Templates.Select(t => t[".id"]).OrderBy(static x => x).ToArray());
    }

    [Fact]
    public async Task OperatorManagedUnrelatedPrefix_AbortsCleanup()
    {
        OspfMigrationPlan plan = MinimalPlan(includeLegacyProvenance: false);
        MutableTemplateStore store = MutableTemplateStore.WithPlanned(plan);
        store.Templates.Add(Row("*OP", "site", "VLAN-200", "192.0.2.0/24", passive: true, disabled: false));
        MutableTemplateConnection capturing = store.Connect();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateOps().RemoveObsoleteTemplatesAsync(capturing, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemovedIds);
        Assert.Contains(store.Templates, t => t[".id"] == "*OP");
    }

    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    private static OspfSupersededTemplateIdentity Sem(
        string? interfaces,
        string networks,
        bool disabled = false) =>
        OspfTemplateSemanticIdentity.FromCaptured(
            "site",
            interfaces,
            networks,
            passive: true,
            new OspfCapturedTemplateSemantics
            {
                Disabled = disabled,
                AreaInstanceName = "default-v2",
            });

    private static OspfMigrationPlan MinimalPlan(bool includeLegacyProvenance = true) => new()
    {
        ImportId = Guid.NewGuid(),
        JobId = "job",
        RouterIp = "198.18.240.11",
        Identity = "LAB.Router",
        SourceRouterOsVersion = "7.23.8",
        Architecture = "mipsbe",
        SourceKind = OspfCaptureSourceKind.RouterOsV7,
        InstanceName = "default-v2",
        RouterId = "10.16.0.11",
        TargetInstanceName = OspfFleetNaming.Ros7InstanceName,
        TargetBackboneAreaName = OspfFleetNaming.Ros7BackboneAreaName,
        // Lab cleanup fixtures keep logical live area name "site"; pin target to avoid remap churn.
        TargetSiteAreaName = "site",
        LoopbackInterfaceName = "Loopback",
        LoopbackCidr = "10.16.0.11/32",
        DerivedManagementCidr = "10.16.11.0/24",
        Areas =
        [
            new OspfPlanArea
            {
                Name = "backbone-v2",
                AreaId = "0.0.0.0",
                TargetType = "default",
                IsBackbone = true,
            },
            new OspfPlanArea
            {
                Name = "site",
                AreaId = "10.16.0.11",
                TargetType = "stub",
                DefaultCost = "1",
                NoSummaries = false,
                IsBackbone = false,
            },
        ],
        ActiveBackhauls =
        [
            new OspfPlanBackhaulTemplate
            {
                AreaName = "backbone-v2",
                Interface = "eth-01_BH",
                NetworkCidr = "10.16.248.76/30",
                Cost = 10,
                Type = "ptp",
                Passive = false,
            },
        ],
        PassiveLoopbacks =
        [
            new OspfPlanLoopbackTemplate
            {
                AreaName = "backbone-v2",
                Interface = "Loopback",
                NetworkCidr = "10.16.0.11/32",
                Passive = true,
            },
        ],
        PassiveSiteNetworks =
        [
            new OspfPlanSiteNetworkTemplate
            {
                AreaName = "site",
                Interfaces = "all",
                NetworkCidr = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
                Passive = true,
            },
        ],
        SupersededTemplates = includeLegacyProvenance
            ?
            [
                Sem("Bridge-VLAN-10", "10.16.11.0/27"),
                Sem("Bridge-VLAN-20", "10.16.11.0/27", disabled: true),
                Sem(null, "10.16.11.0/24"),
                Sem("vlan10", "10.16.11.0/27"),
                Sem("vlan20", "10.16.11.0/27", disabled: true),
                Sem("vlan20", "10.16.11.0/27"),
                Sem("vlan30", "10.16.11.0/27"),
            ]
            : [],
        AreaRanges =
        [
            new OspfPlanAreaRange { AreaName = "site", RangeCidr = "10.16.11.0/24", Comment = "MGMT" },
        ],
        ValidationErrors = [],
        NormalisationNotes = [],
        ContentHash = "test-cleanup",
    };

    private static Dictionary<string, string> PlannedBackhaul(OspfMigrationPlan plan) =>
        Row(
            "*1",
            plan.ActiveBackhauls[0].AreaName,
            plan.ActiveBackhauls[0].Interface,
            plan.ActiveBackhauls[0].NetworkCidr,
            passive: false,
            disabled: false,
            type: "ptp",
            cost: "10");

    private static Dictionary<string, string> PlannedLoopback(OspfMigrationPlan plan) =>
        Row(
            "*9",
            plan.PassiveLoopbacks[0].AreaName,
            plan.PassiveLoopbacks[0].Interface ?? "Loopback",
            plan.PassiveLoopbacks[0].NetworkCidr,
            passive: true,
            disabled: false);

    private static Dictionary<string, string> PlannedSite(OspfMigrationPlan plan) =>
        Row(
            "*A",
            plan.PassiveSiteNetworks[0].AreaName,
            "all",
            plan.PassiveSiteNetworks[0].NetworkCidr,
            passive: true,
            disabled: false);

    private static Dictionary<string, string> Row(
        string id,
        string area,
        string interfaces,
        string networks,
        bool passive,
        bool disabled,
        string? type = null,
        string? cost = null)
    {
        Dictionary<string, string> d = new()
        {
            [".id"] = id,
            ["area"] = area,
            ["networks"] = networks,
            ["disabled"] = disabled ? "true" : "false",
        };
        if (!string.IsNullOrEmpty(interfaces))
        {
            d["interfaces"] = interfaces;
        }

        if (passive)
        {
            d["passive"] = "";
        }

        if (type is not null)
        {
            d["type"] = type;
        }

        if (cost is not null)
        {
            d["cost"] = cost;
        }

        return d;
    }

    private static OspfTemplateInventoryRow InventoryFrom(Dictionary<string, string> row) =>
        new()
        {
            Id = row.GetValueOrDefault(".id"),
            Area = row.GetValueOrDefault("area"),
            Interfaces = row.GetValueOrDefault("interfaces"),
            Networks = row.GetValueOrDefault("networks"),
            Type = row.GetValueOrDefault("type"),
            Cost = row.GetValueOrDefault("cost"),
            Passive = row.ContainsKey("passive"),
            Disabled = row.TryGetValue("disabled", out string? d)
                && d is "true" or "yes" or "1",
            AreaInstanceName = "default-v2",
        };

    private sealed class MutableTemplateStore
    {
        public List<Dictionary<string, string>> Templates { get; } = [];

        public static MutableTemplateStore WithPlanned(OspfMigrationPlan plan)
        {
            MutableTemplateStore store = new();
            store.Templates.Add(PlannedBackhaul(plan));
            store.Templates.Add(PlannedLoopback(plan));
            store.Templates.Add(PlannedSite(plan));
            return store;
        }

        public MutableTemplateConnection Connect()
        {
            MutableTemplateConnection connection = new(this);
            connection.AttachFakeResponses();
            return connection;
        }
    }

    private sealed class MutableTemplateConnection : DisposeTrackingConnection
    {
        private readonly MutableTemplateStore _store;
        private readonly TikFakeConnection _fake;
        private ITikCommand? _pendingRemove;
        private int _removeOrdinal;

        public MutableTemplateConnection(MutableTemplateStore store)
            : this(store, new TikFakeConnection())
        {
        }

        private MutableTemplateConnection(MutableTemplateStore store, TikFakeConnection fake)
            : base(fake)
        {
            _store = store;
            _fake = fake;
        }

        public List<string> RemovedIds { get; } = [];
        public int PrintCount { get; private set; }
        public int AddCount { get; private set; }
        public int SetCount { get; private set; }
        public int UnsetCount { get; private set; }
        public int? FailRemoveAtOrdinal { get; set; }
        public Action<int>? BeforePrint { get; set; }

        public void AttachFakeResponses()
        {
            _fake.WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/print",
                    _ =>
                    {
                        PrintCount++;
                        BeforePrint?.Invoke(PrintCount);
                        return
                        [
                            .._store.Templates.Select(static row =>
                                new TikFakeReSentence(new Dictionary<string, string>(row))),
                            new TikFakeDoneSentence(),
                        ];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/remove",
                    _ =>
                    {
                        _removeOrdinal++;
                        string? id = _pendingRemove?.Parameters
                            .FirstOrDefault(static p => p.Name == ".id")
                            ?.Value;
                        if (FailRemoveAtOrdinal is int failAt && failAt == _removeOrdinal)
                        {
                            throw new InvalidOperationException(
                                $"Simulated remove failure for .id={id} at ordinal {_removeOrdinal}.");
                        }

                        if (string.IsNullOrWhiteSpace(id))
                        {
                            throw new InvalidOperationException("Remove invoked without .id.");
                        }

                        RemovedIds.Add(id);
                        _store.Templates.RemoveAll(t =>
                            string.Equals(t[".id"], id, StringComparison.Ordinal));
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/add",
                    _ =>
                    {
                        AddCount++;
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/set",
                    _ =>
                    {
                        SetCount++;
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface-template/unset",
                    _ =>
                    {
                        UnsetCount++;
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/instance/print",
                    static _ =>
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*i",
                            ["name"] = "default-v2",
                            ["version"] = "2",
                            ["router-id"] = "10.16.0.11",
                        }),
                        new TikFakeDoneSentence(),
                    ])
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
                            ["name"] = "site",
                            ["instance"] = "default-v2",
                        }),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/area/range/print",
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First().StartsWith("/routing/filter", StringComparison.Ordinal),
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/ip/route/print",
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/neighbor/print",
                    static _ => [new TikFakeDoneSentence()])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/interface/print",
                    static _ => [new TikFakeDoneSentence()]);
        }

        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        private void Track(string commandText, ITikCommand cmd)
        {
            if (commandText.Equals("/routing/ospf/interface-template/remove", StringComparison.Ordinal))
            {
                _pendingRemove = cmd;
            }
        }
    }

    /// <summary>
    /// Delegates template ensure/cleanup to real ops; stubs management-plane / filter stages for reconciler runs.
    /// </summary>
    private sealed class RecordingTemplateOps(OspfMikrotikOperations real) : IOspfMikrotikOperations
    {
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) =>
            real.EnsureBackhaulTemplateAsync(connection, template, cancellationToken);

        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            real.EnsureLoopbackTemplateAsync(connection, template, protectedBackhauls, cancellationToken);

        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            real.EnsureSiteNetworkTemplateAsync(connection, template, protectedBackhauls, cancellationToken);


        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            real.AssertSiteNetworkTemplateStateAsync(connection, template, cancellationToken);
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            real.RemoveObsoleteTemplatesAsync(connection, plan, cancellationToken);

        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.InventoryMatchingPlan(MinimalPlan()));
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));
    }
}
