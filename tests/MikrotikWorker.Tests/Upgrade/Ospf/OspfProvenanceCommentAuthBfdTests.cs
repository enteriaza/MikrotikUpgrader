// -----------------------------------------------------------------------------
// File: OspfProvenanceCommentAuthBfdTests.cs
//
// Description:
// Comment, auth-key alias, use-bfd and type-canonicalisation provenance
// coverage. Secrets are asserted only via fingerprints / presence markers —
// never plaintext.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Upgrade.Ospf;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Comment, auth-key alias, use-bfd and type-canonicalisation provenance coverage.
/// Secrets are asserted only via fingerprints / presence markers — never plaintext.
/// </summary>
public sealed class OspfProvenanceCommentAuthBfdTests
{
    /// <summary>
    /// Scenario: the live template is built from provenance and its comment is still exactly "ops:vlan10".
    /// Expected: the classifier reports it as known legacy.
    /// Invariant: an unchanged comment is part of an exact semantic identity match.
    /// </summary>
    [Fact]
    public void ExactCommentMatch_IsKnownLegacy()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(comment: "ops:vlan10");
        AssertKnownLegacy(Classify(provenance, LiveFrom(provenance)));
    }

    /// <summary>
    /// Scenario: the captured comment (null or "original") differs from the live comment ("added" or "changed").
    /// Expected: the live template is classified unknown, and classification refuses to proceed.
    /// Invariant: a comment edited after capture means someone touched the template, so it is not safe to delete.
    /// </summary>
    [Theory]
    [InlineData(null, "added")]
    [InlineData("original", "changed")]
    public void CommentMutation_AwayFromDesired_IsUnknown(string? captured, string? liveComment)
    {
        OspfSupersededTemplateIdentity provenance = Provenance(comment: captured);
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { Comment = liveComment };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: provenance recorded comment "original" for a template under the planned consolidated management prefix, and the live comment is now null.
    /// Expected: the template is still classified known legacy.
    /// Invariant: under a consolidated prefix, attribute-only differences such as comment do not block removing an obsolete template.
    /// </summary>
    [Fact]
    public void CommentClearedUnderConsolidatedPrefix_IsKnownLegacy()
    {
        // Under a planned consolidated prefix, attribute-only differences (including comment)
        // do not block obsolete-template removal.
        OspfSupersededTemplateIdentity provenance = ProvenanceUnderMgmt(comment: "original");
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { Comment = null };
        AssertKnownLegacy(Classify(provenance, live));
    }

    /// <summary>
    /// Scenario: provenance has no comment, and the live comment is first "" and then null.
    /// Expected: both variants classify as known legacy.
    /// Invariant: an absent comment and an empty comment are the same value.
    /// </summary>
    [Fact]
    public void CommentAbsentAndEmpty_AreEquivalent()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(comment: null);
        AssertKnownLegacy(Classify(provenance, LiveFrom(provenance) with { Comment = "" }));
        AssertKnownLegacy(Classify(provenance, LiveFrom(provenance) with { Comment = null }));
    }

    /// <summary>
    /// Scenario: the same secret arrives through <c>auth-key</c>, <c>authentication-key</c>, or both with equal values, and the live row carries its fingerprint.
    /// Expected: <c>ResolveAuthKey</c> yields a present, unambiguous resolution with the secret's fingerprint, and the template classifies as known legacy.
    /// Invariant: RouterOS auth-key aliases resolve to one canonical fingerprint, so alias spelling does not hide a match.
    /// </summary>
    [Theory]
    [InlineData("auth-key")]
    [InlineData("authentication-key")]
    [InlineData("both-same")]
    public void AuthKeyAlias_CanonicalMatch_IsKnownLegacy(string mode)
    {
        const string secret = "unit-test-secret";
        string fp = OspfTemplateSemanticIdentity.FingerprintAuthKey(secret)!;
        OspfSupersededTemplateIdentity provenance = Provenance(authKey: secret);

        OspfTemplateInventoryRow live = LiveFrom(provenance) with
        {
            AuthKeyPresent = true,
            AuthKeyFingerprint = fp,
            AuthKeyAmbiguous = false,
        };

        // Inventory mapping is tested via ResolveAuthKey; classification uses fingerprint.
        Assert.Equal(
            mode switch
            {
                "auth-key" => OspfTemplateSemanticIdentity.ResolveAuthKey(secret, null),
                "authentication-key" => OspfTemplateSemanticIdentity.ResolveAuthKey(null, secret),
                _ => OspfTemplateSemanticIdentity.ResolveAuthKey(secret, secret),
            },
            new OspfTemplateSemanticIdentity.AuthKeyResolution(true, fp, false));

        AssertKnownLegacy(Classify(provenance, live));
    }

    /// <summary>
    /// Scenario: <c>auth-key</c> is "alpha" while <c>authentication-key</c> is "beta", and the live row is marked ambiguous.
    /// Expected: the resolution is ambiguous and not present, and the template is classified unknown.
    /// Invariant: conflicting auth-key aliases are never resolved by guessing, and block deletion.
    /// </summary>
    [Fact]
    public void ConflictingAuthKeyAliases_AreUnknown()
    {
        OspfTemplateSemanticIdentity.AuthKeyResolution conflict =
            OspfTemplateSemanticIdentity.ResolveAuthKey("alpha", "beta");
        Assert.True(conflict.Ambiguous);
        Assert.False(conflict.Present);

        OspfSupersededTemplateIdentity provenance = Provenance(authKey: "alpha");
        OspfTemplateInventoryRow live = LiveFrom(provenance) with
        {
            AuthKeyPresent = false,
            AuthKeyFingerprint = null,
            AuthKeyAmbiguous = true,
        };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: the live row gains an auth key that provenance did not have ("add"), or carries a different key from the one captured ("change").
    /// Expected: the template is classified unknown.
    /// Invariant: an added or rotated auth key after capture marks a template as modified and not safe to delete.
    /// </summary>
    [Theory]
    [InlineData("add")]
    [InlineData("change")]
    public void AuthKeyLifecycle_IsUnknown(string change)
    {
        OspfSupersededTemplateIdentity provenance = change == "add"
            ? Provenance()
            : Provenance(authKey: "original-secret");

        OspfTemplateInventoryRow live = change switch
        {
            "add" => LiveFrom(provenance) with
            {
                AuthKeyPresent = true,
                AuthKeyFingerprint = OspfTemplateSemanticIdentity.FingerprintAuthKey("new-secret"),
            },
            "change" => LiveFrom(provenance) with
            {
                AuthKeyPresent = true,
                AuthKeyFingerprint = OspfTemplateSemanticIdentity.FingerprintAuthKey("changed-secret"),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: provenance recorded an auth key for a template under the consolidated management prefix, and the live row now has no key and no auth.
    /// Expected: the template is classified known legacy.
    /// Invariant: under a consolidated prefix, losing an auth key does not stop removal of an obsolete template.
    /// </summary>
    [Fact]
    public void AuthKeyRemovedUnderConsolidatedPrefix_IsKnownLegacy()
    {
        OspfSupersededTemplateIdentity provenance = ProvenanceUnderMgmt(authKey: "original-secret");
        OspfTemplateInventoryRow live = LiveFrom(provenance) with
        {
            AuthKeyPresent = false,
            AuthKeyFingerprint = null,
            Auth = null,
        };
        AssertKnownLegacy(Classify(provenance, live));
    }

    /// <summary>
    /// Scenario: provenance recorded auth md5 with a key, and the live row now reports auth sha256.
    /// Expected: the template is classified unknown.
    /// Invariant: a changed authentication method means the template no longer matches what was captured.
    /// </summary>
    [Fact]
    public void AuthMethodChanged_IsUnknown()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(auth: "md5", authKey: "k");
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { Auth = "sha256" };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: <c>NormalizeUseBfd</c> is fed null, false/no/0 and true/yes/1 spellings.
    /// Expected: null and the false spellings normalise to "false", and the true spellings normalise to "true".
    /// Invariant: every RouterOS spelling of a boolean BFD flag compares as the same canonical token.
    /// </summary>
    [Theory]
    [InlineData(null, "false")]
    [InlineData("false", "false")]
    [InlineData("no", "false")]
    [InlineData("0", "false")]
    [InlineData("true", "true")]
    [InlineData("yes", "true")]
    [InlineData("1", "true")]
    public void UseBfd_CanonicalRepresentations(string? raw, string expectedToken)
    {
        Assert.Equal(expectedToken, OspfTemplateSemanticIdentity.NormalizeUseBfd(raw));
    }

    /// <summary>
    /// Scenario: the live <c>use-bfd</c> value is the unrecognised string "maybe".
    /// Expected: the template is classified unknown.
    /// Invariant: a value that cannot be normalised is never assumed to match the provenance.
    /// </summary>
    [Fact]
    public void UseBfd_Malformed_IsUnknown()
    {
        OspfSupersededTemplateIdentity provenance = Provenance();
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { UseBfd = "maybe" };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: <c>NormalizeType</c> is fed nbma, ptmp, point-to-multipoint, ptp-unnumbered, virtual-link, ptmp-broadcast, ptp, point-to-point, broadcast, null and a custom value.
    /// Expected: long and short aliases collapse (point-to-multipoint to ptmp, point-to-point to ptp), broadcast and null become "default", and other types are preserved.
    /// Invariant: only true aliases and the default are merged, so distinct network types stay distinct.
    /// </summary>
    [Theory]
    [InlineData("nbma", "nbma")]
    [InlineData("ptmp", "ptmp")]
    [InlineData("point-to-multipoint", "ptmp")]
    [InlineData("ptp-unnumbered", "ptp-unnumbered")]
    [InlineData("virtual-link", "virtual-link")]
    [InlineData("ptmp-broadcast", "ptmp-broadcast")]
    [InlineData("ptp", "ptp")]
    [InlineData("point-to-point", "ptp")]
    [InlineData("broadcast", "default")]
    [InlineData(null, "default")]
    [InlineData("custom-type", "custom-type")]
    public void Type_Canonicalisation_PreservesDistinctTypes(string? raw, string expected)
    {
        Assert.Equal(expected, OspfTemplateSemanticIdentity.NormalizeType(raw));
    }

    /// <summary>
    /// Scenario: provenance recorded type broadcast (the default), and the live row now has type nbma.
    /// Expected: the template is classified unknown.
    /// Invariant: nbma is never treated as equivalent to the broadcast default.
    /// </summary>
    [Fact]
    public void Type_NbmaDoesNotMatchBroadcastDefault()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(type: "broadcast");
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { Type = "nbma" };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    /// <summary>
    /// Scenario: content hashes are computed for plans whose provenance differs only by comment or auth key.
    /// Expected: the hash differs between no comment and comment "x", between no key and key "k1", and between keys "k1" and "k2".
    /// Invariant: the plan content hash covers comment and auth-key identity, so tampering with them invalidates the plan.
    /// </summary>
    [Fact]
    public void PlanHash_ChangesForCommentAndAuthKey()
    {
        string baseHash = OspfMigrationPlanner.ComputeContentHash(BasePlan([Provenance()]));
        Assert.NotEqual(
            baseHash,
            OspfMigrationPlanner.ComputeContentHash(BasePlan([Provenance(comment: "x")])));
        Assert.NotEqual(
            baseHash,
            OspfMigrationPlanner.ComputeContentHash(BasePlan([Provenance(authKey: "k1")])));
        Assert.NotEqual(
            OspfMigrationPlanner.ComputeContentHash(BasePlan([Provenance(authKey: "k1")])),
            OspfMigrationPlanner.ComputeContentHash(BasePlan([Provenance(authKey: "k2")])));
    }

    /// <summary>
    /// Scenario: <c>RemoveObsoleteTemplatesAsync</c> runs against a live store where the legacy template's comment was changed to "changed" after capture.
    /// Expected: <c>InvalidOperationException</c> mentioning "No deletion was attempted", and zero <c>remove</c> commands were sent.
    /// Invariant: one modified template aborts cleanup before any deletion.
    /// </summary>
    [Fact]
    public async Task CommentChangedAfterCapture_AbortsWithZeroDeletes()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(comment: "captured");
        OspfMigrationPlan plan = BasePlan([provenance]);
        var store = new List<Dictionary<string, string>>
        {
            DictBackhaul(),
            DictLoopback(),
            DictSite(),
            WithComment(DictLegacy(), "changed"),
        };

        int removes = 0;
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/print",
                static _ => AreaPrint())
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
    /// Scenario: the live legacy template has <c>auth-key</c>="alpha" and <c>authentication-key</c>="beta".
    /// Expected: <c>InvalidOperationException</c> mentioning "No deletion was attempted", and zero <c>remove</c> commands were sent.
    /// Invariant: ambiguous authentication aliases abort cleanup before any deletion.
    /// </summary>
    [Fact]
    public async Task ConflictingAuthAliases_AbortsWithZeroDeletes()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(authKey: "alpha");
        OspfMigrationPlan plan = BasePlan([provenance]);
        Dictionary<string, string> legacy = DictLegacy();
        legacy["auth-key"] = "alpha";
        legacy["authentication-key"] = "beta";

        int removes = 0;
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/area/print",
                static _ => AreaPrint())
            .WithResponse(
                static rows => rows.First() == "/routing/ospf/interface-template/print",
                _ =>
                [
                    new TikFakeReSentence(DictBackhaul()),
                    new TikFakeReSentence(DictLoopback()),
                    new TikFakeReSentence(DictSite()),
                    new TikFakeReSentence(legacy),
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
    /// Asserts that the report holds exactly one known-legacy template and no unknown ones.
    /// </summary>
    private static void AssertKnownLegacy(OspfTemplateClassificationReport report)
    {
        Assert.Equal(OspfTemplateClassification.KnownLegacy, Assert.Single(report.KnownLegacy).Classification);
        Assert.Empty(report.Unknown);
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
    /// Classifies the planned backhaul, loopback and site templates plus <paramref name="extras"/> against a single provenance record, throwing when unknown templates remain.
    /// <param name="provenance">Superseded-template identity recorded in the plan.</param>
    /// <param name="extras">Additional live templates to classify alongside the planned ones.</param>
    /// <returns>The classification report.</returns>
    /// </summary>
    private static OspfTemplateClassificationReport Classify(
        OspfSupersededTemplateIdentity provenance,
        params OspfTemplateInventoryRow[] extras)
    {
        return OspfTemplateCleanupClassifier.ClassifyOrThrow(
            BasePlan([provenance]),
            [
                Planned("*1", "backbone-v2", "eth-01_BH", "10.16.248.76/30", passive: false, type: "ptp", cost: "10"),
                Planned("*9", "backbone-v2", "Loopback", "10.16.0.11/32", passive: true),
                Planned("*A", "site", "all", "10.16.11.0/24,100.64.0.0/10,172.16.0.0/12", passive: true),
                ..extras,
            ]);
    }

    /// <summary>
    /// Builds a superseded template identity for a non-management prefix with optional comment, auth, auth key and
    /// type.
    /// </summary>
    private static OspfSupersededTemplateIdentity Provenance(
        string? comment = null,
        string? auth = null,
        string? authKey = null,
        string? type = null) =>
        ProvenanceWithNetworks("10.99.99.0/27", comment, auth, authKey, type);

    /// <summary>
    /// Builds a superseded template identity for a prefix inside the management range with optional comment, auth,
    /// auth key and type.
    /// </summary>
    private static OspfSupersededTemplateIdentity ProvenanceUnderMgmt(
        string? comment = null,
        string? auth = null,
        string? authKey = null,
        string? type = null) =>
        ProvenanceWithNetworks("10.16.11.0/27", comment, auth, authKey, type);

    /// <summary>
    /// Builds a superseded template identity for the given networks from captured semantics, including auth key
    /// presence and fingerprint.
    /// </summary>
    private static OspfSupersededTemplateIdentity ProvenanceWithNetworks(
        string networks,
        string? comment = null,
        string? auth = null,
        string? authKey = null,
        string? type = null) =>
        OspfTemplateSemanticIdentity.FromCaptured(
            "site",
            "vlan10",
            networks,
            passive: true,
            new OspfCapturedTemplateSemantics
            {
                Comment = comment,
                Auth = auth,
                AuthKeyPresent = OspfTemplateSemanticIdentity.AuthKeyPresent(authKey),
                AuthKeyFingerprint = OspfTemplateSemanticIdentity.AuthKeyPresent(authKey)
                    ? OspfTemplateSemanticIdentity.FingerprintAuthKey(authKey)
                    : null,
                Type = type,
                AreaInstanceName = "default-v2",
            });

    /// <summary>
    /// Builds the live interface-template row that a provenance identity would produce, mapping default tokens to
    /// RouterOS defaults.
    /// </summary>
    private static OspfTemplateInventoryRow LiveFrom(OspfSupersededTemplateIdentity p) =>
        new()
        {
            Id = "*L",
            Area = p.AreaName,
            Interfaces = p.Interfaces,
            Networks = p.NetworkCidr,
            Passive = p.Passive,
            Disabled = p.Disabled,
            Type = p.Type == OspfTemplateSemanticIdentity.DefaultTypeToken ? "broadcast" : p.Type,
            Cost = "1",
            Auth = p.Auth == OspfTemplateSemanticIdentity.DefaultAuthToken ? null : p.Auth,
            AuthId = p.AuthId,
            AuthKeyPresent = p.AuthKeyPresent,
            AuthKeyFingerprint = p.AuthKeyFingerprint,
            AuthKeyAmbiguous = p.AuthKeyAmbiguous,
            Priority = "128",
            UseBfd = null,
            AreaInstanceName = p.AreaInstanceName,
            Comment = p.Comment,
        };

    /// <summary>
    /// Builds a live row for a planned template with broadcast type, cost 1 and default priority unless overridden (priority 1 for non-passive, 128 for passive).
    /// <param name="id">RouterOS <c>.id</c> of the row.</param>
    /// <param name="area">Area name.</param>
    /// <param name="interfaces">Interfaces property.</param>
    /// <param name="networks">Networks property.</param>
    /// <param name="passive">Whether the template is passive.</param>
    /// <param name="type">Optional network type; defaults to broadcast.</param>
    /// <param name="cost">Optional cost; defaults to 1.</param>
    /// </summary>
    private static OspfTemplateInventoryRow Planned(
        string id,
        string area,
        string interfaces,
        string networks,
        bool passive,
        string? type = null,
        string? cost = null) =>
        new()
        {
            Id = id,
            Area = area,
            Interfaces = interfaces,
            Networks = networks,
            Passive = passive,
            Disabled = false,
            Type = type ?? "broadcast",
            Cost = cost ?? "1",
            Priority = passive ? "128" : "1",
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
    private static tik4net.ITikSentence[] AreaPrint() =>
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
    ];

    /// <summary>
    /// Builds the raw RouterOS row for the planned backhaul template (<c>eth-01_BH</c>, 10.16.248.76/30, ptp, cost 10).
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
    /// Builds the raw RouterOS row for the planned loopback template (<c>Loopback</c>, 10.16.0.11/32, passive).
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
    };

    /// <summary>
    /// Builds a raw template row for the consolidated passive site matcher in the site area.
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
    };

    /// <summary>
    /// Builds the raw RouterOS row for the legacy site template (<c>*L</c>, <c>vlan10</c>, 10.99.99.0/27) carrying the comment <c>captured</c>.
    /// </summary>
    private static Dictionary<string, string> DictLegacy() => new()
    {
        [".id"] = "*L",
        ["area"] = "site",
        ["interfaces"] = "vlan10",
        ["networks"] = "10.99.99.0/27",
        ["passive"] = "",
        ["disabled"] = "false",
        ["type"] = "broadcast",
        ["cost"] = "1",
        ["priority"] = "128",
        ["comment"] = "captured",
    };

    /// <summary>
    /// Returns a copy of <paramref name="row"/> with its <c>comment</c> property replaced.
    /// <param name="row">Raw RouterOS row to copy.</param>
    /// <param name="comment">New comment value.</param>
    /// </summary>
    private static Dictionary<string, string> WithComment(Dictionary<string, string> row, string comment)
    {
        Dictionary<string, string> copy = new(row) { ["comment"] = comment };
        return copy;
    }
}
