// -----------------------------------------------------------------------------
// File: OspfTemplateCleanupClassifier.cs
//
// Description:
// Classifies live OSPF interface-templates for cleanup ownership (planned,
// known-legacy, obsolete, protected) before deletion.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Explicit ownership classes for OSPF interface-template cleanup.
/// </summary>
internal enum OspfTemplateClassification
{
    /// <summary>Live backhaul template that must not be removed.</summary>
    ProtectedBackhaul = 0,

    /// <summary>Live loopback template that must not be removed.</summary>
    ProtectedLoopback = 1,

    /// <summary>Live template that matches the planned consolidated site matcher.</summary>
    PlannedSiteMatcher = 2,

    /// <summary>Live template identified as known-legacy and eligible for cleanup.</summary>
    KnownLegacy = 3,

    /// <summary>Live template with no established replacement relationship (fail closed).</summary>
    Unknown = 4,
}

/// <summary>
/// Sanitised inventory row used for cleanup classification (no live RouterOS dependency).
/// </summary>
internal sealed record OspfTemplateInventoryRow
{
    /// <summary>RouterOS <c>.id</c> of the interface-template row; null when the inventory row had none (such rows abort cleanup).</summary>
    public string? Id { get; init; }
    /// <summary>Name of the OSPF area the template belongs to (<c>area</c>).</summary>
    public string? Area { get; init; }
    /// <summary>Interface matcher (<c>interfaces</c>): a comma-separated interface list, <c>all</c>, or null when the template is network-only.</summary>
    public string? Interfaces { get; init; }
    /// <summary>Network matcher (<c>networks</c>): a single CIDR or a comma-separated set of prefixes.</summary>
    public string? Networks { get; init; }
    /// <summary>Raw OSPF network <c>type</c> (for example <c>ptp</c>, <c>broadcast</c>); null when unset on the router.</summary>
    public string? Type { get; init; }
    /// <summary>Raw OSPF <c>cost</c> text; null when unset (RouterOS default applies).</summary>
    public string? Cost { get; init; }
    /// <summary>Whether the template is passive (advertises the network without forming adjacencies).</summary>
    public bool Passive { get; init; }
    /// <summary>Whether the template is administratively disabled.</summary>
    public bool Disabled { get; init; }
    /// <summary>Raw OSPF authentication method (<c>auth</c>); null when unset.</summary>
    public string? Auth { get; init; }
    /// <summary>Raw authentication key id (<c>auth-id</c>); null when unset.</summary>
    public string? AuthId { get; init; }
    /// <summary>SHA-256 fingerprint of the auth key; the plaintext key is never stored. Meaningful only when <see cref="AuthKeyPresent"/> is true.</summary>
    public string? AuthKeyFingerprint { get; init; }
    /// <summary>True when the live template carries an authentication key.</summary>
    public bool AuthKeyPresent { get; init; }
    /// <summary>True when <c>auth-key</c> and <c>authentication-key</c> held conflicting values, so no single fingerprint could be derived.</summary>
    public bool AuthKeyAmbiguous { get; init; }
    /// <summary>Raw OSPF router <c>priority</c> text; null when unset.</summary>
    public string? Priority { get; init; }
    /// <summary>Raw <c>use-bfd</c> value from inventory; null means absent.</summary>
    public string? UseBfd { get; init; }
    /// <summary>Raw <c>hello-interval</c> text (RouterOS time format); null when unset.</summary>
    public string? HelloInterval { get; init; }
    /// <summary>Raw <c>dead-interval</c> text (RouterOS time format); null when unset.</summary>
    public string? DeadInterval { get; init; }
    /// <summary>Raw <c>retransmit-interval</c> text (RouterOS time format); null when unset.</summary>
    public string? RetransmitInterval { get; init; }
    /// <summary>Raw <c>transmit-delay</c> text (RouterOS time format); null when unset.</summary>
    public string? TransmitDelay { get; init; }
    /// <summary>Raw protocol <c>instance-id</c> text; null when unset.</summary>
    public string? InstanceId { get; init; }
    /// <summary>Resolved area.instance at inventory time; required for complete provenance match when recorded.</summary>
    public string? AreaInstanceName { get; init; }
    /// <summary>Name of the associated <c>prefix-list</c>; null when unset.</summary>
    public string? PrefixList { get; init; }
    /// <summary>Remote router id of a virtual link (<c>vlink-neighbor-id</c>); null for non-virtual-link templates.</summary>
    public string? VlinkNeighborId { get; init; }
    /// <summary>Transit area of a virtual link (<c>vlink-transit-area</c>); null for non-virtual-link templates.</summary>
    public string? VlinkTransitArea { get; init; }
    /// <summary>Exact comment; null means absent/empty.</summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Formats a single-line diagnostic description of the row for refusal messages and logs.
    /// </summary>
    /// <remarks>
    /// Secrets are never printed: the auth key is reduced to <c>&lt;set&gt;</c>, <c>&lt;absent&gt;</c> or <c>&lt;ambiguous&gt;</c>, and the comment to <c>&lt;set&gt;</c> or <c>&lt;absent&gt;</c>.
    /// </remarks>
    /// <returns>A comma-separated <c>name=value</c> string covering every semantic field of the row.</returns>
    public string Describe() =>
        $".id={Id ?? "<missing>"}, area={Area ?? "<none>"}, area-instance={AreaInstanceName ?? "<none>"}, "
        + $"interfaces={Interfaces ?? "<none>"}, networks={Networks ?? "<none>"}, "
        + $"passive={Passive}, disabled={Disabled}, type={Type ?? "<none>"}, cost={Cost ?? "<none>"}, "
        + $"auth={Auth ?? "<none>"}, auth-id={AuthId ?? "<none>"}, "
        + $"auth-key={(AuthKeyAmbiguous ? "<ambiguous>" : AuthKeyPresent ? "<set>" : "<absent>")}, "
        + $"priority={Priority ?? "<none>"}, use-bfd={UseBfd ?? "<absent>"}, "
        + $"hello={HelloInterval ?? "<none>"}, dead={DeadInterval ?? "<none>"}, "
        + $"retransmit={RetransmitInterval ?? "<none>"}, transmit-delay={TransmitDelay ?? "<none>"}, "
        + $"instance-id={InstanceId ?? "<none>"}, prefix-list={PrefixList ?? "<none>"}, "
        + $"vlink-neighbor={VlinkNeighborId ?? "<none>"}, vlink-area={VlinkTransitArea ?? "<none>"}, "
        + $"comment={(Comment is null ? "<absent>" : "<set>")}";
}

