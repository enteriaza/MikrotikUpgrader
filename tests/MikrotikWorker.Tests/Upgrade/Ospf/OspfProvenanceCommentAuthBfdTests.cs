using MikrotikWorker.Upgrade.Ospf;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Comment, auth-key alias, use-bfd and type-canonicalisation provenance coverage.
/// Secrets are asserted only via fingerprints / presence markers — never plaintext.
/// </summary>
public sealed class OspfProvenanceCommentAuthBfdTests
{
    [Fact]
    public void ExactCommentMatch_IsKnownLegacy()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(comment: "ops:vlan10");
        AssertKnownLegacy(Classify(provenance, LiveFrom(provenance)));
    }

    [Theory]
    [InlineData(null, "added")]
    [InlineData("original", "changed")]
    public void CommentMutation_AwayFromDesired_IsUnknown(string? captured, string? liveComment)
    {
        OspfSupersededTemplateIdentity provenance = Provenance(comment: captured);
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { Comment = liveComment };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

    [Fact]
    public void CommentClearedUnderConsolidatedPrefix_IsKnownLegacy()
    {
        // Under a planned consolidated prefix, attribute-only differences (including comment)
        // do not block obsolete-template removal.
        OspfSupersededTemplateIdentity provenance = ProvenanceUnderMgmt(comment: "original");
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { Comment = null };
        AssertKnownLegacy(Classify(provenance, live));
    }

    [Fact]
    public void CommentAbsentAndEmpty_AreEquivalent()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(comment: null);
        AssertKnownLegacy(Classify(provenance, LiveFrom(provenance) with { Comment = "" }));
        AssertKnownLegacy(Classify(provenance, LiveFrom(provenance) with { Comment = null }));
    }

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

    [Fact]
    public void AuthMethodChanged_IsUnknown()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(auth: "md5", authKey: "k");
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { Auth = "sha256" };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

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

    [Fact]
    public void UseBfd_Malformed_IsUnknown()
    {
        OspfSupersededTemplateIdentity provenance = Provenance();
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { UseBfd = "maybe" };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

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

    [Fact]
    public void Type_NbmaDoesNotMatchBroadcastDefault()
    {
        OspfSupersededTemplateIdentity provenance = Provenance(type: "broadcast");
        OspfTemplateInventoryRow live = LiveFrom(provenance) with { Type = "nbma" };
        AssertUnknown(ClassifyExpectThrow(provenance, live));
    }

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

    private static void AssertKnownLegacy(OspfTemplateClassificationReport report)
    {
        Assert.Equal(OspfTemplateClassification.KnownLegacy, Assert.Single(report.KnownLegacy).Classification);
        Assert.Empty(report.Unknown);
    }

    private static void AssertUnknown(InvalidOperationException ex)
    {
        Assert.Contains("No deletion was attempted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException ClassifyExpectThrow(
        OspfSupersededTemplateIdentity provenance,
        OspfTemplateInventoryRow live) =>
        Assert.Throws<InvalidOperationException>(() => Classify(provenance, live));

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

    private static OspfSupersededTemplateIdentity Provenance(
        string? comment = null,
        string? auth = null,
        string? authKey = null,
        string? type = null) =>
        ProvenanceWithNetworks("10.99.99.0/27", comment, auth, authKey, type);

    private static OspfSupersededTemplateIdentity ProvenanceUnderMgmt(
        string? comment = null,
        string? auth = null,
        string? authKey = null,
        string? type = null) =>
        ProvenanceWithNetworks("10.16.11.0/27", comment, auth, authKey, type);

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

    private static Dictionary<string, string> WithComment(Dictionary<string, string> row, string comment)
    {
        Dictionary<string, string> copy = new(row) { ["comment"] = comment };
        return copy;
    }
}
