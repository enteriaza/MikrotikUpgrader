using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Deterministic area-range selection and prefix-folding regression coverage.
/// Exercises production <see cref="OspfSiteMatcherConsolidation"/> paths only.
/// </summary>
public sealed class OspfAreaRangeFoldingTests
{
    private const string SiteArea = "site-v2";

    [Theory]
    [InlineData("mgmt_before_broader")]
    [InlineData("mgmt_after_broader")]
    public void FoldPrefix_PrefersMgmt24_OverBroader16_RegardlessOfOrder(string ordering)
    {
        List<OspfPlanAreaRange> ranges = ordering == "mgmt_before_broader"
            ?
            [
                Mgmt("10.16.11.0/24"),
                Range("10.16.0.0/16"),
            ]
            :
            [
                Range("10.16.0.0/16"),
                Mgmt("10.16.11.0/24"),
            ];

        List<string> notes = [];
        string folded = OspfSiteMatcherConsolidation.FoldPrefix(
            "10.16.11.0/27",
            SiteArea,
            ranges,
            notes);

        Assert.Equal("10.16.11.0/24", folded);
        Assert.Contains(notes, n => n.Contains("10.16.11.0/24", StringComparison.Ordinal));
    }

    [Fact]
    public void Consolidate_SameInputs_DifferentAreaRangeOrder_ProduceIdenticalPlanNetworks()
    {
        List<OspfPlanSiteNetworkTemplate> sites =
        [
            new()
            {
                AreaName = SiteArea,
                Interfaces = "Bridge-VLAN-99_MGMT",
                NetworkCidr = "10.16.11.0/27",
                Passive = true,
            },
            new()
            {
                AreaName = SiteArea,
                Interfaces = "all",
                NetworkCidr = "100.64.10.0/24",
                Passive = true,
            },
            new()
            {
                AreaName = SiteArea,
                Interfaces = "all",
                NetworkCidr = "172.16.5.0/24",
                Passive = true,
            },
        ];

        List<OspfPlanAreaRange> mgmtFirst =
        [
            Mgmt("10.16.11.0/24"),
            Range("10.16.0.0/16"),
            Range("100.99.76.16/28"),
        ];
        List<OspfPlanAreaRange> broaderFirst =
        [
            Range("10.16.0.0/16"),
            Range("100.99.76.16/28"),
            Mgmt("10.16.11.0/24"),
        ];

        List<string> notesA = [];
        List<string> notesB = [];
        OspfSiteConsolidationResult a =
            OspfSiteMatcherConsolidation.ConsolidateWithProvenance(sites, mgmtFirst, notesA);
        OspfSiteConsolidationResult b =
            OspfSiteMatcherConsolidation.ConsolidateWithProvenance(sites, broaderFirst, notesB);

        Assert.Equal(a.Consolidated.Single().NetworkCidr, b.Consolidated.Single().NetworkCidr);
        Assert.Contains("10.16.11.0/24", a.Consolidated.Single().NetworkCidr, StringComparison.Ordinal);
        Assert.Contains("100.64.0.0/10", a.Consolidated.Single().NetworkCidr, StringComparison.Ordinal);
        Assert.Contains("172.16.0.0/12", a.Consolidated.Single().NetworkCidr, StringComparison.Ordinal);
        Assert.DoesNotContain("10.16.0.0/16", a.Consolidated.Single().NetworkCidr, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldPrefix_NestedRanges_SelectMostSpecific()
    {
        List<string> notes = [];
        string folded = OspfSiteMatcherConsolidation.FoldPrefix(
            "10.16.11.64/26",
            SiteArea,
            [
                Range("10.0.0.0/8"),
                Range("10.16.0.0/16"),
                Mgmt("10.16.11.0/24"),
            ],
            notes);

        Assert.Equal("10.16.11.0/24", folded);
    }

    [Fact]
    public void FoldPrefix_EqualPrefix_PrefersUniqueMgmtBoundary()
    {
        List<string> notes = [];
        string folded = OspfSiteMatcherConsolidation.FoldPrefix(
            "10.16.11.0/27",
            SiteArea,
            [
                Range("10.16.11.0/24", comment: "POOL"),
                Mgmt("10.16.11.0/24"),
            ],
            notes);

        Assert.Equal("10.16.11.0/24", folded);
        Assert.Contains(notes, n => n.Contains("selected MGMT boundary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FoldPrefix_OverlappingNonNested_SelectsOnlyContainingRange()
    {
        List<string> notes = [];
        string folded = OspfSiteMatcherConsolidation.FoldPrefix(
            "10.16.11.0/27",
            SiteArea,
            [
                Range("10.16.0.0/17"),
                Range("10.16.128.0/17"),
                Mgmt("10.16.11.0/24"),
            ],
            notes);

        Assert.Equal("10.16.11.0/24", folded);
    }

    [Fact]
    public void FoldPrefix_FleetAggregates_StillFoldIndependentlyOfAreaRanges()
    {
        List<string> notes = [];
        Assert.Equal(
            "100.64.0.0/10",
            OspfSiteMatcherConsolidation.FoldPrefix(
                "100.64.10.0/24",
                SiteArea,
                [Mgmt("10.16.11.0/24"), Range("10.16.0.0/16")],
                notes));
        Assert.Equal(
            "172.16.0.0/12",
            OspfSiteMatcherConsolidation.FoldPrefix(
                "172.16.5.0/24",
                SiteArea,
                [Mgmt("10.16.11.0/24"), Range("10.16.0.0/16")],
                notes));
    }

    [Fact]
    public void FoldPrefix_ManagementSubdivision_FoldsToMgmt24_NotBroader()
    {
        List<string> notes = [];
        foreach (string cidr in new[]
                 {
                     "10.16.11.0/27",
                     "10.16.11.32/27",
                     "10.16.11.64/26",
                     "10.16.11.128/26",
                     "10.16.11.192/26",
                 })
        {
            Assert.Equal(
                "10.16.11.0/24",
                OspfSiteMatcherConsolidation.FoldPrefix(
                    cidr,
                    SiteArea,
                    [Range("10.16.0.0/16"), Mgmt("10.16.11.0/24")],
                    notes));
        }
    }

    [Fact]
    public void SelectFoldAreaRange_AmbiguousEqualLengthDistinctCidrs_Throws()
    {
        OspfIpPrefix prefix = Parse("10.16.11.0/27");
        List<(OspfPlanAreaRange Range, OspfIpPrefix Prefix)> eligible =
        [
            (Range("10.16.10.0/24"), Parse("10.16.10.0/24")),
            (Range("10.16.11.0/24", comment: "POOL"), Parse("10.16.11.0/24")),
        ];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfSiteMatcherConsolidation.SelectFoldAreaRange(prefix, eligible));
        Assert.Contains("Ambiguous OSPF area-range hierarchy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unique MGMT boundary", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectFoldAreaRange_AmbiguousDuplicateMgmt_Throws()
    {
        OspfIpPrefix prefix = Parse("10.16.11.0/27");
        List<(OspfPlanAreaRange Range, OspfIpPrefix Prefix)> eligible =
        [
            (Mgmt("10.16.11.0/24"), Parse("10.16.11.0/24")),
            (Mgmt("10.16.11.0/24"), Parse("10.16.11.0/24")),
        ];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            OspfSiteMatcherConsolidation.SelectFoldAreaRange(prefix, eligible));
        Assert.Contains("Ambiguous OSPF area-range hierarchy", ex.Message, StringComparison.Ordinal);
    }

    private static OspfPlanAreaRange Mgmt(string cidr) =>
        new()
        {
            AreaName = SiteArea,
            RangeCidr = cidr,
            Comment = OspfFleetNaming.ManagementRangeComment,
            Advertise = true,
        };

    private static OspfPlanAreaRange Range(string cidr, string? comment = null) =>
        new()
        {
            AreaName = SiteArea,
            RangeCidr = cidr,
            Comment = comment,
            Advertise = true,
        };

    private static OspfIpPrefix Parse(string cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix prefix)
            ? prefix
            : throw new InvalidOperationException($"Invalid test prefix '{cidr}'.");
}
