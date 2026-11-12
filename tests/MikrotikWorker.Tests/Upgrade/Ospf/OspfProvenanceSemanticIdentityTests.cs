// -----------------------------------------------------------------------------
// File: OspfProvenanceSemanticIdentityTests.cs
//
// Description:
// Fake-based coverage for complete semantic provenance matching used by
// KnownLegacy cleanup. Authentication secrets are never asserted or printed as
// plaintext.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Upgrade.Ospf;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Fake-based coverage for complete semantic provenance matching used by KnownLegacy cleanup.
/// Authentication secrets are never asserted or printed as plaintext.
/// </summary>
public sealed class OspfProvenanceSemanticIdentityTests
{
    /// <summary>
    /// Scenario: a live template (id <c>*L</c>) is built from provenance with every semantic property equal (vlan10, 10.99.99.0/27, broadcast, cost 1, priority 128, BFD off, no auth).
    /// Expected: the report holds exactly one known-legacy row and no unknown rows.
    /// Invariant: an exact semantic match is recognised as a known legacy template.
    /// </summary>
    [Fact]
    public void ExactSemanticProvenance_IsKnownLegacy()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(
            interfaces: "vlan10",
            networks: "10.99.99.0/27",
            type: "broadcast",
            cost: "1",
            authId: null,
            authKey: null,
            priority: "128",
            useBfd: false);

