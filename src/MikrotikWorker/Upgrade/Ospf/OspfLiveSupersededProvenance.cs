// -----------------------------------------------------------------------------
// File: OspfLiveSupersededProvenance.cs
//
// Description:
// Enriches a remapped migration plan with KnownLegacy provenance discovered
// from the live ROS7 inventory. Pre-upgrade (ROS6) plans cannot foresee
// auto-converted per-VLAN site templates; apply-time enrichment records their
// complete semantic identity so cleanup can delete only templates proven
// superseded by the planned consolidated site matcher.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Enriches a remapped migration plan with KnownLegacy provenance discovered from the live
/// ROS7 inventory. Pre-upgrade (ROS6) plans cannot foresee auto-converted per-VLAN site
/// templates; apply-time enrichment records their complete semantic identity so cleanup can
/// delete only templates proven superseded by the planned consolidated site matcher.
/// </summary>
internal static class OspfLiveSupersededProvenance
{
    /// <summary>
    /// Returns a plan whose <see cref="OspfMigrationPlan.SupersededTemplates"/> includes every
    /// live site-area template that is safely superseded by a planned passive site matcher.
    /// Existing provenance is preserved; fingerprints already present are not duplicated.
    /// </summary>
    public static OspfMigrationPlan EnrichFromLiveInventory(
        OspfMigrationPlan livePlan,
        OspfV7Inventory inventory)
    {
        ArgumentNullException.ThrowIfNull(livePlan);
        ArgumentNullException.ThrowIfNull(inventory);

        Dictionary<string, string> areaInstances = BuildAreaInstanceMap(inventory.Areas);
        List<OspfSupersededTemplateIdentity> merged = livePlan.SupersededTemplates.ToList();
        HashSet<string> knownFingerprints = merged
            .Select(OspfTemplateSemanticIdentity.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);

        List<string> notes = livePlan.NormalisationNotes.ToList();
        int added = 0;

        foreach (OspfInterfaceTemplateRow template in inventory.Templates)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
            {
                continue;
            }

            OspfTemplateInventoryRow row = ToInventoryRow(template, areaInstances);
            if (!TryExplainLiveSupersession(row, livePlan, out string reason))
            {
                continue;
            }

            OspfSupersededTemplateIdentity identity = OspfTemplateSemanticIdentity.FromInventoryRow(row);
            string fingerprint = OspfTemplateSemanticIdentity.Fingerprint(identity);
            if (!knownFingerprints.Add(fingerprint))
            {
                continue;
            }

            merged.Add(identity);
            added++;
            notes.Add($"Live superseded provenance: {reason}");
        }

        if (added == 0)
        {
            return livePlan;
        }

        notes.Add(
            $"Enriched {added} live template identity(ies) as KnownLegacy provenance for cleanup "
            + "(ROS7 auto-converted / subdivided site matchers superseded by planned consolidation).");