/// <summary>
/// A live template paired with its cleanup ownership classification.
/// </summary>
internal sealed class OspfClassifiedTemplate
{
    /// <summary>The live inventory row that was classified.</summary>
    public required OspfTemplateInventoryRow Row { get; init; }
    /// <summary>Ownership class assigned to <see cref="Row"/>; drives whether cleanup may delete it.</summary>
    public required OspfTemplateClassification Classification { get; init; }
    /// <summary>Human-readable explanation of why the row received its classification (used in refusal messages and audit output).</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Full classification report for live templates vs the plan.
/// </summary>
internal sealed class OspfTemplateClassificationReport
{
    /// <summary>Every live template with its classification, in inventory order.</summary>
    public required IReadOnlyList<OspfClassifiedTemplate> Items { get; init; }
    /// <summary>Subset of <see cref="Items"/> classified as protected active backhauls (one per planned backhaul).</summary>
    public required IReadOnlyList<OspfClassifiedTemplate> Backhauls { get; init; }
    /// <summary>Subset of <see cref="Items"/> classified as protected passive loopbacks (one per planned loopback).</summary>
    public required IReadOnlyList<OspfClassifiedTemplate> Loopbacks { get; init; }
    /// <summary>Subset of <see cref="Items"/> classified as planned consolidated site matchers (one per planned site network).</summary>
    public required IReadOnlyList<OspfClassifiedTemplate> SiteMatchers { get; init; }

    /// <summary>Templates positively identified as legacy/superseded and therefore eligible for removal. Computed on each access.</summary>
    public IReadOnlyList<OspfClassifiedTemplate> KnownLegacy =>
        Items.Where(static i => i.Classification == OspfTemplateClassification.KnownLegacy).ToList();

    /// <summary>Templates with no established ownership. Any entry here makes cleanup fail closed. Computed on each access.</summary>
    public IReadOnlyList<OspfClassifiedTemplate> Unknown =>
        Items.Where(static i => i.Classification == OspfTemplateClassification.Unknown).ToList();

