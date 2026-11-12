namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Explicit ownership classes for OSPF interface-template cleanup.
/// </summary>
internal enum OspfTemplateClassification
{
    ProtectedBackhaul = 0,
    ProtectedLoopback = 1,
    PlannedSiteMatcher = 2,
    KnownLegacy = 3,
    Unknown = 4,
}

/// <summary>
/// Sanitised inventory row used for cleanup classification (no live RouterOS dependency).
/// </summary>
internal sealed record OspfTemplateInventoryRow
{
    public string? Id { get; init; }
    public string? Area { get; init; }
    public string? Interfaces { get; init; }
    public string? Networks { get; init; }
    public string? Type { get; init; }
    public string? Cost { get; init; }
    public bool Passive { get; init; }
    public bool Disabled { get; init; }
    public string? Auth { get; init; }
    public string? AuthId { get; init; }
    public string? AuthKeyFingerprint { get; init; }
    public bool AuthKeyPresent { get; init; }
    public bool AuthKeyAmbiguous { get; init; }
    public string? Priority { get; init; }
    /// <summary>Raw <c>use-bfd</c> value from inventory; null means absent.</summary>
    public string? UseBfd { get; init; }
    public string? HelloInterval { get; init; }
    public string? DeadInterval { get; init; }
    public string? RetransmitInterval { get; init; }
    public string? TransmitDelay { get; init; }
    public string? InstanceId { get; init; }
    /// <summary>Resolved area.instance at inventory time; required for complete provenance match when recorded.</summary>
    public string? AreaInstanceName { get; init; }
    public string? PrefixList { get; init; }
    public string? VlinkNeighborId { get; init; }
    public string? VlinkTransitArea { get; init; }
    /// <summary>Exact comment; null means absent/empty.</summary>
    public string? Comment { get; init; }

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

internal sealed class OspfClassifiedTemplate
{
    public required OspfTemplateInventoryRow Row { get; init; }
    public required OspfTemplateClassification Classification { get; init; }
    public required string Reason { get; init; }
}

internal sealed class OspfTemplateClassificationReport
{
    public required IReadOnlyList<OspfClassifiedTemplate> Items { get; init; }
    public required IReadOnlyList<OspfClassifiedTemplate> Backhauls { get; init; }
    public required IReadOnlyList<OspfClassifiedTemplate> Loopbacks { get; init; }
    public required IReadOnlyList<OspfClassifiedTemplate> SiteMatchers { get; init; }

    public IReadOnlyList<OspfClassifiedTemplate> KnownLegacy =>
        Items.Where(static i => i.Classification == OspfTemplateClassification.KnownLegacy).ToList();

    public IReadOnlyList<OspfClassifiedTemplate> Unknown =>
        Items.Where(static i => i.Classification == OspfTemplateClassification.Unknown).ToList();

    public int PlannedProtectedCount => Backhauls.Count + Loopbacks.Count + SiteMatchers.Count;
}

/// <summary>
/// Classifies every OSPF interface-template before any destructive cleanup.
/// Unknown/unrelated rows abort cleanup; only positively identified legacy rows may be removed.
/// Protected roles are plan-derived (N backhauls / loopbacks / site matchers).
/// </summary>
internal static class OspfTemplateCleanupClassifier
{
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

    public static bool MatchesBackhaul(OspfTemplateInventoryRow row, OspfPlanBackhaulTemplate bh) =>
        string.Equals(row.Area, bh.AreaName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(row.Interfaces, bh.Interface, StringComparison.Ordinal)
        && string.Equals(NormalizeCidr(row.Networks), NormalizeCidr(bh.NetworkCidr), StringComparison.Ordinal);

    public static bool MatchesLoopback(OspfTemplateInventoryRow row, OspfPlanLoopbackTemplate loop) =>
        !row.Disabled
        && row.Passive
        && string.Equals(row.Area, loop.AreaName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeCidr(row.Networks), NormalizeCidr(loop.NetworkCidr), StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(loop.Interface)
            || string.IsNullOrWhiteSpace(row.Interfaces)
            || string.Equals(row.Interfaces, loop.Interface, StringComparison.Ordinal));

    public static bool MatchesPlannedSite(OspfTemplateInventoryRow row, OspfPlanSiteNetworkTemplate site) =>
        OspfSiteMatcherConsolidation.MatchesPlannedSite(
            row.Area,
            row.Interfaces,
            row.Networks,
            row.Passive,
            row.Disabled,
            site);

    public static string LegacyFingerprint(OspfTemplateInventoryRow row) =>
        OspfTemplateSemanticIdentity.Fingerprint(OspfTemplateSemanticIdentity.FromInventoryRow(row));

    public static HashSet<string> ProtectedIds(OspfTemplateClassificationReport report) =>
        report.Items
            .Where(static i =>
                i.Classification is OspfTemplateClassification.ProtectedBackhaul
                    or OspfTemplateClassification.ProtectedLoopback
                    or OspfTemplateClassification.PlannedSiteMatcher)
            .Select(static i => i.Row.Id!)
            .ToHashSet(StringComparer.Ordinal);

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

    private static string NormalizeCidr(string? value) =>
        OspfIpPrefix.TryParse(value, out OspfIpPrefix prefix) ? prefix.ToCidrString() : value?.Trim() ?? string.Empty;
}
