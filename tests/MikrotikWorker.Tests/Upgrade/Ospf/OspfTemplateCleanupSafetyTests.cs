// -----------------------------------------------------------------------------
// File: OspfTemplateCleanupSafetyTests.cs
//
// Description:
// Fake-based coverage for OSPF interface-template cleanup classification,
// pre-deletion safety gate, fresh .id resolution, partial-failure recovery,
// inventory mutation abort, and cleanup idempotency.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Fake-based coverage for OSPF interface-template cleanup classification, pre-deletion safety gate,
/// fresh .id resolution, partial-failure recovery, inventory mutation abort, and cleanup idempotency.
/// </summary>
public sealed class OspfTemplateCleanupSafetyTests
{
    /// <summary>
    /// Scenario: one unrelated live template (<c>*U</c>) is added beside the planned templates: unexpected area, unrelated network, unrelated active or passive, or a disabled unrelated template.
    /// Expected: <c>RemoveObsoleteTemplatesAsync</c> throws <c>InvalidOperationException</c> mentioning "No deletion was attempted" and <c>*U</c>; no ids are removed and all four templates remain.
    /// Invariant: a single unrecognised template aborts cleanup before any deletion, whatever its flags.
    /// </summary>
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

    /// <summary>
    /// Scenario: a second live template (<c>*DUP</c>) matches the planned backhaul's area, interface and network.
    /// Expected: <c>OspfBackboneMutationGuardException</c> mentioning "No deletion was attempted", and no ids are removed.
    /// Invariant: an ambiguous backbone template is never cleaned up around.
    /// </summary>
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

    /// <summary>
    /// Scenario: the plan is classified against the three planned rows plus legacy rows; first with an unrelated <c>*U1</c> on ether99, then with two legacy 10.16.11.0/27 rows (one enabled, one disabled).
    /// Expected: the first run throws <c>InvalidOperationException</c> naming <c>*U1</c> and "No deletion was attempted"; the second reports two known legacy rows, nothing unknown, and one backhaul, loopback and site matcher.
    /// Invariant: the classifier separates removable legacy rows from unknown ones, and any unknown aborts the whole run.
    /// </summary>
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

    /// <summary>
    /// Scenario: the plan has no legacy provenance, and the live inventory has an extra 192.0.2.0/24 template (<c>*OP</c>) in the site area.
    /// Expected: classification throws <c>InvalidOperationException</c> mentioning "No deletion was attempted", <c>*OP</c> and "consolidated-prefix replacement".
    /// Invariant: without provenance, only prefixes inside the consolidated prefix can be treated as removable.
    /// </summary>
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

    /// <summary>
    /// Scenario: the inventory contains only the planned backhaul (<c>*1</c>), loopback (<c>*9</c>) and site matcher (<c>*A</c>).
    /// Expected: no item is known legacy, and each role maps to its single planned row.
    /// Invariant: the backhaul, loopback and site matcher are protected and never marked for deletion.
    /// </summary>
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

    /// <summary>
    /// Scenario: the live store is missing or duplicates the planned backhaul, loopback or site matcher.
    /// Expected: <c>RemoveObsoleteTemplatesAsync</c> throws an exception mentioning "No deletion was attempted", and no ids are removed.
    /// Invariant: cleanup runs only when each protected role exists exactly once.
    /// </summary>
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

    /// <summary>
    /// Scenario: three legacy templates (one enabled /27, one disabled /27, one /24 with no interface) sit beside the planned templates.
    /// Expected: exactly <c>*L1</c>, <c>*L2</c> and <c>*L3</c> are removed, the protected <c>*1</c>, <c>*9</c> and <c>*A</c> remain, and each deletion is preceded by a fresh print (at least 1 + 3*2 + 1 prints).
    /// Invariant: legacy templates are deleted regardless of enabled state, with each id re-checked against fresh state first.
    /// </summary>
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

    /// <summary>
    /// Scenario: three legacy templates exist and the second deletion fails.
    /// Expected: the first run throws <c>InvalidOperationException</c> with one id removed and the protected templates intact; a second run removes the remaining two, leaving only <c>*1</c>, <c>*9</c> and <c>*A</c>.
    /// Invariant: a partial failure never touches protected templates and is recoverable by re-running.
    /// </summary>
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

    /// <summary>
    /// Scenario: after the first classification print, the legacy <c>*L1</c> has its network changed to 198.51.100.0/24 before the pre-delete refresh.
    /// Expected: <c>InvalidOperationException</c> mentioning "No deletion was attempted"; no ids are removed and <c>*L1</c> remains.
    /// Invariant: a candidate that changes between classification and delete is re-evaluated, and an unrecognised result aborts instead of deleting.
    /// </summary>
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