        OspfTemplateClassificationReport report = Classify(provenance, LiveFrom(provenance, id: "*L"));
        Assert.Equal(OspfTemplateClassification.KnownLegacy, Assert.Single(report.KnownLegacy).Classification);
        Assert.Empty(report.Unknown);
    }

    /// <summary>
    /// Scenario: the live template matches provenance except for one property: cost, auth-id, priority, type, auth, hello/dead/retransmit interval, transmit-delay, instance-id, area-instance, prefix-list or either virtual-link field.
    /// Expected: the template is classified unknown and classification refuses to proceed.
    /// Invariant: any single semantic difference makes a template not safe to delete.
    /// </summary>
    [Theory]
    [InlineData("cost", "10")]
    [InlineData("auth-id", "1")]
    [InlineData("priority", "50")]
    [InlineData("type", "nbma")]
    [InlineData("auth", "md5")]
    [InlineData("hello-interval", "1s")]
    [InlineData("dead-interval", "3s")]
    [InlineData("retransmit-interval", "2s")]
    [InlineData("transmit-delay", "2s")]
    [InlineData("instance-id", "1")]
    [InlineData("area-instance", "other-v2")]
    [InlineData("prefix-list", "plist1")]
    [InlineData("vlink-neighbor-id", "1.2.3.4")]
    [InlineData("vlink-transit-area", "transit")]
    public void SinglePropertyDifference_IsUnknown(string property, string value)
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.99.99.0/27");
        OspfTemplateInventoryRow live = LiveFrom(provenance, "*L");
        live = property switch
        {
            "cost" => live with { Cost = value },
            "auth-id" => live with { AuthId = value },
            "priority" => live with { Priority = value },
            "type" => live with { Type = value },
            "auth" => live with { Auth = value },
            "hello-interval" => live with { HelloInterval = value },
            "dead-interval" => live with { DeadInterval = value },
            "retransmit-interval" => live with { RetransmitInterval = value },
            "transmit-delay" => live with { TransmitDelay = value },
            "instance-id" => live with { InstanceId = value },
            "area-instance" => live with { AreaInstanceName = value },
            "prefix-list" => live with { PrefixList = value },
            "vlink-neighbor-id" => live with { VlinkNeighborId = value },
            "vlink-transit-area" => live with { VlinkTransitArea = value },
            _ => live,
        };

        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: the live template matches provenance except that an auth key (with fingerprint) is now present.
    /// Expected: the template is classified unknown.
    /// Invariant: the auth key is part of the identity, so adding one alone is enough to block deletion.
    /// </summary>
    [Fact]
    public void AuthKeyOnlyDifference_IsUnknown()
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.99.99.0/27");
        OspfTemplateInventoryRow live = LiveFrom(provenance, "*L") with
        {
            AuthKeyPresent = true,
            AuthKeyFingerprint = OspfTemplateSemanticIdentity.FingerprintAuthKey("secret-value"),
        };

        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: the live template matches provenance except <c>use-bfd</c> is now "true".
    /// Expected: the template is classified unknown.
    /// Invariant: a BFD-only change blocks deletion.
    /// </summary>
    [Fact]
    public void BfdOnlyDifference_IsUnknown()
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.99.99.0/27");
        OspfTemplateInventoryRow live = LiveFrom(provenance, "*L") with { UseBfd = "true" };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: the live template matches provenance except <c>Passive</c> is now false.
    /// Expected: the template is classified unknown.
    /// Invariant: a passive-state change blocks deletion.
    /// </summary>
    [Fact]
    public void PassiveStateDifference_IsUnknown()
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.99.99.0/27");
        OspfTemplateInventoryRow live = LiveFrom(provenance, "*L") with { Passive = false };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: a disabled live template for 10.16.11.0/27 sits under the planned consolidated management prefix, so its recorded fingerprint differs from the live row.
    /// Expected: it is classified as a single known-legacy row.
    /// Invariant: containment under a consolidated prefix makes a disabled leftover obsolete even without a fingerprint match.
    /// </summary>
    [Fact]
    public void DisabledLeftoverUnderManagement_IsKnownLegacyViaConsolidatedPrefix()
    {
        // Disabled leftovers under a planned consolidated prefix are obsolete by containment,
        // even when recorded provenance fingerprints differ.
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.16.11.0/27");
        OspfTemplateInventoryRow live = LiveFrom(provenance, "*L") with { Disabled = true };
        Assert.Equal(
            OspfTemplateClassification.KnownLegacy,
            Assert.Single(Classify(provenance, live).KnownLegacy).Classification);
    }

    /// <summary>
    /// Scenario: provenance lists networks as 10.99.99.0/27,10.99.99.32/27 while the live row lists them in reverse order.
    /// Expected: the row is classified as a single known-legacy template.
    /// Invariant: network lists are compared as sets, so order does not matter.
    /// </summary>
    [Fact]
    public void NetworkListOrderDifference_StillMatches()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(
            "vlan10",
            "10.99.99.0/27,10.99.99.32/27");
        OspfTemplateInventoryRow live = LiveFrom(provenance, "*L") with
        {
            Networks = "10.99.99.32/27,10.99.99.0/27",
        };
        Assert.Equal(
            OspfTemplateClassification.KnownLegacy,
            Assert.Single(Classify(provenance, live).KnownLegacy).Classification);
    }

    /// <summary>
    /// Scenario: provenance lists interfaces as vlan-b,vlan-a while the live row lists vlan-a,vlan-b.
    /// Expected: the row is classified as a single known-legacy template.
    /// Invariant: interface lists are compared as sets, so order does not matter.
    /// </summary>
    [Fact]
    public void InterfaceListOrderDifference_StillMatches()
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan-b,vlan-a", "10.99.99.0/27");
        OspfTemplateInventoryRow live = LiveFrom(provenance, "*L") with { Interfaces = "vlan-a,vlan-b" };
        Assert.Equal(
            OspfTemplateClassification.KnownLegacy,
            Assert.Single(Classify(provenance, live).KnownLegacy).Classification);
    }

    /// <summary>
    /// Scenario: the live row spells the default timers explicitly (hello 10s, dead 40s, retransmit 5s, transmit-delay 1s, instance-id 0) while provenance leaves them unset.
    /// Expected: the row is classified as a single known-legacy template.
    /// Invariant: an explicit default value is equivalent to an absent one.
    /// </summary>
    [Fact]
    public void TimerDefaultRepresentations_AreEquivalent()
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.99.99.0/27");
        OspfTemplateInventoryRow live = LiveFrom(provenance, "*L") with
        {
            HelloInterval = "10s",
            DeadInterval = "40s",
            RetransmitInterval = "5s",
            TransmitDelay = "1s",
            InstanceId = "0",
        };
        Assert.Equal(
            OspfTemplateClassification.KnownLegacy,
            Assert.Single(Classify(provenance, live).KnownLegacy).Classification);
    }

    /// <summary>
    /// Scenario: the plan has no superseded identities, and the live inventory has the planned templates plus a 10.16.11.0/27 template on vlan10.
    /// Expected: exactly one known-legacy row whose reason contains <c>Consolidated-prefix replacement</c>, and nothing unknown.
    /// Invariant: a management subdivision is classified through desired-state containment even when no provenance was recorded.
    /// </summary>
    [Fact]
    public void MissingProvenance_CompatibleManagementSubdivision_IsKnownLegacyViaDesiredState()
    {
        OspfMigrationPlan plan = BasePlan(superseded: []);
        OspfTemplateClassificationReport report = OspfTemplateCleanupClassifier.ClassifyOrThrow(
            plan,
            [
                PlannedBackhaul(),
                PlannedLoopback(),
                PlannedSite(),
                LiveRow("*L", "site", "vlan10", "10.16.11.0/27", passive: true),
            ]);
        Assert.Single(report.KnownLegacy);
        Assert.Empty(report.Unknown);
        Assert.Contains(
            "Consolidated-prefix replacement",
            report.KnownLegacy[0].Reason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: the plan has no superseded identities, and the live inventory has an extra 192.0.2.0/24 template outside the consolidated prefix.
    /// Expected: classification throws <c>InvalidOperationException</c> mentioning "No deletion was attempted" and "consolidated-prefix replacement".
    /// Invariant: without provenance, only templates contained in the consolidated prefix may be treated as obsolete.
    /// </summary>
    [Fact]
    public void MissingProvenance_UnrelatedPrefix_IsUnknown()
    {
        OspfMigrationPlan plan = BasePlan(superseded: []);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfTemplateCleanupClassifier.ClassifyOrThrow(
                plan,
                [
                    PlannedBackhaul(),
                    PlannedLoopback(),
                    PlannedSite(),
                    LiveRow("*L", "site", "vlan10", "192.0.2.0/24", passive: true),
                ]));
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("consolidated-prefix replacement", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scenario: the live row carries an unparseable value for cost, priority, or a timer or instance-id property (for example "not-a-number" or "bogus").
    /// Expected: the template is classified unknown.
    /// Invariant: a malformed value is never normalised into a match.
    /// </summary>
    [Theory]
    [InlineData("cost", "not-a-number")]
    [InlineData("priority", "bad")]
    [InlineData("hello-interval", "not-a-time")]
    [InlineData("dead-interval", "xx")]
    [InlineData("retransmit-interval", "bogus")]
    [InlineData("transmit-delay", "??")]
    [InlineData("instance-id", "999")]
    public void MalformedProperty_IsUnknown(string property, string value)
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.99.99.0/27");
        OspfTemplateInventoryRow live = LiveFrom(provenance, "*L");
        live = property switch
        {
            "cost" => live with { Cost = value },
            "priority" => live with { Priority = value },
            "hello-interval" => live with { HelloInterval = value },
            "dead-interval" => live with { DeadInterval = value },
            "retransmit-interval" => live with { RetransmitInterval = value },
            "transmit-delay" => live with { TransmitDelay = value },
            "instance-id" => live with { InstanceId = value },
            _ => live,
        };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: the live row has the id <c>*OTHER</c> but identical semantics to the recorded provenance.
    /// Expected: the row is classified as a single known-legacy template.
    /// Invariant: matching is by semantic identity, not by RouterOS row id.
    /// </summary>
    [Fact]
    public void DifferentId_SameSemantics_Matches()
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.99.99.0/27");
        Assert.Equal(
            OspfTemplateClassification.KnownLegacy,
            Assert.Single(Classify(provenance, LiveFrom(provenance, "*OTHER")).KnownLegacy).Classification);
    }

    /// <summary>
    /// Scenario: the legacy template's cost is changed to 10 after provenance was recorded, and <c>RemoveObsoleteTemplatesAsync</c> runs.
    /// Expected: <c>InvalidOperationException</c> mentioning "No deletion was attempted", and zero <c>remove</c> commands were sent.
    /// Invariant: a configuration drift after capture aborts cleanup before any deletion.
    /// </summary>
    [Fact]
    public async Task ConfigurationModifiedAfterProvenance_AbortsCleanupWithZeroDeletes()
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.99.99.0/27");
        OspfMigrationPlan plan = BasePlan([provenance]);
        var store = new List<Dictionary<string, string>>
        {
            DictBackhaul(),
            DictLoopback(),
            DictSite(),
            DictLegacy("vlan10", "10.99.99.0/27"),
        };

        // Mutate cost after "capture" so live no longer matches provenance.
        store[^1]["cost"] = "10";

        int removes = 0;
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/print",
                static _ => AreaPrintSentences())
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ =>
                [
                    ..store.Select(static r => new TikFakeReSentence(new Dictionary<string, string>(r))),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/remove",
                _ =>
                {
                    removes++;
                    return [new TikFakeDoneSentence()];
                });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OspfMikrotikOperations(Microsoft.Extensions.Logging.Abstractions.NullLogger<OspfMikrotikOperations>.Instance)
                .RemoveObsoleteTemplatesAsync(fake, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, removes);
    }

    /// <summary>
    /// Scenario: one timer or instance-id property of the live legacy template is changed after provenance was recorded, and <c>RemoveObsoleteTemplatesAsync</c> runs.
    /// Expected: <c>InvalidOperationException</c> mentioning "No deletion was attempted", and zero <c>remove</c> commands were sent.
    /// Invariant: drift in timer or instance-id values aborts cleanup before any deletion.
    /// </summary>
    [Theory]
    [InlineData("hello-interval", "1s")]
    [InlineData("dead-interval", "3s")]
    [InlineData("retransmit-interval", "2s")]
    [InlineData("transmit-delay", "2s")]
    [InlineData("instance-id", "7")]
    public async Task TimerOrInstanceModifiedAfterProvenance_AbortsWithZeroDeletes(string property, string value)
    {
        OspfSupersededTemplateIdentity provenance = Provenance("vlan10", "10.99.99.0/27");
        OspfMigrationPlan plan = BasePlan([provenance]);
        var store = new List<Dictionary<string, string>>
        {
            DictBackhaul(),
            DictLoopback(),
            DictSite(),
            DictLegacy("vlan10", "10.99.99.0/27"),
        };
        store[^1][property] = value;

        int removes = 0;
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/print",
                static _ => AreaPrintSentences())
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ =>
                [
                    ..store.Select(static r => new TikFakeReSentence(new Dictionary<string, string>(r))),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/remove",
                _ =>
                {
                    removes++;
                    return [new TikFakeDoneSentence()];
                });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OspfMikrotikOperations(Microsoft.Extensions.Logging.Abstractions.NullLogger<OspfMikrotikOperations>.Instance)
                .RemoveObsoleteTemplatesAsync(fake, plan, CancellationToken.None));

        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, removes);
    }

    /// <summary>
    /// Scenario: two provenance identities (vlan10 and vlan20 on separate /27s) are classified against two live rows (<c>*L1</c> and <c>*L2</c>).
    /// Expected: both rows are known legacy and nothing is unknown.
    /// Invariant: each superseded identity is matched on its own, with no interference between entries.
    /// </summary>
    [Fact]
    public void MultipleSupersededIdentities_MatchIndependently()
    {
        OspfSupersededTemplateIdentity a = Provenance("vlan10", "10.99.99.0/27");
        OspfSupersededTemplateIdentity b = Provenance("vlan20", "10.99.99.32/27");
        OspfTemplateClassificationReport report = Classify(
            [a, b],
            LiveFrom(a, "*L1"),
            LiveFrom(b, "*L2"));
        Assert.Equal(2, report.KnownLegacy.Count);
        Assert.Empty(report.Unknown);
    }

    /// <summary>
    /// Scenario: the plan content hash is recomputed after changing one provenance property (cost, auth, auth-id, priority, use-bfd, a timer, instance-id, area-instance, prefix-list, a virtual-link field, passive, disabled or type).
    /// Expected: each change produces a hash different from the base plan hash.
    /// Invariant: every semantic provenance property is covered by the content hash, so tampering invalidates the plan.
    /// </summary>
    [Theory]
    [InlineData("cost", "10")]
    [InlineData("auth", "md5")]
    [InlineData("auth-id", "2")]
    [InlineData("priority", "50")]
    [InlineData("use-bfd", "true")]
    [InlineData("hello-interval", "1s")]
    [InlineData("dead-interval", "3s")]
    [InlineData("retransmit-interval", "2s")]
    [InlineData("transmit-delay", "2s")]
    [InlineData("instance-id", "3")]
    [InlineData("area-instance", "alt-v2")]
    [InlineData("prefix-list", "pl")]
    [InlineData("vlink-neighbor-id", "9.9.9.9")]
    [InlineData("vlink-transit-area", "ta")]
    [InlineData("passive", "false")]
    [InlineData("disabled", "true")]
    [InlineData("type", "nbma")]
    public void PlanHash_ChangesWhenSemanticProvenancePropertyChanges(string property, string value)
    {
        OspfMigrationPlan basePlan = BasePlan([Provenance("vlan10", "10.99.99.0/27")]);
        string hash1 = OspfMigrationPlanner.ComputeContentHash(basePlan);

        OspfSupersededTemplateIdentity changed = property switch
        {
            "cost" => Provenance("vlan10", "10.99.99.0/27", cost: value),
            "auth" => Provenance("vlan10", "10.99.99.0/27", auth: value),
            "auth-id" => Provenance("vlan10", "10.99.99.0/27", authId: value),
            "priority" => Provenance("vlan10", "10.99.99.0/27", priority: value),
            "use-bfd" => Provenance("vlan10", "10.99.99.0/27", useBfd: true),
            "hello-interval" => Provenance("vlan10", "10.99.99.0/27", helloInterval: value),
            "dead-interval" => Provenance("vlan10", "10.99.99.0/27", deadInterval: value),
            "retransmit-interval" => Provenance("vlan10", "10.99.99.0/27", retransmitInterval: value),
            "transmit-delay" => Provenance("vlan10", "10.99.99.0/27", transmitDelay: value),
            "instance-id" => Provenance("vlan10", "10.99.99.0/27", instanceId: value),
            "area-instance" => Provenance("vlan10", "10.99.99.0/27", areaInstanceName: value),
            "prefix-list" => Provenance("vlan10", "10.99.99.0/27", prefixList: value),
            "vlink-neighbor-id" => Provenance("vlan10", "10.99.99.0/27", vlinkNeighborId: value),
            "vlink-transit-area" => Provenance("vlan10", "10.99.99.0/27", vlinkTransitArea: value),
            "passive" => Provenance("vlan10", "10.99.99.0/27", passive: false),
            "disabled" => Provenance("vlan10", "10.99.99.0/27", disabled: true),
            "type" => Provenance("vlan10", "10.99.99.0/27", type: value),
            _ => throw new InvalidOperationException(property),
        };

        Assert.NotEqual(hash1, OspfMigrationPlanner.ComputeContentHash(BasePlan([changed])));
    }

    /// <summary>
    /// Asserts that the exception reports unknown templates and states that no deletion was attempted.
    /// </summary>
    private static void AssertUnknown(InvalidOperationException ex)
    {
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classifies the live template against the provenance and returns the <c>InvalidOperationException</c> that
    /// classification is expected to throw.
    /// </summary>
    private static InvalidOperationException ClassifyExpectThrow(
        OspfSupersededTemplateIdentity provenance,
        OspfTemplateInventoryRow live) =>
        Assert.Throws<InvalidOperationException>(() => Classify(provenance, live));

    /// <summary>
    /// Classifies the planned backhaul, loopback and site templates plus <paramref name="extras"/> against a single provenance record.
    /// <param name="provenance">Superseded-template identity recorded in the plan.</param>
    /// <param name="extras">Additional live templates to classify alongside the planned ones.</param>
    /// <returns>The classification report; throws <see cref="InvalidOperationException"/> when unknown templates are found.</returns>
    /// </summary>
    private static OspfTemplateClassificationReport Classify(
        OspfSupersededTemplateIdentity provenance,
        params OspfTemplateInventoryRow[] extras) =>
        Classify([provenance], extras);

    /// <summary>
    /// Classifies the planned backhaul, loopback and site templates plus <paramref name="extras"/> against the given provenance records, failing closed on unknown templates.
    /// <param name="provenance">Superseded-template identities recorded in the plan.</param>
    /// <param name="extras">Additional live templates to classify alongside the planned ones.</param>
    /// <returns>The classification report.</returns>
    /// </summary>
    private static OspfTemplateClassificationReport Classify(
        IReadOnlyList<OspfSupersededTemplateIdentity> provenance,
        params OspfTemplateInventoryRow[] extras)
    {
        List<OspfTemplateInventoryRow> rows =
        [
            PlannedBackhaul(),
            PlannedLoopback(),
            PlannedSite(),
            ..extras,
        ];
        return OspfTemplateCleanupClassifier.ClassifyOrThrow(BasePlan(provenance), rows);
    }

    /// <summary>
    /// Builds a captured superseded-template identity for the <c>site</c> area from the supplied properties; auth keys are only fingerprinted, never stored as plaintext.
    /// <param name="interfaces">Interfaces property of the legacy template.</param>
    /// <param name="networks">Networks property of the legacy template.</param>
    /// <param name="type">Network type; <see langword="null"/> uses the RouterOS default.</param>
    /// <param name="cost">Interface cost; <see langword="null"/> uses the default.</param>
    /// <param name="auth">Authentication method; <see langword="null"/> uses the default.</param>
    /// <param name="authId">Authentication key id.</param>
    /// <param name="authKey">Authentication key; only its presence and fingerprint are captured.</param>
    /// <param name="priority">Router priority; <see langword="null"/> uses the default.</param>
    /// <param name="useBfd">Whether BFD is enabled; <see langword="null"/> uses the default.</param>
    /// <param name="disabled">Whether the template is disabled.</param>
    /// <param name="passive">Whether the template is passive.</param>
    /// <param name="helloInterval">Hello interval; <see langword="null"/> uses the default.</param>
    /// <param name="deadInterval">Dead interval; <see langword="null"/> uses the default.</param>
    /// <param name="retransmitInterval">Retransmit interval; <see langword="null"/> uses the default.</param>
    /// <param name="transmitDelay">Transmit delay; <see langword="null"/> uses the default.</param>
    /// <param name="instanceId">OSPF instance id; <see langword="null"/> uses the default.</param>
    /// <param name="areaInstanceName">Instance the area belongs to.</param>
    /// <param name="prefixList">Prefix list reference.</param>
    /// <param name="vlinkNeighborId">Virtual-link neighbour id.</param>
    /// <param name="vlinkTransitArea">Virtual-link transit area.</param>
    /// <param name="comment">Template comment.</param>
    /// <param name="authKeyAmbiguous">Whether the auth key could not be read unambiguously.</param>
    /// <returns>The captured semantic identity.</returns>
    /// </summary>
    private static OspfSupersededTemplateIdentity Provenance(
        string interfaces,
        string networks,
        string? type = null,
        string? cost = null,
        string? auth = null,
        string? authId = null,
        string? authKey = null,
        string? priority = null,
        bool? useBfd = null,
        bool disabled = false,
        bool passive = true,
        string? helloInterval = null,
        string? deadInterval = null,
        string? retransmitInterval = null,
        string? transmitDelay = null,
        string? instanceId = null,
        string? areaInstanceName = "default-v2",
        string? prefixList = null,
        string? vlinkNeighborId = null,
        string? vlinkTransitArea = null,
        string? comment = null,
        bool authKeyAmbiguous = false)
    {
        bool keyPresent = !authKeyAmbiguous && OspfTemplateSemanticIdentity.AuthKeyPresent(authKey);
        string? useBfdRaw = useBfd switch
        {
            null => null,
            true => "true",
            false => "false",
        };
        return OspfTemplateSemanticIdentity.FromCaptured(
            "site",
            interfaces,
            networks,
            passive,
            new OspfCapturedTemplateSemantics
            {
                Disabled = disabled,
                Type = type,
                Cost = cost,
                Auth = auth,
                AuthId = authId,
                AuthKeyPresent = keyPresent,
                AuthKeyFingerprint = keyPresent
                    ? OspfTemplateSemanticIdentity.FingerprintAuthKey(authKey)
                    : null,
                AuthKeyAmbiguous = authKeyAmbiguous,
                Priority = priority,
                UseBfd = useBfdRaw,
                HelloInterval = helloInterval,
                DeadInterval = deadInterval,
                RetransmitInterval = retransmitInterval,
                TransmitDelay = transmitDelay,
                InstanceId = instanceId,
                AreaInstanceName = areaInstanceName,
                PrefixList = prefixList,
                VlinkNeighborId = vlinkNeighborId,
                VlinkTransitArea = vlinkTransitArea,
                Comment = comment,
            });
    }

    /// <summary>
    /// Builds the live interface-template row with the given id that a provenance identity would produce,
    /// converting default tokens and intervals to RouterOS values.
    /// </summary>
    private static OspfTemplateInventoryRow LiveFrom(OspfSupersededTemplateIdentity p, string id) =>
        new()
        {
            Id = id,
            Area = p.AreaName,
            Interfaces = p.Interfaces,
            Networks = p.NetworkCidr,
            Passive = p.Passive,
            Disabled = p.Disabled,
            Type = p.Type == OspfTemplateSemanticIdentity.DefaultTypeToken ? "broadcast" : p.Type,
            Cost = p.Cost == OspfTemplateSemanticIdentity.DefaultCostToken ? "1" : p.Cost,
            Auth = p.Auth == OspfTemplateSemanticIdentity.DefaultAuthToken ? null : p.Auth,
            AuthId = p.AuthId,
            AuthKeyPresent = p.AuthKeyPresent,
            AuthKeyFingerprint = p.AuthKeyFingerprint,
            AuthKeyAmbiguous = p.AuthKeyAmbiguous,
            Priority = p.Priority == OspfTemplateSemanticIdentity.DefaultPriorityToken ? "128" : p.Priority,
            UseBfd = p.UseBfd == OspfTemplateSemanticIdentity.DefaultUseBfdToken ? null : p.UseBfd,
            HelloInterval = p.HelloInterval == OspfTemplateSemanticIdentity.DefaultHelloToken
                ? null
                : $"{p.HelloInterval}s",
            DeadInterval = p.DeadInterval == OspfTemplateSemanticIdentity.DefaultDeadToken
                ? null
                : $"{p.DeadInterval}s",
            RetransmitInterval = p.RetransmitInterval == OspfTemplateSemanticIdentity.DefaultRetransmitToken
                ? null
                : $"{p.RetransmitInterval}s",
            TransmitDelay = p.TransmitDelay == OspfTemplateSemanticIdentity.DefaultTransmitDelayToken
                ? null
                : $"{p.TransmitDelay}s",
            InstanceId = p.InstanceId == OspfTemplateSemanticIdentity.DefaultInstanceIdToken ? null : p.InstanceId,
            AreaInstanceName = p.AreaInstanceName,
            PrefixList = p.PrefixList,
            VlinkNeighborId = p.VlinkNeighborId,
            VlinkTransitArea = p.VlinkTransitArea,
            Comment = p.Comment,
        };

    /// <summary>
    /// Builds a plain live template row with default broadcast type, cost 1, priority 128 and no BFD.
    /// <param name="id">RouterOS <c>.id</c> of the row.</param>
    /// <param name="area">Area name.</param>
    /// <param name="interfaces">Interfaces property.</param>
    /// <param name="networks">Networks property.</param>
    /// <param name="passive">Whether the template is passive.</param>
    /// </summary>
    private static OspfTemplateInventoryRow LiveRow(
        string id,
        string area,
        string interfaces,
        string networks,
        bool passive) =>
        new()
        {
            Id = id,
            Area = area,
            Interfaces = interfaces,
            Networks = networks,
            Passive = passive,
            Disabled = false,
            Type = "broadcast",
            Cost = "1",
            Priority = "128",
            UseBfd = null,
            AreaInstanceName = "default-v2",
        };

    /// <summary>
    /// Builds the live row for the planned backhaul template (<c>eth-01_BH</c>, 10.16.248.76/30, ptp, cost 10).
    /// </summary>
    private static OspfTemplateInventoryRow PlannedBackhaul() =>
        new()
        {
            Id = "*1",
            Area = "backbone-v2",
            Interfaces = "eth-01_BH",
            Networks = "10.16.248.76/30",
            Passive = false,
            Disabled = false,
            Type = "ptp",
            Cost = "10",
            Priority = "1",
            UseBfd = null,
            AreaInstanceName = "default-v2",
        };

    /// <summary>
    /// Builds the live row for the planned loopback template (<c>Loopback</c>, 10.16.0.11/32, passive).
    /// </summary>
    private static OspfTemplateInventoryRow PlannedLoopback() =>
        new()
        {
            Id = "*9",
            Area = "backbone-v2",
            Interfaces = "Loopback",
            Networks = "10.16.0.11/32",
            Passive = true,
            Disabled = false,
            Type = "broadcast",
            Cost = "1",
            Priority = "128",
            UseBfd = null,
            AreaInstanceName = "default-v2",
        };

    /// <summary>
    /// Builds the live row for the planned consolidated site template (<c>all</c> interfaces, 10.16.11.0/24, 100.64.0.0/10 and 172.16.0.0/12, passive).
    /// </summary>
    private static OspfTemplateInventoryRow PlannedSite() =>
        new()
        {
            Id = "*A",
            Area = "site",
            Interfaces = "all",
            Networks = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
            Passive = true,
            Disabled = false,
            Type = "broadcast",
            Cost = "1",
            Priority = "128",
            UseBfd = null,
            AreaInstanceName = "default-v2",
        };

    /// <summary>
    /// Builds a valid RouterOS v7 plan (backhaul, loopback, consolidated site template) carrying the supplied superseded-template provenance.
    /// <param name="superseded">Provenance records to attach to the plan.</param>
    /// </summary>
    private static OspfMigrationPlan BasePlan(IReadOnlyList<OspfSupersededTemplateIdentity> superseded) =>
        new()
        {
            ImportId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            JobId = "job",
            RouterIp = "10.0.0.1",
            Identity = "LAB",
            SourceRouterOsVersion = "7.23.8",
            Architecture = "mipsbe",
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            InstanceName = "default-v2",
            RouterId = "10.16.0.11",
            DerivedManagementCidr = "10.16.11.0/24",
            Areas =
            [
                new OspfPlanArea { Name = "backbone-v2", AreaId = "0.0.0.0", TargetType = "default", IsBackbone = true },
                new OspfPlanArea
                {
                    Name = "site",
                    AreaId = "10.16.0.11",
                    TargetType = "stub",
                    DefaultCost = "1",
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
            SupersededTemplates = superseded,
            ValidationErrors = [],
            NormalisationNotes = [],
            ContentHash = "x",
        };

    /// <summary>
    /// Builds the fake <c>/routing/ospf/area/print</c> response containing the <c>backbone-v2</c> and <c>site</c> areas.
    /// </summary>
    private static tik4net.ITikSentence[] AreaPrintSentences() =>
    [
        new TikFakeReSentence(new Dictionary<string, string>
        {
            [".id"] = "*ab",
            ["name"] = "backbone-v2",
            ["instance"] = "default-v2",
            ["area-id"] = "0.0.0.0",
        }),
        new TikFakeReSentence(new Dictionary<string, string>
        {
            [".id"] = "*as",
            ["name"] = "site",
            ["instance"] = "default-v2",
            ["area-id"] = "10.16.0.11",
        }),
        new TikFakeDoneSentence(),
    ];

    /// <summary>
    /// Builds a raw template row for the backbone backhaul with ptp type and cost 10.
    /// </summary>
    private static Dictionary<string, string> DictBackhaul() => new()
    {
        [".id"] = "*1",
        ["area"] = "backbone-v2",
        ["interfaces"] = "eth-01_BH",
        ["networks"] = "10.16.248.76/30",
        ["type"] = "ptp",
        ["cost"] = "10",
        ["disabled"] = "false",
    };

    /// <summary>
    /// Builds the raw RouterOS row for the planned loopback template.
    /// </summary>
    private static Dictionary<string, string> DictLoopback() => new()
    {
        [".id"] = "*9",
        ["area"] = "backbone-v2",
        ["interfaces"] = "Loopback",
        ["networks"] = "10.16.0.11/32",
        ["passive"] = "",
        ["disabled"] = "false",
        ["type"] = "broadcast",
        ["cost"] = "1",
        ["priority"] = "128",
        ["use-bfd"] = "false",
    };

    /// <summary>
    /// Builds the raw RouterOS row for the planned consolidated site template.
    /// </summary>
    private static Dictionary<string, string> DictSite() => new()
    {
        [".id"] = "*A",
        ["area"] = "site",
        ["interfaces"] = "all",
        ["networks"] = "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12",
        ["passive"] = "",
        ["disabled"] = "false",
        ["type"] = "broadcast",
        ["cost"] = "1",
        ["priority"] = "128",
        ["use-bfd"] = "false",
    };

    /// <summary>
    /// Builds the raw RouterOS row for a legacy site template (<c>*L</c>) with default broadcast semantics and the given interfaces and networks.
    /// <param name="interfaces">Interfaces property of the legacy row.</param>
    /// <param name="networks">Networks property of the legacy row.</param>
    /// </summary>
    private static Dictionary<string, string> DictLegacy(string interfaces, string networks) => new()
    {
        [".id"] = "*L",
        ["area"] = "site",
        ["interfaces"] = interfaces,
        ["networks"] = networks,
        ["passive"] = "",
        ["disabled"] = "false",
        ["type"] = "broadcast",
        ["cost"] = "1",
        ["priority"] = "128",
        ["use-bfd"] = "false",
    };
}