    /// <summary>Total number of live templates that must be preserved because the plan owns them (backhauls + loopbacks + site matchers).</summary>
    public int PlannedProtectedCount => Backhauls.Count + Loopbacks.Count + SiteMatchers.Count;
}

/// <summary>
/// Classifies every OSPF interface-template before any destructive cleanup.
/// Unknown/unrelated rows abort cleanup; only positively identified legacy rows may be removed.
/// Protected roles are plan-derived (N backhauls / loopbacks / site matchers).
/// </summary>
internal static class OspfTemplateCleanupClassifier
{
    /// <summary>
    /// Classifies every live template against the migration plan and throws unless the result is safe to act on.
    /// </summary>
    /// <param name="plan">Migration plan that defines the protected backhaul, loopback and site-matcher roles.</param>
    /// <param name="rows">Sanitised live interface-template inventory.</param>
    /// <returns>A report in which every row is a protected planned role or known-legacy; it contains no unknown rows.</returns>
    /// <exception cref="InvalidOperationException">
    /// A row has no <c>.id</c>, the plan has no active backhaul, a planned role matches zero or several live templates,
    /// planned role counts disagree after classification, or any row is unknown. No deletion has been attempted in any of these cases.
    /// </exception>
    /// <exception cref="OspfBackboneMutationGuardException">A protected backhaul matches several templates or collides with another planned role.</exception>
    public static OspfTemplateClassificationReport ClassifyOrThrow(
        OspfMigrationPlan plan,
        IReadOnlyList<OspfTemplateInventoryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Any(static r => string.IsNullOrWhiteSpace(r.Id)))
        {
            throw new InvalidOperationException(
                "OSPF interface-template inventory contains a row without .id; refusing cleanup. No deletion was attempted.");
        }

        if (plan.ActiveBackhauls.Count == 0)
        {
            throw new InvalidOperationException(
                "Cleanup requires at least one planned backhaul. No deletion was attempted.");
        }

        Dictionary<string, OspfClassifiedTemplate> byId = new(StringComparer.Ordinal);

        foreach (OspfPlanBackhaulTemplate bh in plan.ActiveBackhauls)
        {
            List<OspfTemplateInventoryRow> matches = rows.Where(r => MatchesBackhaul(r, bh)).ToList();
            if (matches.Count != 1)
            {
                throw new OspfBackboneMutationGuardException(
                    $"Protected backhaul '{bh.Interface}' {bh.NetworkCidr} matches {matches.Count} live templates; refusing cleanup. No deletion was attempted.");
            }

            ValidatePlannedBackhaulProperties(matches[0], bh);
            string id = matches[0].Id!;
            if (!byId.TryAdd(
                    id,
                    new OspfClassifiedTemplate
                    {
                        Row = matches[0],
                        Classification = OspfTemplateClassification.ProtectedBackhaul,
                        Reason = $"Unique planned active backhaul '{bh.Interface}' {bh.NetworkCidr}.",
                    }))
            {
                throw new OspfBackboneMutationGuardException(
                    $"Protected backhaul .id={id} collides with another planned role; refusing cleanup. No deletion was attempted.");
            }
        }

        foreach (OspfPlanLoopbackTemplate loop in plan.PassiveLoopbacks)
        {
            List<OspfTemplateInventoryRow> matches = rows.Where(r => MatchesLoopback(r, loop)).ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Protected loopback '{loop.NetworkCidr}' matches {matches.Count} live templates; refusing cleanup. No deletion was attempted.");
            }

            ValidatePlannedLoopbackProperties(matches[0], loop);
            string id = matches[0].Id!;
            if (!byId.TryAdd(
                    id,
                    new OspfClassifiedTemplate
                    {
                        Row = matches[0],
                        Classification = OspfTemplateClassification.ProtectedLoopback,
                        Reason = $"Unique planned passive loopback '{loop.NetworkCidr}'.",
                    }))
            {
                throw new InvalidOperationException(
                    $"Protected loopback .id={id} collides with another planned role; refusing cleanup. No deletion was attempted.");
            }
        }

        foreach (OspfPlanSiteNetworkTemplate site in plan.PassiveSiteNetworks)
        {
            List<OspfTemplateInventoryRow> matches = rows.Where(r => MatchesPlannedSite(r, site)).ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Planned site matcher '{site.AreaName}' interfaces={site.Interfaces} networks={site.NetworkCidr} matches {matches.Count} live templates; refusing cleanup. No deletion was attempted.");
            }