    /// <summary>
    /// Scenario: between classification and the refresh, the legacy template's id changes from <c>*OLD</c> to <c>*NEW</c> with identical content.
    /// Expected: only <c>*NEW</c> is removed, never <c>*OLD</c>, and three templates remain.
    /// Invariant: candidates are tracked by semantic fingerprint, and deletes use the fresh id.
    /// </summary>
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

    /// <summary>
    /// Scenario: the legacy template <c>*L1</c> disappears from the live store before the pre-delete refresh.
    /// Expected: cleanup succeeds without removing anything, leaving three templates.
    /// Invariant: an already-deleted candidate is skipped rather than failing or deleting by a stale id.
    /// </summary>
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

    /// <summary>
    /// Scenario: a second row with the same content (<c>*L1b</c>) appears for the legacy candidate before the refresh.
    /// Expected: <c>InvalidOperationException</c> mentioning "matches 2 live rows", and no ids are removed.
    /// Invariant: a candidate must match exactly one live row to be deleted.
    /// </summary>
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

    /// <summary>
    /// Scenario: cleanup runs three times against a store with two legacy templates (one disabled).
    /// Expected: the first run removes two ids and leaves three templates; the second and third remove nothing, keep the same ids, and the later runs still print inventory.
    /// Invariant: cleanup is idempotent and converges on the three protected templates.
    /// </summary>
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

    /// <summary>
    /// Scenario: the full reconciler applies the plan three times to a store with one legacy template.
    /// Expected: the first run removes <c>*L1</c> and leaves three templates; the second and third runs remove nothing and issue no further add, set or unset commands, with ids unchanged.
    /// Invariant: re-applying a converged plan makes no RouterOS changes.
    /// </summary>
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

    /// <summary>
    /// Scenario: the plan has no legacy provenance and the live site area holds an operator-managed 192.0.2.0/24 template (<c>*OP</c>).
    /// Expected: <c>InvalidOperationException</c> mentioning "No deletion was attempted"; no ids are removed and <c>*OP</c> remains.
    /// Invariant: operator-managed templates outside the consolidated prefix are never deleted.
    /// </summary>
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

    /// <summary>
    /// Creates the real <c>OspfMikrotikOperations</c> with a null logger.
    /// </summary>
    private static OspfMikrotikOperations CreateOps() =>
        new(NullLogger<OspfMikrotikOperations>.Instance);

    /// <summary>
    /// Builds a superseded-template identity (provenance) for a passive site template in
    /// <c>default-v2</c>. Only rows matching these captured identities may be classified
    /// known-legacy and deleted; anything else is unknown and aborts cleanup.
    /// </summary>
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

    /// <summary>
    /// Lab cleanup fixtures keep logical live area name "site"; pin target to avoid remap churn.
    /// </summary>
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

    /// <summary>
    /// Live row for the protected planned backhaul (<c>*1</c>, active ptp cost 10); it must never
    /// be classified as legacy or deleted.
    /// </summary>
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

    /// <summary>
    /// Builds a raw template row for the plan's first passive loopback template.
    /// </summary>
    private static Dictionary<string, string> PlannedLoopback(OspfMigrationPlan plan) =>
        Row(
            "*9",
            plan.PassiveLoopbacks[0].AreaName,
            plan.PassiveLoopbacks[0].Interface ?? "Loopback",
            plan.PassiveLoopbacks[0].NetworkCidr,
            passive: true,
            disabled: false);

    /// <summary>
    /// Live row for the planned consolidated site matcher (<c>*A</c>, interfaces=all, passive); a
    /// protected role that must survive cleanup.
    /// </summary>
    private static Dictionary<string, string> PlannedSite(OspfMigrationPlan plan) =>
        Row(
            "*A",
            plan.PassiveSiteNetworks[0].AreaName,
            "all",
            plan.PassiveSiteNetworks[0].NetworkCidr,
            passive: true,
            disabled: false);

    /// <summary>
    /// Builds a raw interface-template row dictionary with the given id, area, interfaces, networks, passive and
    /// disabled flags, adding optional type and cost.
    /// </summary>
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

    /// <summary>
    /// Converts a raw RouterOS row dictionary into the typed inventory row the classifier consumes:
    /// passive means the key is present and disabled is parsed from its textual flag.
    /// </summary>
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

    /// <summary>
    /// In-memory interface-template table seeded with the plan's protected backhaul, loopback and site templates,
    /// to which tests add legacy or unknown rows.
    /// </summary>
    private sealed class MutableTemplateStore
    {
        /// <summary>
        /// In-memory <c>/routing/ospf/interface-template</c> table that the fake connection prints
        /// from and removes rows out of.
        /// </summary>
        public List<Dictionary<string, string>> Templates { get; } = [];

