// -----------------------------------------------------------------------------
// File: OspfAreaRangeFoldingTests.cs
//
// Description:
// Deterministic area-range selection and prefix-folding regression coverage.
// Exercises production paths only.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Deterministic area-range selection and prefix-folding regression coverage.
/// Exercises production <see cref="OspfSiteMatcherConsolidation"/> paths only.
/// </summary>
public sealed class OspfAreaRangeFoldingTests
{
    /// <summary>Name of the site area that owns the ranges under test.</summary>
    private const string SiteArea = "site-v2";

    /// <summary>
    /// Scenario: a /27 inside the site area has both a broader 10.16.0.0/16 range and the MGMT 10.16.11.0/24 range, listed in either order.
    /// Expected: the prefix folds to 10.16.11.0/24 and a note records the chosen range.
    /// Invariant: fold selection must depend on range specificity, never on the order the ranges were read from the router.
    /// </summary>
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

    /// <summary>
    /// Scenario: three passive site networks (MGMT /27, 100.64.10.0/24, 172.16.5.0/24) consolidated twice, once with the MGMT range first and once with the broader range first.
    /// Expected: both runs yield the same single network string containing 10.16.11.0/24, 100.64.0.0/10 and 172.16.0.0/12, and never the broader 10.16.0.0/16.
    /// Invariant: the generated plan must be deterministic regardless of area-range ordering.
    /// </summary>
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

    /// <summary>
    /// Scenario: a /26 is covered by nested ranges 10.0.0.0/8, 10.16.0.0/16 and MGMT 10.16.11.0/24.
    /// Expected: the most specific containing range, 10.16.11.0/24, is chosen.
    /// Invariant: nesting never promotes a broader range over a narrower one that still contains the prefix.
    /// </summary>
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

    /// <summary>
    /// Scenario: a POOL-commented range and the MGMT range share the identical 10.16.11.0/24 prefix.
    /// Expected: the fold returns 10.16.11.0/24 and a note says the MGMT boundary was selected.
    /// Invariant: a tie on prefix length is broken by the single MGMT-tagged range, not by list position.
    /// </summary>
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

    /// <summary>
    /// Scenario: two sibling /17 ranges (10.16.0.0/17 contains the /27, 10.16.128.0/17 does not) sit alongside the MGMT /24.
    /// Expected: the non-containing /17 is ignored and the most specific containing range wins, yielding 10.16.11.0/24.
    /// Invariant: unrelated or non-containing ranges must not influence the fold.
    /// </summary>
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

    /// <summary>
    /// Scenario: 100.64.10.0/24 and 172.16.5.0/24 are folded while the area only defines 10.16.x ranges.
    /// Expected: they fold to the fleet aggregates 100.64.0.0/10 and 172.16.0.0/12.
    /// Invariant: fleet-wide aggregate folding must not depend on any site area range being present.
    /// </summary>
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

    /// <summary>
    /// Scenario: every management subdivision (/27 and /26 blocks across 10.16.11.0-255) is folded with a 10.16.0.0/16 range and the MGMT /24 present.
    /// Expected: each fold returns 10.16.11.0/24.
    /// Invariant: management subnets always collapse to the MGMT /24 and never widen to the /16.
    /// </summary>
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

    /// <summary>
    /// Scenario: two eligible /24 ranges of equal length with distinct CIDRs and neither is a unique MGMT boundary.
    /// Expected: InvalidOperationException whose message mentions 'Ambiguous OSPF area-range hierarchy' and 'unique MGMT boundary'.
    /// Invariant: an ambiguous hierarchy must fail loudly instead of guessing which range to use.
    /// </summary>
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

    /// <summary>
    /// Scenario: two MGMT-tagged ranges with the same 10.16.11.0/24 prefix are both eligible.
    /// Expected: InvalidOperationException mentioning 'Ambiguous OSPF area-range hierarchy'.
    /// Invariant: duplicate MGMT boundaries are not silently de-duplicated.
    /// </summary>
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

    /// <summary>
    /// Creates a management area range for the site area with the given CIDR, tagged with the management range
    /// comment and set to advertise.
    /// </summary>
    private static OspfPlanAreaRange Mgmt(string cidr) =>
        new()
        {
            AreaName = SiteArea,
            RangeCidr = cidr,
            Comment = OspfFleetNaming.ManagementRangeComment,
            Advertise = true,
        };

    /// <summary>Creates an advertised plan area range in the site area.</summary>
    /// <param name="cidr">Range prefix in CIDR notation.</param>
    /// <param name="comment">Optional range comment.</param>
    private static OspfPlanAreaRange Range(string cidr, string? comment = null) =>
        new()
        {
            AreaName = SiteArea,
            RangeCidr = cidr,
            Comment = comment,
            Advertise = true,
        };

    /// <summary>Parses <paramref name="cidr"/> into a prefix, failing the test on invalid input.</summary>
    /// <param name="cidr">Prefix in CIDR notation.</param>
    /// <exception cref="InvalidOperationException">When <paramref name="cidr"/> is not a valid prefix.</exception>
    private static OspfIpPrefix Parse(string cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix prefix)
            ? prefix
            : throw new InvalidOperationException($"Invalid test prefix '{cidr}'.");
}