        return CloneWithSuperseded(livePlan, merged, notes);
    }

    /// <summary>
    /// True when a live inventory row is a site-role template fully covered by a planned
    /// consolidated site matcher in the same area, and is not itself a planned protected role.
    /// </summary>
    public static bool TryExplainLiveSupersession(
        OspfTemplateInventoryRow row,
        OspfMigrationPlan livePlan,
        out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(row.Area) || string.IsNullOrWhiteSpace(row.Networks))
        {
            return false;
        }

        // Never treat planned protected roles as superseded.
        if (livePlan.ActiveBackhauls.Any(b => OspfTemplateCleanupClassifier.MatchesBackhaul(row, b))
            || livePlan.PassiveLoopbacks.Any(l => OspfTemplateCleanupClassifier.MatchesLoopback(row, l))
            || livePlan.PassiveSiteNetworks.Any(s => OspfTemplateCleanupClassifier.MatchesPlannedSite(row, s)))
        {
            return false;
        }

        // Simple same-area consolidated-prefix containment (no semantic attribute matching).
        if (OspfConsolidatedPrefixObsolescence.TryExplain(row, livePlan, out reason))
        {
            return true;
        }

        // Site supersession applies to passive (or disabled leftover) matchers only.
        // Active templates in a site area are fail-closed Unknown unless they match a planned backhaul.
        if (!row.Passive && !row.Disabled)
        {
            return false;
        }

        // Area must belong to the live plan instance when instance ownership is known.
        if (!string.IsNullOrWhiteSpace(row.AreaInstanceName)
            && !string.IsNullOrWhiteSpace(livePlan.InstanceName)
            && !string.Equals(
                row.AreaInstanceName,
                livePlan.InstanceName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Ambiguous auth-key semantics cannot be owned safely.
        if (row.AuthKeyAmbiguous
            || OspfTemplateSemanticIdentity.HasInvalidSemanticTokens(
                OspfTemplateSemanticIdentity.FromInventoryRow(row)))
        {
            return false;
        }

        // Non-site roles must remain Unknown (fail closed).
        if (!string.IsNullOrWhiteSpace(row.PrefixList)
            || !string.IsNullOrWhiteSpace(row.VlinkNeighborId)
            || !string.IsNullOrWhiteSpace(row.VlinkTransitArea)
            || string.Equals(
                OspfTemplateSemanticIdentity.NormalizeType(row.Type),
                OspfTemplateSemanticIdentity.VirtualLinkTypeToken,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!OspfNetworkList.TryParseAll(row.Networks, out List<OspfIpPrefix> liveNets, out _)
            || liveNets.Count == 0)
        {
            return false;
        }

        OspfIpPrefix? derivedMgmt = null;
        if (!string.IsNullOrWhiteSpace(livePlan.DerivedManagementCidr)
            && OspfIpPrefix.TryParse(livePlan.DerivedManagementCidr, out OspfIpPrefix mgmtParsed))
        {
            derivedMgmt = mgmtParsed;
        }

        foreach (OspfPlanSiteNetworkTemplate planned in livePlan.PassiveSiteNetworks)
        {
            if (!string.Equals(row.Area, planned.AreaName, StringComparison.OrdinalIgnoreCase)
                || !planned.Passive)
            {
                continue;
            }

            if (!OspfNetworkList.TryParseAll(planned.NetworkCidr, out List<OspfIpPrefix> plannedNets, out _)
                || plannedNets.Count == 0)
            {
                continue;
            }

            if (!AllNetworksCoveredByPlanned(liveNets, plannedNets))
            {
                continue;
            }

            if (OspfSiteMatcherConsolidation.MatchesPlannedSite(
                    row.Area,
                    row.Interfaces,
                    row.Networks,
                    row.Passive,
                    row.Disabled,
                    planned))
            {
                return false;
            }

            // CIDR containment alone is insufficient. Require a planner-intended replacement
            // relationship: management summary, fleet aggregate fold, or interfaces=all consolidation.
            if (!TryExplainReplacementRelationship(
                    liveNets,
                    planned,
                    plannedNets,
                    row,
                    derivedMgmt,
                    out string relationship))
            {
                continue;
            }

            reason =
                $"Live template area={row.Area} interfaces={row.Interfaces ?? "<none>"} "
                + $"networks={row.Networks} is superseded by planned site matcher "
                + $"area={planned.AreaName} interfaces={planned.Interfaces} networks={planned.NetworkCidr} "
                + $"({relationship}).";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Distinguishes intended consolidation/summarisation replacements from incidental containment
    /// under an unrelated broader prefix, and requires the live template to be semantically
    /// represented by the planned consolidated site matcher (with explicit known ROS conversions).
    /// </summary>
    public static bool TryExplainReplacementRelationship(
        IReadOnlyList<OspfIpPrefix> liveNets,
        OspfPlanSiteNetworkTemplate planned,
        IReadOnlyList<OspfIpPrefix> plannedNets,
        OspfTemplateInventoryRow row,
        OspfIpPrefix? derivedManagementCidr,
        out string relationship)
    {
        relationship = string.Empty;

        if (!TryExplainStructuralReplacement(
                liveNets,
                planned,
                plannedNets,
                row,
                derivedManagementCidr,
                out string structural))
        {
            return false;
        }

        if (!TryExplainSemanticCompatibility(row, planned, out string semantic))
        {
            return false;
        }

        relationship = $"{structural}; {semantic}";
        return true;
    }

    /// <summary>
    /// Structural network/interface replacement only. Callers must also establish semantic
    /// compatibility before treating a template as superseded.
    /// </summary>
    public static bool TryExplainStructuralReplacement(
        IReadOnlyList<OspfIpPrefix> liveNets,
        OspfPlanSiteNetworkTemplate planned,
        IReadOnlyList<OspfIpPrefix> plannedNets,
        OspfTemplateInventoryRow row,
        OspfIpPrefix? derivedManagementCidr,
        out string relationship)
    {
        relationship = string.Empty;

        // Fleet consolidation target is always interfaces=all.
        if (!OspfSiteMatcherConsolidation.IsAllInterfaces(planned.Interfaces))
        {
            return false;
        }

        bool liveIsAll = OspfSiteMatcherConsolidation.IsAllInterfaces(row.Interfaces);
        bool liveIsSpecific = !liveIsAll && !string.IsNullOrWhiteSpace(row.Interfaces);
        if (!liveIsAll && !liveIsSpecific)
        {
            // Neither specific nor all — cannot establish an interface replacement relationship.
            return false;
        }

        // Management summarisation boundary: DerivedManagementCidr, or a planned prefix that is
        // itself the explicit MGMT area-range / derived summary present in the planned networks.
        if (TryExplainManagementMembership(
                liveNets,
                plannedNets,
                derivedManagementCidr,
                liveIsSpecific,
                out string mgmtRelationship))
        {
            relationship = mgmtRelationship;
            return true;
        }

        if (liveNets.All(n =>
                OspfSiteMatcherConsolidation.SharedAddressSpace.Equals(n)
                || OspfSiteMatcherConsolidation.SharedAddressSpace.Contains(n)
                || OspfSiteMatcherConsolidation.Private172Aggregate.Equals(n)
                || OspfSiteMatcherConsolidation.Private172Aggregate.Contains(n)))
        {
            relationship =
                "fleet aggregate fold: live networks fall under 100.64.0.0/10 or 172.16.0.0/12 "
                + $"with {(liveIsSpecific ? "specific→all" : "interfaces=all")} interface replacement";
            return true;
        }

        // Classic ROS7 auto-converted VLAN-bound matcher → planned interfaces=all consolidation.
        bool everyLiveEqualsPlannedMember = liveNets.All(live =>
            plannedNets.Any(plannedNet => plannedNet.Equals(live)));
        if (liveIsSpecific && everyLiveEqualsPlannedMember)
        {
            relationship =
                "interface consolidation: specific-interface matcher replaced by planned interfaces=all "
                + "with identical network membership";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Requires the live template's OSPF attributes to be represented by the planned consolidated
    /// passive <c>interfaces=all</c> site matcher.
    /// </summary>
    /// <remarks>
    /// Known ROS conversion: auto-converted per-VLAN passive site matchers often retain
    /// <c>type=ptp</c> from the source interface while the fleet consolidated matcher uses the
    /// default/broadcast type. For <b>passive</b> site matchers this does not change adjacency
    /// behaviour (no neighbours form), so ptp→default is an explicit equivalent conversion.
    /// Non-default cost, auth, timers, BFD, priority, instance-id, or other role-bearing fields
    /// are never treated as equivalent.
    /// </remarks>
    public static bool TryExplainSemanticCompatibility(
        OspfTemplateInventoryRow row,
        OspfPlanSiteNetworkTemplate planned,
        out string detail)
    {
        detail = string.Empty;

        if (!planned.Passive)
        {
            return false;
        }

        // Enabled leftovers must remain passive; disabled leftovers may still be removed when
        // remaining attributes match the planned site-role defaults / known conversions.
        if (!row.Disabled && !row.Passive)
        {
            return false;
        }

        OspfSupersededTemplateIdentity live = OspfTemplateSemanticIdentity.FromInventoryRow(row);
        if (OspfTemplateSemanticIdentity.HasInvalidSemanticTokens(live) || live.AuthKeyAmbiguous)
        {
            return false;
        }

        bool typeIsDefault = string.Equals(
            live.Type,
            OspfTemplateSemanticIdentity.DefaultTypeToken,
            StringComparison.Ordinal);
        bool typeIsKnownPtpConversion = string.Equals(
            live.Type,
            OspfTemplateSemanticIdentity.PtpTypeToken,
            StringComparison.Ordinal);
        if (!typeIsDefault && !typeIsKnownPtpConversion)
        {
            return false;
        }

        if (!string.Equals(live.Cost, OspfTemplateSemanticIdentity.DefaultCostToken, StringComparison.Ordinal)
            || !string.Equals(live.Auth, OspfTemplateSemanticIdentity.DefaultAuthToken, StringComparison.Ordinal)
            || live.AuthId is not null
            || live.AuthKeyPresent
            || !string.Equals(live.Priority, OspfTemplateSemanticIdentity.DefaultPriorityToken, StringComparison.Ordinal)
            || !string.Equals(live.UseBfd, OspfTemplateSemanticIdentity.DefaultUseBfdToken, StringComparison.Ordinal)
            || !string.Equals(live.HelloInterval, OspfTemplateSemanticIdentity.DefaultHelloToken, StringComparison.Ordinal)
            || !string.Equals(live.DeadInterval, OspfTemplateSemanticIdentity.DefaultDeadToken, StringComparison.Ordinal)
            || !string.Equals(
                live.RetransmitInterval,
                OspfTemplateSemanticIdentity.DefaultRetransmitToken,
                StringComparison.Ordinal)
            || !string.Equals(
                live.TransmitDelay,
                OspfTemplateSemanticIdentity.DefaultTransmitDelayToken,
                StringComparison.Ordinal)
            || !string.Equals(
                live.InstanceId,
                OspfTemplateSemanticIdentity.DefaultInstanceIdToken,
                StringComparison.Ordinal)
            || live.PrefixList is not null
            || live.VlinkNeighborId is not null
            || live.VlinkTransitArea is not null)
        {
            return false;
        }

        // Comments are not routing attributes, but non-matching annotations are not silently ignored:
        // desired-state replacement requires the planned comment (usually absent); exact provenance
        // fingerprint matching remains available for annotated identities.
        string? plannedComment = OspfTemplateSemanticIdentity.NormalizeComment(planned.Comment);
        if (!string.Equals(
                live.Comment ?? string.Empty,
                plannedComment ?? string.Empty,
                StringComparison.Ordinal))
        {
            return false;
        }

        detail = typeIsKnownPtpConversion
            ? "semantic replacement: passive site defaults with known type ptp→default ROS conversion"
            : "semantic replacement: passive site defaults (type/cost/auth/timers/bfd)";
        return true;
    }

    /// <summary>
    /// Determines whether every live network equals or lies inside a planned network.
    /// </summary>
    /// <param name="liveNets">Networks currently on the router.</param>
    /// <param name="plannedNets">Networks in the plan.</param>
    /// <returns><see langword="true"/> when there is at least one live network and all are covered.</returns>
    public static bool AllNetworksCoveredByPlanned(
        IReadOnlyList<OspfIpPrefix> liveNets,
        IReadOnlyList<OspfIpPrefix> plannedNets)
    {
        foreach (OspfIpPrefix live in liveNets)
        {
            bool covered = plannedNets.Any(planned =>
                planned.Equals(live) || planned.Contains(live));
            if (!covered)
            {
                return false;
            }
        }

        return liveNets.Count > 0;
    }

    /// <summary>
    /// True when every live network belongs to the intended management summarisation boundary
    /// represented by the planned consolidated site matcher (not incidental broader containment).
    /// </summary>
    public static bool TryExplainManagementMembership(
        IReadOnlyList<OspfIpPrefix> liveNets,
        IReadOnlyList<OspfIpPrefix> plannedNets,
        OspfIpPrefix? derivedManagementCidr,
        bool liveIsSpecific,
        out string relationship)
    {
        relationship = string.Empty;
        if (liveNets.Count == 0)
        {
            return false;
        }

        OspfIpPrefix? mgmtBoundary = derivedManagementCidr;
        if (mgmtBoundary is null)
        {
            // Fall back to an exact planned /24 that covers every live net (fleet management shape).
            foreach (OspfIpPrefix planned in plannedNets.Where(static p => p.PrefixLength == 24))
            {
                if (liveNets.All(n => planned.Equals(n) || planned.Contains(n)))
                {
                    mgmtBoundary = planned;
                    break;
                }
            }
        }

        if (mgmtBoundary is not OspfIpPrefix mgmt)
        {
            return false;
        }

        if (!liveNets.All(n => mgmt.Equals(n) || mgmt.Contains(n)))
        {
            return false;
        }

        // Require the planned site to actually represent that management boundary.
        if (!plannedNets.Any(p => p.Equals(mgmt) || p.Contains(mgmt)))
        {
            return false;
        }

        relationship =
            $"management summarisation: every live network is under management boundary "
            + $"'{mgmt.ToCidrString()}' with {(liveIsSpecific ? "specific→all" : "interfaces=all")} "
            + "interface replacement";
        return true;
    }

    /// <summary>
    /// Converts a live interface-template row to an inventory row, resolving its area instance and authentication key.
    /// </summary>
    /// <param name="template">Live interface-template row.</param>
    /// <param name="areaInstances">Instance names keyed by area name.</param>
    /// <returns>The inventory row.</returns>
    public static OspfTemplateInventoryRow ToInventoryRow(
        OspfInterfaceTemplateRow template,
        IReadOnlyDictionary<string, string> areaInstances)
    {
        OspfTemplateSemanticIdentity.AuthKeyResolution authKey =
            OspfTemplateSemanticIdentity.ResolveAuthKey(template.AuthKey, template.AuthenticationKey);
        string? areaInstance = null;
        if (!string.IsNullOrWhiteSpace(template.Area)
            && areaInstances.TryGetValue(template.Area, out string? resolved))
        {
            areaInstance = resolved;
        }

        return new OspfTemplateInventoryRow
        {
            Id = template.Id,
            Area = template.Area,
            Interfaces = template.Interfaces,
            Networks = template.Networks,
            Type = template.Type,
            Cost = template.Cost,
            Passive = template.Passive,
            Disabled = template.Disabled,
            Auth = template.Auth,
            AuthId = template.AuthId,
            AuthKeyPresent = authKey.Present,
            AuthKeyFingerprint = authKey.Fingerprint,
            AuthKeyAmbiguous = authKey.Ambiguous,
            Priority = template.Priority,
            UseBfd = template.UseBfd,
            HelloInterval = template.HelloInterval,
            DeadInterval = template.DeadInterval,
            RetransmitInterval = template.RetransmitInterval,
            TransmitDelay = template.TransmitDelay,
            InstanceId = template.InstanceId,
            AreaInstanceName = areaInstance,
            PrefixList = template.PrefixList,
            VlinkNeighborId = template.VlinkNeighborId,
            VlinkTransitArea = template.VlinkTransitArea,
            Comment = OspfTemplateSemanticIdentity.NormalizeComment(template.Comment),
        };
    }

    /// <summary>
    /// Maps area names to their instance names, ignoring areas missing either.
    /// </summary>
    /// <param name="areas">Live areas.</param>
    /// <returns>Instance names keyed case-insensitively by area name.</returns>
    public static Dictionary<string, string> BuildAreaInstanceMap(IReadOnlyList<OspfAreaRow> areas)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (OspfAreaRow area in areas)
        {
            if (string.IsNullOrWhiteSpace(area.Name) || string.IsNullOrWhiteSpace(area.Instance))
            {
                continue;
            }

            map[area.Name] = area.Instance;
        }

        return map;
    }

    /// <summary>
    /// Copies a plan with replacement superseded templates and notes.
    /// </summary>
    /// <param name="plan">Plan to copy.</param>
    /// <param name="superseded">Superseded template identities for the copy.</param>
    /// <param name="notes">Normalisation notes for the copy.</param>
    /// <returns>The copied plan.</returns>
    private static OspfMigrationPlan CloneWithSuperseded(
        OspfMigrationPlan plan,
        IReadOnlyList<OspfSupersededTemplateIdentity> superseded,
        IReadOnlyList<string> notes) =>
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
            TargetInstanceName = plan.TargetInstanceName,
            TargetBackboneAreaName = plan.TargetBackboneAreaName,
            TargetSiteAreaName = plan.TargetSiteAreaName,
            LoopbackInterfaceName = plan.LoopbackInterfaceName,
            LoopbackCidr = plan.LoopbackCidr,
            ObservedInScopeLoopbackAddresses = plan.ObservedInScopeLoopbackAddresses,
            IgnoredOutsideRangeLoopbackAddresses = plan.IgnoredOutsideRangeLoopbackAddresses,
            DerivedManagementCidr = plan.DerivedManagementCidr,
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            SupersededTemplates = superseded,
            RemoveAllRoutingFilters = plan.RemoveAllRoutingFilters,
            ClearAllRedistribution = plan.ClearAllRedistribution,
            RemoveAllOspfV3 = plan.RemoveAllOspfV3,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = notes,
            ValidationErrors = plan.ValidationErrors,
        };
}