        /// <summary>
        /// Creates a store containing exactly the plan's protected backhaul, loopback and site
        /// templates so tests add legacy or unknown rows on top.
        /// </summary>
        public static MutableTemplateStore WithPlanned(OspfMigrationPlan plan)
        {
            MutableTemplateStore store = new();
            store.Templates.Add(PlannedBackhaul(plan));
            store.Templates.Add(PlannedLoopback(plan));
            store.Templates.Add(PlannedSite(plan));
            return store;
        }

        /// <summary>
        /// Creates a connection bound to this store with fake responses already attached.
        /// </summary>
        public MutableTemplateConnection Connect()
        {
            MutableTemplateConnection connection = new(this);
            connection.AttachFakeResponses();
            return connection;
        }
    }

    /// <summary>
    /// Stateful connection fake backed by a template store that serves template print, remove, add and set and the
    /// supporting reads, records removals and counts, and can fail a chosen remove or run a hook before each print.
    /// </summary>
    private sealed class MutableTemplateConnection : DisposeTrackingConnection
    {
        /// <summary>
        /// Backing template store mutated by the scripted <c>remove</c> handler.
        /// </summary>
        private readonly MutableTemplateStore _store;

        /// <summary>
        /// Underlying fake connection on which <see cref="AttachFakeResponses"/> registers
        /// handlers.
        /// </summary>
        private readonly TikFakeConnection _fake;

        /// <summary>
        /// Most recent <c>remove</c> command; its <c>.id</c> parameter is added after creation, so
        /// the handler reads it from here.
        /// </summary>
        private ITikCommand? _pendingRemove;

        /// <summary>
        /// 1-based count of remove executions, compared with <see cref="FailRemoveAtOrdinal"/> to
        /// inject a failure at a chosen deletion.
        /// </summary>
        private int _removeOrdinal;

        /// <summary>
        /// Creates a connection over a new fake; call <see cref="AttachFakeResponses"/> to wire the
        /// handlers.
        /// </summary>
        public MutableTemplateConnection(MutableTemplateStore store)
            : this(store, new TikFakeConnection())
        {
        }

        /// <summary>
        /// Chains the supplied fake into the base class while keeping a typed reference for handler
        /// registration.
        /// </summary>
        private MutableTemplateConnection(MutableTemplateStore store, TikFakeConnection fake)
            : base(fake)
        {
            _store = store;
            _fake = fake;
        }

        /// <summary>
        /// Ids of templates actually removed, in order. Empty asserts the zero-delete abort
        /// guarantee; exact contents assert that fresh ids were used.
        /// </summary>
        public List<string> RemovedIds { get; } = [];

        /// <summary>
        /// Number of interface-template prints. Cleanup must reprint before each delete, so this
        /// proves the pre-delete refresh and post-delete verification happen.
        /// </summary>
        public int PrintCount { get; private set; }

        /// <summary>
        /// Number of template adds; must not grow across repeated reconciliations (idempotence).
        /// </summary>
        public int AddCount { get; private set; }

        /// <summary>
        /// Number of template sets; must not grow across repeated reconciliations (idempotence).
        /// </summary>
        public int SetCount { get; private set; }

        /// <summary>
        /// Number of template unsets; must not grow across repeated reconciliations (idempotence).
        /// </summary>
        public int UnsetCount { get; private set; }

        /// <summary>
        /// 1-based remove ordinal at which the fake throws, simulating a partial deletion failure
        /// that a later run must recover from.
        /// </summary>
        public int? FailRemoveAtOrdinal { get; set; }

        /// <summary>
        /// Hook run at the start of every template print with its 1-based ordinal. It lets tests
        /// mutate the router between classification (print 1) and the pre-delete refresh (print 2)
        /// to model concurrent operator edits.
        /// </summary>
        public Action<int>? BeforePrint { get; set; }

        /// <summary>
        /// Registers stateful handlers for template print/remove/add/set/unset plus the instance,
        /// area, filter, route and neighbour reads reconciler runs need, so cleanup and full
        /// reconciliation operate on realistic data.
        /// </summary>
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

        /// <summary>
        /// Tracks template remove commands, then returns the inner command.
        /// </summary>
        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            Track(commandText, cmd);
            return cmd;
        }