            ValidatePlannedSiteProperties(matches[0], site);
            string id = matches[0].Id!;
            if (!byId.TryAdd(
                    id,
                    new OspfClassifiedTemplate
                    {
                        Row = matches[0],
                        Classification = OspfTemplateClassification.PlannedSiteMatcher,
                        Reason =
                            $"Unique planned site matcher area={site.AreaName} interfaces={site.Interfaces} networks={site.NetworkCidr}.",
                    }))
            {
                throw new InvalidOperationException(
                    $"Planned site matcher .id={id} collides with another planned role; refusing cleanup. No deletion was attempted.");
            }
        }

        List<OspfClassifiedTemplate> classified = [];
        foreach (OspfTemplateInventoryRow row in rows)
        {
            if (byId.TryGetValue(row.Id!, out OspfClassifiedTemplate? planned))
            {
                classified.Add(planned);
                continue;
            }

            if (TryExplainKnownLegacy(row, plan, out string legacyReason))
            {
                classified.Add(new OspfClassifiedTemplate
                {
                    Row = row,
                    Classification = OspfTemplateClassification.KnownLegacy,
                    Reason = legacyReason,
                });
                continue;
            }

            classified.Add(new OspfClassifiedTemplate
            {
                Row = row,
                Classification = OspfTemplateClassification.Unknown,
                Reason =
                    "Not a planned template and no consolidated-prefix replacement "
                    + "(same-area more-specific containment), desired-state relationship, "
                    + "or recorded superseded provenance could be established.",
            });
        }

        OspfTemplateClassificationReport report = new()
        {
            Items = classified,
            Backhauls = classified
                .Where(static c => c.Classification == OspfTemplateClassification.ProtectedBackhaul)
                .ToList(),
            Loopbacks = classified
                .Where(static c => c.Classification == OspfTemplateClassification.ProtectedLoopback)
                .ToList(),
            SiteMatchers = classified
                .Where(static c => c.Classification == OspfTemplateClassification.PlannedSiteMatcher)
                .ToList(),
        };

        if (report.Backhauls.Count != plan.ActiveBackhauls.Count
            || report.Loopbacks.Count != plan.PassiveLoopbacks.Count
            || report.SiteMatchers.Count != plan.PassiveSiteNetworks.Count)
        {
            throw new InvalidOperationException(
                $"Planned role counts mismatch after classification "
                + $"(bh={report.Backhauls.Count}/{plan.ActiveBackhauls.Count}, "
                + $"loop={report.Loopbacks.Count}/{plan.PassiveLoopbacks.Count}, "
                + $"site={report.SiteMatchers.Count}/{plan.PassiveSiteNetworks.Count}). No deletion was attempted.");
        }

        if (report.Unknown.Count > 0)
        {
            string detail = string.Join(
                " | ",
                report.Unknown.Select(static u => $"{u.Row.Describe()}; reason={u.Reason}"));
            throw new InvalidOperationException(
                $"Refusing OSPF template cleanup: {report.Unknown.Count} unknown/unrelated template(s) present. "
                + "No deletion was attempted. "
                + detail);
        }

        return report;
    }

    /// <summary>Returns true when the row is the live template for the planned backhaul: same area (case-insensitive), exact interface name, and same normalised network CIDR.</summary>
    public static bool MatchesBackhaul(OspfTemplateInventoryRow row, OspfPlanBackhaulTemplate bh) =>
        string.Equals(row.Area, bh.AreaName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(row.Interfaces, bh.Interface, StringComparison.Ordinal)
        && string.Equals(NormalizeCidr(row.Networks), NormalizeCidr(bh.NetworkCidr), StringComparison.Ordinal);

    /// <summary>Returns true when the row is an enabled, passive template for the planned loopback: same area, same normalised CIDR, and a compatible interface (either side blank, or equal).</summary>
    public static bool MatchesLoopback(OspfTemplateInventoryRow row, OspfPlanLoopbackTemplate loop) =>
        !row.Disabled
        && row.Passive
        && string.Equals(row.Area, loop.AreaName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeCidr(row.Networks), NormalizeCidr(loop.NetworkCidr), StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(loop.Interface)
            || string.IsNullOrWhiteSpace(row.Interfaces)
            || string.Equals(row.Interfaces, loop.Interface, StringComparison.Ordinal));

    /// <summary>Returns true when the row matches the planned consolidated site matcher (area, interfaces, networks, passive and enabled state) as judged by <see cref="OspfSiteMatcherConsolidation"/>.</summary>
    public static bool MatchesPlannedSite(OspfTemplateInventoryRow row, OspfPlanSiteNetworkTemplate site) =>
        OspfSiteMatcherConsolidation.MatchesPlannedSite(
            row.Area,
            row.Interfaces,
            row.Networks,
            row.Passive,
            row.Disabled,
            site);

    /// <summary>
    /// Computes the stable semantic fingerprint of a live row (see <see cref="OspfTemplateSemanticIdentity.Fingerprint"/>).
    /// </summary>
    /// <param name="row">Inventory row to fingerprint.</param>
    /// <returns>A pipe-delimited canonical identity string; auth keys contribute only their hash.</returns>
    public static string LegacyFingerprint(OspfTemplateInventoryRow row) =>
        OspfTemplateSemanticIdentity.Fingerprint(OspfTemplateSemanticIdentity.FromInventoryRow(row));

    /// <summary>
    /// Collects the <c>.id</c> values of every row the plan owns and cleanup must never delete.
    /// </summary>
    /// <param name="report">Classification report to read.</param>
    /// <returns>Ids of protected backhaul, protected loopback and planned site-matcher rows (ordinal comparison).</returns>
    public static HashSet<string> ProtectedIds(OspfTemplateClassificationReport report) =>
        report.Items
            .Where(static i =>
                i.Classification is OspfTemplateClassification.ProtectedBackhaul
                    or OspfTemplateClassification.ProtectedLoopback
                    or OspfTemplateClassification.PlannedSiteMatcher)
            .Select(static i => i.Row.Id!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Decides whether a row not owned by the plan is positively identified as legacy, and explains why.
    /// </summary>
    /// <remarks>
    /// Evaluation order: planned roles are never legacy; then same-area containment under a consolidated
    /// <c>interfaces=all</c> prefix; then desired-state supersession; then recorded superseded provenance (exact semantic match).
    /// </remarks>
    /// <param name="row">Live row to evaluate.</param>
    /// <param name="plan">Migration plan providing planned roles and recorded superseded identities.</param>
    /// <param name="reason">Receives the justification when the method returns true; otherwise empty.</param>
    /// <returns>True when one of the positive legacy rules matched.</returns>
    private static bool TryExplainKnownLegacy(
        OspfTemplateInventoryRow row,
        OspfMigrationPlan plan,
        out string reason)
    {
        reason = string.Empty;

        // Never treat planned roles as legacy even if properties partially drift.
        if (plan.ActiveBackhauls.Any(b => MatchesBackhaul(row, b))
            || plan.PassiveLoopbacks.Any(l => MatchesLoopback(row, l))
            || plan.PassiveSiteNetworks.Any(s => MatchesPlannedSite(row, s)))
        {
            return false;
        }

        // Primary rule: same-area strictly more-specific networks under a planned consolidated
        // interfaces=all prefix are obsolete. No provenance or semantic attribute matching required.
        if (OspfConsolidatedPrefixObsolescence.TryExplain(row, plan, out string containmentReason))
        {
            reason = containmentReason;
            return true;
        }

        // Additional path: richer desired-state / conversion relationships (fleet aggregates,
        // interface consolidation with semantic compatibility).
        if (OspfLiveSupersededProvenance.TryExplainLiveSupersession(row, plan, out string desiredReason))
        {
            reason = "Desired-state replacement: " + desiredReason;
            return true;
        }

        // Planner-recorded complete semantic provenance remains an additional positive path
        // (exact fingerprint match) for identities captured before consolidation.
        if (plan.SupersededTemplates.Count == 0)
        {
            return false;
        }

        OspfSupersededTemplateIdentity? match = plan.SupersededTemplates.FirstOrDefault(s =>
            OspfSiteMatcherConsolidation.MatchesSupersededIdentity(row, s));
        if (match is null)
        {
            return false;
        }

        reason =
            $"Matches migration-superseded complete semantic provenance "
            + $"(area={match.AreaName}, area-instance={match.AreaInstanceName ?? "<none>"}, "
            + $"interfaces={match.Interfaces ?? "<none>"}, networks={match.NetworkCidr}, "
            + $"passive={match.Passive}, disabled={match.Disabled}, type={match.Type}, cost={match.Cost}, "
            + $"auth={match.Auth}, auth-id={match.AuthId ?? "<none>"}, "
            + $"auth-key={(match.AuthKeyAmbiguous ? "<ambiguous>" : match.AuthKeyPresent ? "<set>" : "<absent>")}, "
            + $"priority={match.Priority}, use-bfd={match.UseBfd}, "
            + $"hello={match.HelloInterval}, dead={match.DeadInterval}, "
            + $"retransmit={match.RetransmitInterval}, transmit-delay={match.TransmitDelay}, "
            + $"instance-id={match.InstanceId}, comment={(match.Comment is null ? "<absent>" : "<set>")}).";
        return true;
    }

    /// <summary>Verifies the live backhaul row is enabled, non-passive, point-to-point and uses the fleet backhaul cost; throws <see cref="InvalidOperationException"/> otherwise.</summary>
    private static void ValidatePlannedBackhaulProperties(
        OspfTemplateInventoryRow row,
        OspfPlanBackhaulTemplate bh)
    {
        if (row.Disabled)
        {
            throw new InvalidOperationException(
                $"Protected backhaul '{bh.Interface}' {bh.NetworkCidr} is disabled; refusing cleanup. No deletion was attempted.");
        }

        if (row.Passive)
        {
            throw new InvalidOperationException(
                $"Protected backhaul '{bh.Interface}' {bh.NetworkCidr} is passive; refusing cleanup. No deletion was attempted.");
        }

        if (!string.Equals(row.Type, "ptp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.Type, "point-to-point", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Protected backhaul '{bh.Interface}' type is '{row.Type}', expected ptp; refusing cleanup. No deletion was attempted.");
        }

        if (!string.Equals(row.Cost, OspfMigrationPlanner.FleetBackhaulCost.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Protected backhaul '{bh.Interface}' cost is '{row.Cost}', expected {OspfMigrationPlanner.FleetBackhaulCost}; refusing cleanup. No deletion was attempted.");
        }
    }

    /// <summary>Verifies the live loopback row is enabled, passive and not point-to-point; throws <see cref="InvalidOperationException"/> otherwise.</summary>
    private static void ValidatePlannedLoopbackProperties(
        OspfTemplateInventoryRow row,
        OspfPlanLoopbackTemplate loop)
    {
        if (row.Disabled || !row.Passive)
        {
            throw new InvalidOperationException(
                $"Protected loopback '{loop.NetworkCidr}' must be enabled and passive; refusing cleanup. No deletion was attempted.");
        }

        if (!string.IsNullOrWhiteSpace(row.Type)
            && (row.Type.Equals("ptp", StringComparison.OrdinalIgnoreCase)
                || row.Type.Equals("point-to-point", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Protected loopback '{loop.NetworkCidr}' must not be PTP; refusing cleanup. No deletion was attempted.");
        }
    }

    /// <summary>Verifies the live site-matcher row is enabled and passive and that its interfaces and networks equal the planned values; throws <see cref="InvalidOperationException"/> otherwise.</summary>
    private static void ValidatePlannedSiteProperties(
        OspfTemplateInventoryRow row,
        OspfPlanSiteNetworkTemplate site)
    {
        if (row.Disabled || !row.Passive)
        {
            throw new InvalidOperationException(
                $"Planned site matcher '{site.NetworkCidr}' must be enabled and passive; refusing cleanup. No deletion was attempted.");
        }

        if (!OspfSiteMatcherConsolidation.InterfacesEqual(row.Interfaces, site.Interfaces))
        {
            throw new InvalidOperationException(
                $"Planned site matcher interfaces mismatch live='{row.Interfaces}' plan='{site.Interfaces}'; refusing cleanup. No deletion was attempted.");
        }

        if (!OspfNetworkList.SetsEqual(row.Networks, site.NetworkCidr))
        {
            throw new InvalidOperationException(
                $"Planned site matcher networks mismatch live='{row.Networks}' plan='{site.NetworkCidr}'; refusing cleanup. No deletion was attempted.");
        }
    }

    /// <summary>Normalises a CIDR string to canonical form; unparseable input is returned trimmed (or empty when null) so callers still get a comparable value.</summary>
    private static string NormalizeCidr(string? value) =>
        OspfIpPrefix.TryParse(value, out OspfIpPrefix prefix) ? prefix.ToCidrString() : value?.Trim() ?? string.Empty;
}