        /// <summary>
        /// Overload with an explicit parameter format; tracks identically.
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
        /// Remembers the pending template <c>remove</c> command so its <c>.id</c> can be read when
        /// the handler runs.
        /// </summary>
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
        /// <summary>
        /// Delegates to the real operations so reconciler runs exercise genuine ensure logic (and
        /// its idempotence) against the fake connection.
        /// </summary>
        public Task EnsureBackhaulTemplateAsync(ITikConnection connection, OspfPlanBackhaulTemplate template, CancellationToken cancellationToken = default) =>
            real.EnsureBackhaulTemplateAsync(connection, template, cancellationToken);

        /// <summary>
        /// Delegates to the real loopback ensure, including protected-backhaul handling.
        /// </summary>
        public Task EnsureLoopbackTemplateAsync(ITikConnection connection, OspfPlanLoopbackTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            real.EnsureLoopbackTemplateAsync(connection, template, protectedBackhauls, cancellationToken);

        /// <summary>
        /// Delegates to the real site-matcher ensure, including protected-backhaul handling.
        /// </summary>
        public Task EnsureSiteNetworkTemplateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls, CancellationToken cancellationToken = default) =>
            real.EnsureSiteNetworkTemplateAsync(connection, template, protectedBackhauls, cancellationToken);


        /// <summary>
        /// Delegates to the real site read-back assertion.
        /// </summary>
        public Task AssertSiteNetworkTemplateStateAsync(ITikConnection connection, OspfPlanSiteNetworkTemplate template, CancellationToken cancellationToken = default) =>
            real.AssertSiteNetworkTemplateStateAsync(connection, template, cancellationToken);

        /// <summary>
        /// Delegates to the real cleanup, the code path whose safety gate and idempotence these
        /// tests lock down.
        /// </summary>
        public Task RemoveObsoleteTemplatesAsync(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) =>
            real.RemoveObsoleteTemplatesAsync(connection, plan, cancellationToken);

        /// <summary>
        /// Returns the Tommy lab baseline instead of querying a router, keeping the
        /// management-plane stage deterministic.
        /// </summary>
        public Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(ITikConnection connection, OspfMigrationPlan plan, string managementHost, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.Tommy(managementHost));

        /// <summary>
        /// No-op: the backbone guard is stubbed so cleanup behaviour can be tested in isolation.
        /// </summary>
        public Task AssertBackboneStillActiveAsync(ITikConnection connection, OspfMigrationPlan plan, OspfManagementPlaneBaseline baseline, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// No-op: routing-filter removal is outside this scenario, so the fake completes without
        /// recording or failing.
        /// </summary>
        public Task RemoveAllRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: clearing instance in-filter/out-filter references is not under test in this
        /// scenario.
        /// </summary>
        public Task ClearOspfInstanceFilterReferencesAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: redistribution clearing is not under test in this scenario.
        /// </summary>
        public Task ClearOspfRedistributionAsync(ITikConnection connection, string instanceName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: the router-id in the fake inventory is already correct, so no router-id write is
        /// modelled.
        /// </summary>
        public Task SetInstanceRouterIdAsync(ITikConnection connection, string instanceName, string routerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: area reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaAsync(ITikConnection connection, string instanceName, OspfPlanArea area, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: area-range reconciliation is not under test in this scenario.
        /// </summary>
        public Task EnsureOspfAreaRangeAsync(ITikConnection connection, OspfPlanAreaRange range, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// No-op: OSPFv3 removal is not under test in this scenario.
        /// </summary>
        public Task RemoveOspfV3Async(ITikConnection connection, OspfMigrationPlan plan, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <summary>
        /// Reports zero routing filters so the "routing filters remain" read-back check cannot fail
        /// for reasons unrelated to this scenario.
        /// </summary>
        public Task<int> CountRoutingFiltersAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult(0);

        /// <summary>
        /// Returns an inventory matching <c>MinimalPlan()</c> so identity resolution and
        /// verification pass.
        /// </summary>
        public Task<OspfV7Inventory> ReadV7InventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.InventoryMatchingPlan(MinimalPlan()));

        /// <summary>
        /// Always throws <see cref="NotSupportedException"/>: this fake only supports the v7
        /// reconcile/verify path, never a live capture.
        /// </summary>
        public Task<OspfCaptureSnapshot> CaptureAsync(ITikConnection connection, OspfCaptureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>
        /// Returns no neighbours; the adjacency guard is not exercised by this scenario.
        /// </summary>
        public Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(ITikConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>([]);

        /// <summary>
        /// Returns a single <c>10.16.0.11/32</c> Loopback address so loopback-derived router-id
        /// validation sees one consistent /32.
        /// </summary>
        public Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.FromResult(OspfTestBaselines.LoopbackSlash32("10.16.0.11"));
    }
}
