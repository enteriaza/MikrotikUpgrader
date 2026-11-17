// -----------------------------------------------------------------------------
// File: OspfMigrationPlanner.cs
//
// Description:
// Ensures the derived management /24 is available as a consolidation input
// when needed. /// Does not inject a duplicate /24 when a broader site matcher
// already covers it and there /// are no more-specific management subdivisions
// to fold — the MGMT area-range remains the /// summarisation artifact in that
// hierarchy.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MikrotikWorker.Serialization;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Builds an immutable fleet-standard <see cref="OspfMigrationPlan"/> from a capture snapshot.
/// Configuration-driven; no router-identity or sample-prefix hardcoding.
/// </summary>
internal static class OspfMigrationPlanner
{
    /// <summary>
    /// OSPF cost applied to every active backbone PTP backhaul under the fleet standard.
    /// </summary>
    public const int FleetBackhaulCost = 10;
    /// <summary>
    /// Area ID of the OSPF backbone area.
    /// </summary>
    public const string BackboneAreaId = "0.0.0.0";

    /// <summary>
    /// Classifies a capture snapshot into a normalised migration plan and validates it.
    /// Problems are collected as validation errors on the plan rather than thrown, so callers must check validity before use.
    /// </summary>
    /// <param name="snapshot">Captured OSPF, address, and identity state for one router.</param>
    /// <returns>A result holding the content-hashed plan, including any validation errors and normalisation notes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="snapshot"/> is null.</exception>
    public static OspfPlanValidationResult Build(OspfCaptureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<string> errors = [];
        List<string> notes = [];

        // Eligible inventory boundary: disabled/unknown-admin-state objects remain in the raw
        // snapshot for diagnostics but never enter OSPF migration decisions.
        OspfCaptureSnapshot eligible = ToEligibleDecisionSnapshot(snapshot, notes);

        if (eligible.Instances.Count == 0)
        {
            errors.Add("OSPF instance is missing; empty OSPF configuration is a serious configuration error.");
        }

        OspfAreaRow? backbone = eligible.Areas.FirstOrDefault(static a =>
            string.Equals(NormalizeAreaId(a.AreaId), BackboneAreaId, StringComparison.Ordinal));
        if (backbone is null)
        {
            errors.Add("Backbone area (area-id 0.0.0.0) is missing.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.Identity))
        {
            errors.Add("Router /system identity name is missing or empty.");
        }

        OspfInstanceRow? instance = SelectInstance(eligible, backbone, errors);
        string routerId = string.Empty;
        // Corroborate multi-address Loopback selection with the OSPF instance router-id when present.
        OspfLoopbackRouterIdResult loopbackRid = OspfLoopbackRouterId.Derive(
            eligible.IpAddresses,
            instance?.RouterId);
        if (!loopbackRid.Succeeded || string.IsNullOrWhiteSpace(loopbackRid.RouterId))
        {
            errors.AddRange(loopbackRid.Errors);
            if (loopbackRid.Errors.Count == 0)
            {
                errors.Add("Loopback /32 Router ID derivation failed.");
            }
        }
        else
        {
            routerId = loopbackRid.RouterId!;
            string? instanceRid = instance?.RouterId?.Trim();
            string? resolvedLegacyRid = ResolveLegacyRouterIdValue(instanceRid, snapshot.RoutingIds);
            if (string.IsNullOrWhiteSpace(instanceRid))
            {
                notes.Add(
                    "OSPF instance router-id is missing or empty; migration will apply Loopback-derived "
                    + $"literal router-id '{routerId}'.");
            }
            else if (!OspfLoopbackRouterId.EqualsRouterId(resolvedLegacyRid ?? instanceRid, routerId))
            {
                notes.Add(
                    $"OSPF instance router-id '{instanceRid}' differs from Loopback /32 address "
                    + $"'{loopbackRid.LoopbackCidr}' on '{loopbackRid.LoopbackInterfaceName}'; "
                    + $"migration will normalise to literal '{routerId}'.");
            }

            if (loopbackRid.IgnoredOutsideRangeAddresses.Count > 0)
            {
                notes.Add(
                    "Ignored outside-range Loopback address(es) for migration identity: "
                    + string.Join(", ", loopbackRid.IgnoredOutsideRangeAddresses)
                    + ".");
            }
        }

        string instanceName = OspfFleetNaming.SourceInstanceName(snapshot.SourceKind);
        if (!string.IsNullOrWhiteSpace(instance?.Name)
            && !string.Equals(instance.Name, instanceName, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(
                $"Source OSPF instance name '{instance.Name}' differs from fleet target "
                + $"'{OspfFleetNaming.Ros7InstanceName}'; migration will rename the OSPFv2 instance to "
                + $"'{OspfFleetNaming.Ros7InstanceName}'.");
        }

        if (eligible.SourceKind == OspfCaptureSourceKind.RouterOsV6 && eligible.Networks.Count == 0
            && eligible.InterfaceTemplates.Count == 0)
        {
            errors.Add("No OSPF network statements (or v7 templates) were found; configuration is incomplete.");
        }

        if (eligible.SourceKind == OspfCaptureSourceKind.RouterOsV7 && eligible.InterfaceTemplates.Count == 0
            && eligible.Networks.Count == 0)
        {
            errors.Add("No OSPF interface-templates were found on RouterOS 7; cannot establish topology.");
        }

        List<OspfPlanArea> areas = [];
        foreach (OspfAreaRow area in eligible.Areas.Where(a => BelongsToInstance(a, instance)))
        {
            string areaId = NormalizeAreaId(area.AreaId);
            bool isBb = areaId == BackboneAreaId;
            string? sourceDefaultCost = NullIfWhite(area.DefaultCost);
            string? defaultCost = null;
            if (sourceDefaultCost is not null)
            {
                if (!int.TryParse(sourceDefaultCost, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    || parsed < 0)
                {
                    errors.Add(
                        $"Area '{area.Name}' default-cost '{sourceDefaultCost}' cannot be parsed safely.");
                }
                else
                {
                    defaultCost = parsed.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (!isBb)
            {
                // Fleet target: site areas are stub with default-cost=1.
                if (defaultCost is null)
                {
                    notes.Add(
                        $"Area '{area.Name}' missing default-cost; applying fleet target "
                        + $"{OspfFleetNaming.SiteDefaultCost}.");
                    defaultCost = OspfFleetNaming.SiteDefaultCost;
                }
                else if (!string.Equals(defaultCost, OspfFleetNaming.SiteDefaultCost, StringComparison.Ordinal))
                {
                    notes.Add(
                        $"Area '{area.Name}' default-cost '{defaultCost}' normalised to "
                        + $"{OspfFleetNaming.SiteDefaultCost} (fleet standard).");
                    defaultCost = OspfFleetNaming.SiteDefaultCost;
                }

                if (area.Type is not null
                    && !area.Type.Equals("stub", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(area.Type)
                    && !area.Type.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(
                        $"Area '{area.Name}' source type '{area.Type}' normalised to stub (fleet standard).");
                }
                else if (string.IsNullOrWhiteSpace(area.Type)
                         || area.Type.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add(
                        $"Area '{area.Name}' source type '{area.Type ?? "<unset>"}' normalised to stub (fleet standard).");
                }
            }

            // Fleet target: stub site areas permit inter-area summaries. Clear no-summaries when present.
            if (area.NoSummaries == true)
            {
                notes.Add(
                    $"Area '{area.Name}' had no-summaries set; clearing to permit inter-area summaries.");
            }

            areas.Add(new OspfPlanArea
            {
                Name = area.Name ?? areaId,
                AreaId = areaId,
                IsBackbone = isBb,
                TargetType = isBb ? "default" : "stub",
                NoSummaries = false,
                DefaultCost = isBb ? null : defaultCost,
                SourceDefaultCost = sourceDefaultCost,
            });
        }

        if (areas.Count(static a => a.IsBackbone) != 1 && errors.All(e => !e.Contains("Backbone", StringComparison.Ordinal)))
        {
            // already reported missing; duplicate check:
            if (areas.Count(static a => a.IsBackbone) > 1)
            {
                errors.Add("Multiple backbone areas (0.0.0.0) found; ambiguous.");
            }
        }

        string backboneName = areas.FirstOrDefault(static a => a.IsBackbone)?.Name
            ?? backbone?.Name
            ?? "backbone";

        Dictionary<string, string> areaNameById = areas
            .GroupBy(static a => a.AreaId, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First().Name, StringComparer.Ordinal);

        List<OspfPlanAreaRange> ranges = [];
        foreach (OspfAreaRangeRow range in eligible.AreaRanges)
        {
            string? areaName = ResolveAreaName(range.Area, areas, areaNameById);
            if (areaName is null)
            {
                errors.Add($"Area range references unknown area '{range.Area}'.");
                continue;
            }

            if (!OspfIpPrefix.TryParse(range.Range, out OspfIpPrefix prefix))
            {
                errors.Add($"Area range prefix '{range.Range}' is invalid.");
                continue;
            }

            ranges.Add(new OspfPlanAreaRange
            {
                AreaName = areaName,
                RangeCidr = prefix.ToCidrString(),
                Comment = range.Comment,
                Advertise = range.Advertise,
            });
        }

        List<OspfPlanBackhaulTemplate> backhauls = [];
        List<OspfPlanLoopbackTemplate> loopbacks = [];
        List<OspfPlanSiteNetworkTemplate> sites = [];

        if (eligible.SourceKind == OspfCaptureSourceKind.RouterOsV6)
        {
            ClassifyFromV6(
                eligible,
                areas,
                backboneName,
                areaNameById,
                backhauls,
                loopbacks,
                sites,
                errors,
                notes);
        }
        else
        {
            ClassifyFromV7(
                eligible,
                areas,
                backboneName,
                areaNameById,
                backhauls,
                loopbacks,
                sites,
                errors,
                notes);
        }

        if (backhauls.Count == 0)
        {
            TryClassifyBackhaulFromNeighbourEvidence(
                eligible,
                areas,
                backboneName,
                backhauls,
                errors,
                notes);
        }

        if (backhauls.Count == 0)
        {
            errors.Add(
                "No valid active backbone backhaul could be classified from PTP templates/interfaces "
                + "or Full neighbour evidence.");
        }

        if (loopbacks.Count == 0)
        {
            errors.Add("No backbone loopback /32 advertisement could be classified.");
        }

        // Active template must not use a network that contains another local interface's address
        // on a different interface (would risk unintended activation under OR matching).
        errors.AddRange(ValidateBackhaulTemplateIsolation(backhauls, eligible.IpAddresses));

        // Derive management /24 set and attach MGMT area-ranges BEFORE consolidation so FoldPrefix
        // can fold subdivided management matchers into the planned summaries during provenance capture.
        string? derivedMgmt = null;
        IReadOnlyList<string> derivedMgmtSet = [];
        if (loopbackRid.Succeeded && backhauls.Count > 0)
        {
            IReadOnlyList<string> mgmtHints = OspfSiteManagementSummary.CollectCorroboratingManagementCidrs(
                ranges,
                eligible.Networks);
            OspfSiteManagementSummaryResult mgmt = OspfSiteManagementSummary.Derive(
                eligible.IpAddresses,
                loopbackRid,
                backhauls,
                mgmtHints);
            if (!mgmt.Succeeded
                || string.IsNullOrWhiteSpace(mgmt.ManagementCidr)
                || mgmt.ManagementCidrs.Count == 0)
            {
                errors.AddRange(mgmt.Errors);
            }
            else
            {
                derivedMgmt = mgmt.ManagementCidr;
                derivedMgmtSet = mgmt.ManagementCidrs;
                notes.Add(
                    $"Derived site management /24 set [{string.Join(", ", derivedMgmtSet)}] from "
                    + string.Join("; ", mgmt.ContributingAddresses));
                notes.AddRange(
                    mgmt.SelectionDiagnostics.Where(static d =>
                        d.StartsWith("accepted additional management expansion", StringComparison.Ordinal)
                        || d.StartsWith("selected existing management aggregate", StringComparison.Ordinal)));
                ranges = EnsureManagementAreaRanges(
                    ranges,
                    areas,
                    derivedMgmtSet,
                    notes,
                    errors);
                foreach (string cidr in derivedMgmtSet)
                {
                    sites = EnsureDerivedManagementSiteMatcher(sites, areas, cidr, notes);
                }
            }
        }

        // Collapse per-VLAN / per-network site matchers into one interfaces=all template per site area.
        // Record superseded source identities as positive KnownLegacy provenance for cleanup.
        OspfSiteConsolidationResult consolidation =
            OspfSiteMatcherConsolidation.ConsolidateWithProvenance(sites, ranges, notes);
        sites = consolidation.Consolidated.ToList();
        List<OspfSupersededTemplateIdentity> superseded = consolidation.Superseded.ToList();

        OspfPlanArea? siteArea = areas.FirstOrDefault(static a => !a.IsBackbone);
        if (siteArea is not null
            && !string.IsNullOrWhiteSpace(snapshot.Identity)
            && !string.Equals(siteArea.Name, snapshot.Identity.Trim(), StringComparison.Ordinal))
        {
            // Genuine fixture exception e.g. Kopefontein: identity uses '.' while area uses '_'.
            notes.Add(
                $"Captured site area name '{siteArea.Name}' differs from /system identity "
                + $"'{snapshot.Identity.Trim()}'; ROS7 target site area remains "
                + $"'{OspfFleetNaming.Ros7SiteAreaName(snapshot.Identity)}'.");
        }

        if (!string.IsNullOrWhiteSpace(routerId)
            && siteArea is not null
            && !string.Equals(siteArea.AreaId, routerId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Site area '{siteArea.Name}' area-id '{siteArea.AreaId}' does not equal Loopback Router ID '{routerId}'.");
        }

        List<OspfV3ObjectRef> v3 = snapshot.OspfV3Objects.ToList();
        if (v3.Count > 0)
        {
            notes.Add($"OSPFv3 objects queued for removal: {v3.Count}.");
        }

        string targetSite = string.IsNullOrWhiteSpace(snapshot.Identity)
            ? string.Empty
            : OspfFleetNaming.Ros7SiteAreaName(snapshot.Identity);

        var plan = new OspfMigrationPlan
        {
            ImportId = snapshot.ImportId,
            JobId = snapshot.JobId,
            RouterIp = snapshot.RouterIp.ToString(),
            Identity = snapshot.Identity,
            SourceRouterOsVersion = snapshot.RouterOsVersion,
            Architecture = snapshot.Architecture,
            SourceKind = snapshot.SourceKind,
            InstanceName = instanceName,
            RouterId = routerId,
            TargetInstanceName = OspfFleetNaming.Ros7InstanceName,
            TargetBackboneAreaName = OspfFleetNaming.Ros7BackboneAreaName,
            TargetSiteAreaName = targetSite,
            LoopbackInterfaceName = loopbackRid.LoopbackInterfaceName,
            LoopbackCidr = loopbackRid.LoopbackCidr,
            ObservedInScopeLoopbackAddresses = loopbackRid.ObservedInScopeAddresses,
            IgnoredOutsideRangeLoopbackAddresses = loopbackRid.IgnoredOutsideRangeAddresses,
            DerivedManagementCidr = derivedMgmt,
            DerivedManagementCidrs = derivedMgmtSet,
            Areas = areas,
            AreaRanges = ranges,
            ActiveBackhauls = backhauls,
            PassiveLoopbacks = loopbacks,
            PassiveSiteNetworks = sites,
            SupersededTemplates = superseded,
            OspfV3ToRemove = v3,
            NormalisationNotes = notes,
            ValidationErrors = errors,
        };

        // First-class backbone / role invariants before any mutation path accepts the plan.
        errors.AddRange(OspfBackboneSafety.ValidatePlanInvariants(plan, eligible.IpAddresses));
        plan = ClonePlan(plan, errors);
        plan.ContentHash = ComputeContentHash(plan);

        return new OspfPlanValidationResult { Plan = plan };
    }

    /// <summary>
    /// Classifies RouterOS 6 <c>network</c> and interface rows into backhaul, loopback, and site-network templates.
    /// </summary>
    /// <param name="snapshot">Captured router state.</param>
    /// <param name="areas">Planned areas used to resolve area references.</param>
    /// <param name="backboneName">Name of the backbone area.</param>
    /// <param name="areaNameById">Area names keyed by area ID.</param>
    /// <param name="backhauls">Receives active backbone PTP backhaul templates.</param>
    /// <param name="loopbacks">Receives passive backbone loopback templates.</param>
    /// <param name="sites">Receives passive site network matchers.</param>
    /// <param name="errors">Receives validation errors.</param>
    /// <param name="notes">Receives normalisation notes.</param>
    private static void ClassifyFromV6(
        OspfCaptureSnapshot snapshot,
        List<OspfPlanArea> areas,
        string backboneName,
        Dictionary<string, string> areaNameById,
        List<OspfPlanBackhaulTemplate> backhauls,
        List<OspfPlanLoopbackTemplate> loopbacks,
        List<OspfPlanSiteNetworkTemplate> sites,
        List<string> errors,
        List<string> notes)
    {
        bool defaultPassive = snapshot.Interfaces.Any(static i =>
            string.IsNullOrWhiteSpace(i.Interface) && i.Passive);
        if (defaultPassive)
        {
            notes.Add("Observed unnamed passive OSPF interface entry; enforcing passive non-backhaul policy.");
        }

        foreach (OspfNetworkRow network in snapshot.Networks)
        {
            string? areaName = ResolveAreaName(network.Area, areas, areaNameById);
            if (areaName is null)
            {
                errors.Add($"OSPF network '{network.Network}' references unknown area '{network.Area}'.");
                continue;
            }

            if (!OspfIpPrefix.TryParse(network.Network, out OspfIpPrefix prefix))
            {
                errors.Add($"OSPF network prefix '{network.Network}' is invalid.");
                continue;
            }

            bool isBackbone = string.Equals(areaName, backboneName, StringComparison.Ordinal)
                || areas.Any(a => a.Name == areaName && a.IsBackbone);

            if (isBackbone && prefix.PrefixLength == 32)
            {
                OspfIpAddressRow? addr = snapshot.IpAddresses.FirstOrDefault(a =>
                    OspfIpPrefix.TryParseHost(a.Address, out uint host, out _)
                    && host == prefix.NetworkAddress);
                loopbacks.Add(new OspfPlanLoopbackTemplate
                {
                    AreaName = backboneName,
                    NetworkCidr = prefix.ToCidrString(),
                    Interface = addr?.Interface,
                    Passive = true,
                });
                continue;
            }

            if (isBackbone)
            {
                // Backbone non-/32: must map to unique active PTP interface
                List<OspfInterfaceRow> candidates = snapshot.Interfaces
                    .Where(i => !string.IsNullOrWhiteSpace(i.Interface)
                        && !i.Passive
                        && IsPointToPoint(i.NetworkType))
                    .ToList();

                List<(OspfInterfaceRow Iface, OspfIpAddressRow Addr, OspfIpPrefix Subnet)> matches = [];
                foreach (OspfInterfaceRow iface in candidates)
                {
                    foreach (OspfIpAddressRow addr in snapshot.IpAddresses.Where(a =>
                                 string.Equals(a.Interface, iface.Interface, StringComparison.Ordinal)))
                    {
                        if (!TryInterfaceSubnet(addr, out OspfIpPrefix subnet))
                        {
                            continue;
                        }

                        if (prefix.Contains(subnet) || subnet.Contains(prefix) || prefix.Equals(subnet))
                        {
                            // Prefer exact equality of network prefixes
                            if (prefix.Equals(subnet) || (prefix.ContainsHost(subnet.NetworkAddress)
                                && subnet.PrefixLength == prefix.PrefixLength))
                            {
                                matches.Add((iface, addr, subnet));
                            }
                        }
                    }
                }

                // Exact subnet match preferred
                matches = matches
                    .Where(m => m.Subnet.Equals(prefix) || m.Subnet.ToCidrString() == prefix.ToCidrString())
                    .ToList();
                if (matches.Count == 0)
                {
                    // Fall back: network statement covers interface subnet
                    matches = [];
                    foreach (OspfInterfaceRow iface in candidates)
                    {
                        foreach (OspfIpAddressRow addr in snapshot.IpAddresses.Where(a =>
                                     string.Equals(a.Interface, iface.Interface, StringComparison.Ordinal)))
                        {
                            if (!TryInterfaceSubnet(addr, out OspfIpPrefix subnet))
                            {
                                continue;
                            }

                            if (prefix.Contains(subnet) || prefix.Equals(subnet))
                            {
                                matches.Add((iface, addr, subnet));
                            }
                        }
                    }
                }

                if (matches.Count != 1)
                {
                    errors.Add(
                        $"Backbone network '{prefix.ToCidrString()}' does not map to exactly one active PTP interface (matches={matches.Count}).");
                    continue;
                }

                (OspfInterfaceRow matchedIface, _, OspfIpPrefix matchedSubnet) = matches[0];
                backhauls.Add(new OspfPlanBackhaulTemplate
                {
                    AreaName = backboneName,
                    Interface = matchedIface.Interface!,
                    NetworkCidr = matchedSubnet.ToCidrString(),
                    Cost = FleetBackhaulCost,
                    Type = "ptp",
                    Passive = false,
                    SourceCost = matchedIface.Cost,
                });
                if (!string.IsNullOrWhiteSpace(matchedIface.Cost)
                    && matchedIface.Cost != FleetBackhaulCost.ToString(CultureInfo.InvariantCulture))
                {
                    notes.Add(
                        $"Backhaul '{matchedIface.Interface}' source cost '{matchedIface.Cost}' normalised to {FleetBackhaulCost}.");
                }

                continue;
            }

            // Site-area: collect passive matchers; consolidation emits interfaces=all later.
            sites.Add(new OspfPlanSiteNetworkTemplate
            {
                AreaName = areaName,
                Interfaces = OspfSiteMatcherConsolidation.AllInterfaces,
                NetworkCidr = prefix.ToCidrString(),
                Comment = network.Comment,
                Passive = true,
            });
        }

        // Named active PTP without backbone network → fail
        foreach (OspfInterfaceRow iface in snapshot.Interfaces.Where(i =>
                     !string.IsNullOrWhiteSpace(i.Interface) && !i.Passive && IsPointToPoint(i.NetworkType)))
        {
            bool covered = backhauls.Any(b =>
                string.Equals(b.Interface, iface.Interface, StringComparison.Ordinal));
            if (!covered)
            {
                errors.Add(
                    $"Active PTP OSPF interface '{iface.Interface}' has no matching backbone network classification.");
            }
        }
    }

    /// <summary>
    /// Classifies RouterOS 7 interface templates into backhaul, loopback, and site-network templates.
    /// </summary>
    /// <param name="snapshot">Captured router state.</param>
    /// <param name="areas">Planned areas used to resolve area references.</param>
    /// <param name="backboneName">Name of the backbone area.</param>
    /// <param name="areaNameById">Area names keyed by area ID.</param>
    /// <param name="backhauls">Receives active backbone PTP backhaul templates.</param>
    /// <param name="loopbacks">Receives passive backbone loopback templates.</param>
    /// <param name="sites">Receives passive site network matchers.</param>
    /// <param name="errors">Receives validation errors.</param>
    /// <param name="notes">Receives normalisation notes.</param>
    private static void ClassifyFromV7(
        OspfCaptureSnapshot snapshot,
        List<OspfPlanArea> areas,
        string backboneName,
        Dictionary<string, string> areaNameById,
        List<OspfPlanBackhaulTemplate> backhauls,
        List<OspfPlanLoopbackTemplate> loopbacks,
        List<OspfPlanSiteNetworkTemplate> sites,
        List<string> errors,
        List<string> notes)
    {
        foreach (OspfInterfaceTemplateRow template in snapshot.InterfaceTemplates.Where(OspfInventoryEligibility.IsEligible))
        {
            string? areaName = ResolveAreaName(template.Area, areas, areaNameById);
            if (areaName is null)
            {
                errors.Add($"Interface-template references unknown area '{template.Area}'.");
                continue;
            }

            bool isBackbone = areas.Any(a => a.Name == areaName && a.IsBackbone);
            bool passive = template.Passive;
            bool ptp = IsPointToPoint(template.Type);

            // Backbone /32 (or host form) → passive loopback. Must run before active PTP backhaul
            // classification so auto-converted active/ptp loopbacks are not mistaken for backhauls.
            if (isBackbone
                && OspfIpPrefix.TryParse(template.Networks, out OspfIpPrefix loop)
                && loop.PrefixLength == 32)
            {
                if (!passive)
                {
                    notes.Add(
                        $"Normalising backbone loopback '{loop.ToCidrString()}' from active to passive (fleet standard).");
                }

                if (ptp)
                {
                    notes.Add(
                        $"Clearing forced PTP on backbone loopback '{loop.ToCidrString()}' (do not force type=ptp).");
                }

                loopbacks.Add(new OspfPlanLoopbackTemplate
                {
                    AreaName = backboneName,
                    NetworkCidr = loop.ToCidrString(),
                    Interface = NullIfWhite(template.Interfaces),
                    Passive = true,
                });
                continue;
            }

            // Active backbone template with interfaces + networks. Prefer PTP; also accept non-PTP
            // when a Full neighbour on the same interface corroborates the adjacency (bridged /
            // VLAN / wireless logical backhaul).
            bool neighbourFullOnIface = !string.IsNullOrWhiteSpace(template.Interfaces)
                && snapshot.Neighbours.Any(n =>
                    string.Equals(n.State, "Full", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(n.Interface, template.Interfaces, StringComparison.OrdinalIgnoreCase));
            if (isBackbone && !passive && (ptp || neighbourFullOnIface)
                && !string.IsNullOrWhiteSpace(template.Interfaces)
                && OspfIpPrefix.TryParse(template.Networks, out OspfIpPrefix net))
            {
                OspfIpAddressRow? addr = snapshot.IpAddresses.FirstOrDefault(a =>
                    OspfInventoryEligibility.IsEligible(a)
                    && string.Equals(a.Interface, template.Interfaces, StringComparison.Ordinal)
                    && TryInterfaceSubnet(a, out OspfIpPrefix sub)
                    && (sub.Equals(net) || net.Contains(sub)));
                if (addr is null || !TryInterfaceSubnet(addr, out OspfIpPrefix exactSubnet))
                {
                    errors.Add(
                        $"Active backbone template interfaces='{template.Interfaces}' networks='{template.Networks}' could not be correlated to a local address/subnet.");
                    continue;
                }

                // Constrain to exact interface subnet, not a broader template network
                if (!exactSubnet.Equals(net) && net.Contains(exactSubnet) && net.PrefixLength < exactSubnet.PrefixLength)
                {
                    notes.Add(
                        $"Narrowing backhaul networks from '{net.ToCidrString()}' to interface subnet '{exactSubnet.ToCidrString()}'.");
                }

                if (!ptp && neighbourFullOnIface)
                {
                    notes.Add(
                        $"Classifying non-PTP active backbone template on '{template.Interfaces}' as backhaul "
                        + "using Full neighbour corroboration; planned type normalised to ptp.");
                }

                backhauls.Add(new OspfPlanBackhaulTemplate
                {
                    AreaName = backboneName,
                    Interface = template.Interfaces!,
                    NetworkCidr = exactSubnet.ToCidrString(),
                    Cost = FleetBackhaulCost,
                    Type = "ptp",
                    Passive = false,
                    SourceCost = template.Cost,
                });
                if (!string.IsNullOrWhiteSpace(template.Cost)
                    && template.Cost != FleetBackhaulCost.ToString(CultureInfo.InvariantCulture))
                {
                    notes.Add(
                        $"Backhaul '{template.Interfaces}' source cost '{template.Cost}' normalised to {FleetBackhaulCost}.");
                }

                continue;
            }

            // Non-backbone matchers → passive site/PPPoE templates. Legacy ROS7 conversion may bind
            // them to specific VLANs; fleet policy consolidates to interfaces=all later.
            // Preserve original interfaces + semantic fields so SupersededTemplates carry complete provenance.
            if (!isBackbone
                && OspfNetworkList.TryParseAll(template.Networks, out List<OspfIpPrefix> siteNets, out _))
            {
                foreach (OspfIpPrefix siteNet in siteNets)
                {
                    if (!passive)
                    {
                        notes.Add(
                            $"Normalising site matcher '{siteNet.ToCidrString()}' from active to passive (fleet standard).");
                    }

                    string? sourceInterfaces = NullIfWhite(template.Interfaces);
                    if (!string.IsNullOrWhiteSpace(sourceInterfaces)
                        && !OspfSiteMatcherConsolidation.IsAllInterfaces(sourceInterfaces))
                    {
                        notes.Add(
                            $"Site matcher '{siteNet.ToCidrString()}' source interfaces='{sourceInterfaces}' will consolidate to interfaces=all; recording provenance for cleanup.");
                    }

                    sites.Add(new OspfPlanSiteNetworkTemplate
                    {
                        AreaName = areaName,
                        Interfaces = sourceInterfaces ?? OspfSiteMatcherConsolidation.AllInterfaces,
                        NetworkCidr = siteNet.ToCidrString(),
                        Comment = template.Comment,
                        Passive = true,
                        CapturedSemantics = OspfTemplateSemanticIdentity.CaptureFromTemplateRow(
                            template,
                            ResolveAreaInstanceName(snapshot, areaName)),
                    });
                }

                continue;
            }

            if (!passive && !string.IsNullOrWhiteSpace(template.Interfaces))
            {
                errors.Add(
                    $"Unrecognised active interface-template area='{template.Area}' interfaces='{template.Interfaces}' networks='{template.Networks}' cannot be classified as a backhaul.");
            }
        }
    }

    /// <summary>
    /// Fail-closed isolation: a planned active backhaul <c>networks=</c> CIDR must not cover any
    /// host address on a different interface. Does not assume AND matching of interfaces+networks.
    /// </summary>
    internal static IReadOnlyList<string> ValidateBackhaulTemplateIsolation(
        IReadOnlyList<OspfPlanBackhaulTemplate> backhauls,
        IReadOnlyList<OspfIpAddressRow> addresses)
    {
        List<string> errors = [];
        foreach (OspfPlanBackhaulTemplate bh in backhauls)
        {
            if (!OspfIpPrefix.TryParse(bh.NetworkCidr, out OspfIpPrefix net))
            {
                errors.Add($"Backhaul network '{bh.NetworkCidr}' is invalid.");
                continue;
            }

            foreach (OspfIpAddressRow addr in addresses.Where(OspfInventoryEligibility.IsEligible))
            {
                if (string.Equals(addr.Interface, bh.Interface, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!OspfIpPrefix.TryParseHost(addr.Address, out uint host, out _))
                {
                    continue;
                }

                if (net.ContainsHost(host))
                {
                    errors.Add(
                        $"Active backhaul template for '{bh.Interface}' networks={bh.NetworkCidr} also covers address on unrelated interface '{addr.Interface}'; refusing unsafe activation risk.");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Builds a decision snapshot containing only demonstrably enabled objects.
    /// OSPFv3 removal refs are retained from the raw snapshot so mandatory removal is unchanged.
    /// </summary>
    private static OspfCaptureSnapshot ToEligibleDecisionSnapshot(
        OspfCaptureSnapshot raw,
        List<string> notes)
    {
        IReadOnlyList<OspfIpAddressRow> addresses = OspfInventoryEligibility.Eligible(raw.IpAddresses);
        IReadOnlyList<OspfInstanceRow> instances = OspfInventoryEligibility.Eligible(raw.Instances);
        IReadOnlyList<OspfAreaRow> areas = OspfInventoryEligibility.Eligible(raw.Areas);
        IReadOnlyList<OspfAreaRangeRow> ranges = OspfInventoryEligibility.Eligible(raw.AreaRanges);
        IReadOnlyList<OspfNetworkRow> networks = OspfInventoryEligibility.Eligible(raw.Networks);
        IReadOnlyList<OspfInterfaceRow> interfaces = OspfInventoryEligibility.Eligible(raw.Interfaces);
        IReadOnlyList<OspfInterfaceTemplateRow> templates =
            OspfInventoryEligibility.Eligible(raw.InterfaceTemplates);
        IReadOnlyList<OspfRoutingFilterRow> filters =
            OspfInventoryEligibility.Eligible(raw.RoutingFilters);

        NoteExcludedDisabled(notes, "IP address", raw.IpAddresses.Count, addresses.Count);
        NoteExcludedDisabled(notes, "OSPF instance", raw.Instances.Count, instances.Count);
        NoteExcludedDisabled(notes, "OSPF area", raw.Areas.Count, areas.Count);
        NoteExcludedDisabled(notes, "OSPF area-range", raw.AreaRanges.Count, ranges.Count);
        NoteExcludedDisabled(notes, "OSPF network", raw.Networks.Count, networks.Count);
        NoteExcludedDisabled(notes, "OSPF interface", raw.Interfaces.Count, interfaces.Count);
        NoteExcludedDisabled(notes, "OSPF interface-template", raw.InterfaceTemplates.Count, templates.Count);
        NoteExcludedDisabled(notes, "routing filter", raw.RoutingFilters.Count, filters.Count);

        return new OspfCaptureSnapshot
        {
            ImportId = raw.ImportId,
            JobId = raw.JobId,
            RouterIp = raw.RouterIp,
            Identity = raw.Identity,
            RouterOsVersion = raw.RouterOsVersion,
            Architecture = raw.Architecture,
            SourceKind = raw.SourceKind,
            Instances = instances,
            Areas = areas,
            AreaRanges = ranges,
            Networks = networks,
            Interfaces = interfaces,
            InterfaceTemplates = templates,
            IpAddresses = addresses,
            RoutingFilters = filters,
            RoutingIds = raw.RoutingIds,
            Neighbours = raw.Neighbours,
            // Keep raw OSPFv3 refs so mandatory removal still covers disabled OSPFv3 leftovers.
            OspfV3Objects = raw.OspfV3Objects,
        };
    }

    /// <summary>
    /// Appends a planner diagnostic when raw inventory contained disabled or unknown-admin-state
    /// objects that were stripped before OSPF decision-making.
    /// </summary>
    /// <param name="notes">Mutable planner diagnostic list.</param>
    /// <param name="kind">Inventory kind label (for example <c>template</c> or <c>address</c>).</param>
    /// <param name="rawCount">Count before eligibility filtering.</param>
    /// <param name="eligibleCount">Count remaining after disabled/unknown exclusion.</param>
    private static void NoteExcludedDisabled(
        List<string> notes,
        string kind,
        int rawCount,
        int eligibleCount)
    {
        int excluded = rawCount - eligibleCount;
        if (excluded <= 0)
        {
            return;
        }

        notes.Add(
            $"Excluded {excluded} disabled/unknown-admin-state {kind} object(s) from OSPF migration decisions.");
    }

    /// <summary>
    /// Selects the OSPFv2 instance that owns the backbone, recording an error when the choice is ambiguous.
    /// </summary>
    /// <param name="snapshot">Captured router state.</param>
    /// <param name="backbone">Backbone area row, used to match its owning instance.</param>
    /// <param name="errors">Receives an error when several instances remain.</param>
    /// <returns>The selected instance, or null when none exist.</returns>
    private static OspfInstanceRow? SelectInstance(
        OspfCaptureSnapshot snapshot,
        OspfAreaRow? backbone,
        List<string> errors)
    {
        // OSPFv2 migration target only — OSPFv3 instances are mandatory removal targets, never the
        // OSPFv2 plan source instance. Caller must pass eligible (enabled) inventory.
        List<OspfInstanceRow> list = snapshot.Instances
            .Where(static i => !string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (list.Count == 0)
        {
            if (snapshot.Instances.Any(static i =>
                    string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(
                    "No OSPFv2 instance is present; sole/remaining OSPF instance(s) are OSPFv3 "
                    + "removal targets and cannot be selected as the OSPFv2 migration target.");
            }

            return null;
        }

        if (list.Count == 1)
        {
            return list[0];
        }

        if (backbone?.Instance is not null)
        {
            OspfInstanceRow? byName = list.FirstOrDefault(i =>
                string.Equals(i.Name, backbone.Instance, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        OspfInstanceRow? def = list.FirstOrDefault(static i => i.IsDefault);
        if (def is not null)
        {
            return def;
        }

        errors.Add("Multiple OSPFv2 instances present; cannot select the owning instance unambiguously.");
        return null;
    }

    /// <summary>
    /// Returns the instance name, or the RouterOS default when it is unnamed.
    /// </summary>
    /// <param name="instance">Selected instance row, if any.</param>
    /// <param name="kind">Source RouterOS major version.</param>
    /// <returns>The instance name, or <c>default-v2</c> / <c>default</c> as fallback.</returns>
    private static string ResolveInstanceName(OspfInstanceRow? instance, OspfCaptureSourceKind kind)
    {
        if (!string.IsNullOrWhiteSpace(instance?.Name))
        {
            return instance.Name!;
        }

        return kind == OspfCaptureSourceKind.RouterOsV7 ? "default-v2" : "default";
    }

    /// <summary>
    /// Resolves a legacy OSPF instance router-id value that may be a literal IPv4 address or a
    /// <c>/routing/id</c> object name. Returns null when the value cannot be resolved to IPv4.
    /// </summary>
    internal static string? ResolveLegacyRouterIdValue(
        string? routerIdValue,
        IReadOnlyList<OspfRoutingIdRow> routingIds)
    {
        if (string.IsNullOrWhiteSpace(routerIdValue))
        {
            return null;
        }

        string trimmed = routerIdValue.Trim();
        if (OspfLoopbackRouterId.IsDottedQuadIPv4(trimmed))
        {
            int slash = trimmed.IndexOf('/');
            return slash < 0 ? trimmed : trimmed[..slash];
        }

        OspfRoutingIdRow? match = routingIds.FirstOrDefault(r =>
            string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is null || string.IsNullOrWhiteSpace(match.Address))
        {
            return null;
        }

        string address = match.Address.Trim();
        if (!OspfLoopbackRouterId.IsDottedQuadIPv4(address))
        {
            return null;
        }

        int addrSlash = address.IndexOf('/');
        return addrSlash < 0 ? address : address[..addrSlash];
    }

    /// <summary>
    /// When PTP classification found no backhaul, attempt a single uniquely evidenced backbone
    /// adjacency from Full neighbours correlated to a local interface address in the backbone area.
    /// </summary>
    private static void TryClassifyBackhaulFromNeighbourEvidence(
        OspfCaptureSnapshot snapshot,
        List<OspfPlanArea> areas,
        string backboneName,
        List<OspfPlanBackhaulTemplate> backhauls,
        List<string> errors,
        List<string> notes)
    {
        List<OspfNeighbourRow> full = snapshot.Neighbours
            .Where(static n =>
                string.Equals(n.State, "Full", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(n.Interface))
            .ToList();
        if (full.Count == 0)
        {
            return;
        }

        List<(string Interface, OspfIpPrefix Subnet, string? SourceCost)> candidates = [];
        foreach (OspfNeighbourRow nbr in full)
        {
            string iface = nbr.Interface!.Trim();
            // Prefer the OSPF interface/template cost when present; otherwise leave null.
            string? sourceCost = snapshot.Interfaces
                .Where(OspfInventoryEligibility.IsEligible)
                .FirstOrDefault(i => string.Equals(i.Interface, iface, StringComparison.Ordinal))
                ?.Cost
                ?? snapshot.InterfaceTemplates
                    .Where(OspfInventoryEligibility.IsEligible)
                    .FirstOrDefault(t =>
                        string.Equals(t.Interfaces, iface, StringComparison.Ordinal)
                        && !t.Passive)
                    ?.Cost;

            List<OspfIpAddressRow> addrs = snapshot.IpAddresses
                .Where(a =>
                    OspfInventoryEligibility.IsEligible(a)
                    && string.Equals(a.Interface, iface, StringComparison.Ordinal))
                .ToList();
            foreach (OspfIpAddressRow addr in addrs)
            {
                if (!TryInterfaceSubnet(addr, out OspfIpPrefix subnet))
                {
                    continue;
                }

                // Transport-style prefixes only (/30 preferred; reject host /32 loopbacks).
                if (subnet.PrefixLength is < 30 or > 30)
                {
                    continue;
                }

                if (candidates.Any(c =>
                        string.Equals(c.Interface, iface, StringComparison.Ordinal)
                        && c.Subnet.Equals(subnet)))
                {
                    continue;
                }

                candidates.Add((iface, subnet, sourceCost));
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        if (candidates.Count > 1)
        {
            errors.Add(
                "Multiple Full-neighbour backbone adjacency candidates were observed ("
                + string.Join(
                    ", ",
                    candidates.Select(static c => $"{c.Interface}:{c.Subnet.ToCidrString()}"))
                + "); refusing to select without unique evidence.");
            return;
        }

        (string Interface, OspfIpPrefix Subnet, string? SourceCost) chosen = candidates[0];
        // Ensure the neighbour interface is not already classified as a site-only template without
        // backbone ownership evidence: require either a backbone template/network or no conflicting
        // non-backbone exclusive claim.
        bool onBackboneTemplate = snapshot.InterfaceTemplates.Any(t =>
            OspfInventoryEligibility.IsEligible(t)
            && string.Equals(t.Interfaces, chosen.Interface, StringComparison.Ordinal)
            && areas.Any(a =>
                a.IsBackbone
                && string.Equals(a.Name, t.Area, StringComparison.OrdinalIgnoreCase)));
        bool onBackboneNetwork = snapshot.Networks.Any(n =>
            areas.Any(a =>
                a.IsBackbone
                && (string.Equals(a.Name, n.Area, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(NormalizeAreaId(a.AreaId), NormalizeAreaId(n.Area), StringComparison.Ordinal)))
            && OspfIpPrefix.TryParse(n.Network, out OspfIpPrefix net)
            && (net.Contains(chosen.Subnet) || chosen.Subnet.Contains(net) || net.Equals(chosen.Subnet)));

        if (snapshot.InterfaceTemplates.Count > 0 && !onBackboneTemplate && !onBackboneNetwork
            && snapshot.Networks.Count > 0 && !onBackboneNetwork)
        {
            // Soft: still allow when only neighbour evidence exists and no explicit site-only exclusive
            // active template claims the interface.
            bool siteActiveClaim = snapshot.InterfaceTemplates.Any(t =>
                OspfInventoryEligibility.IsEligible(t)
                && !t.Passive
                && string.Equals(t.Interfaces, chosen.Interface, StringComparison.Ordinal)
                && areas.Any(a =>
                    !a.IsBackbone
                    && string.Equals(a.Name, t.Area, StringComparison.OrdinalIgnoreCase)));
            if (siteActiveClaim)
            {
                errors.Add(
                    $"Full neighbour on '{chosen.Interface}' is claimed by an active site-area template; "
                    + "refusing to classify as backbone.");
                return;
            }
        }

        backhauls.Add(new OspfPlanBackhaulTemplate
        {
            AreaName = backboneName,
            Interface = chosen.Interface,
            NetworkCidr = chosen.Subnet.ToCidrString(),
            Cost = FleetBackhaulCost,
            Type = "ptp",
            Passive = false,
            SourceCost = chosen.SourceCost,
        });
        notes.Add(
            $"Classified backbone backhaul '{chosen.Interface}' {chosen.Subnet.ToCidrString()} "
            + "from unique Full OSPF neighbour evidence"
            + (string.IsNullOrWhiteSpace(chosen.SourceCost)
                ? "."
                : $" (source cost '{chosen.SourceCost}' normalised to {FleetBackhaulCost})."));
    }

    /// <summary>
    /// Determines whether an area belongs to the selected instance; unassigned areas are treated as belonging.
    /// </summary>
    /// <param name="area">Area row to test.</param>
    /// <param name="instance">Selected instance, if any.</param>
    /// <returns><see langword="true"/> when the area applies to the instance.</returns>
    private static bool BelongsToInstance(OspfAreaRow area, OspfInstanceRow? instance)
    {
        if (instance is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(area.Instance))
        {
            return true;
        }

        return string.Equals(area.Instance, instance.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Looks up the instance name recorded on a captured area.
    /// </summary>
    /// <param name="snapshot">Captured router state.</param>
    /// <param name="areaName">Area name to find.</param>
    /// <returns>The trimmed instance name, or null when not recorded.</returns>
    private static string? ResolveAreaInstanceName(OspfCaptureSnapshot snapshot, string areaName)
    {
        OspfAreaRow? area = snapshot.Areas.FirstOrDefault(a =>
            string.Equals(a.Name, areaName, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(area?.Instance) ? null : area!.Instance!.Trim();
    }

    /// <summary>
    /// Resolves an area reference, given as a name or an area ID, to a planned area name.
    /// </summary>
    /// <param name="areaRef">Area name or area ID from a captured row.</param>
    /// <param name="areas">Planned areas.</param>
    /// <param name="areaNameById">Area names keyed by area ID.</param>
    /// <returns>The planned area name, or null when the reference is blank or unknown.</returns>
    private static string? ResolveAreaName(
        string? areaRef,
        List<OspfPlanArea> areas,
        Dictionary<string, string> areaNameById)
    {
        if (string.IsNullOrWhiteSpace(areaRef))
        {
            return null;
        }

        OspfPlanArea? byName = areas.FirstOrDefault(a =>
            string.Equals(a.Name, areaRef, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName.Name;
        }

        string id = NormalizeAreaId(areaRef);
        if (areaNameById.TryGetValue(id, out string? name))
        {
            return name;
        }

        return null;
    }

    /// <summary>
    /// Trims an area ID, treating a blank one as the backbone.
    /// </summary>
    /// <param name="areaId">Raw area ID.</param>
    /// <returns>The normalised area ID.</returns>
    private static string NormalizeAreaId(string? areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
        {
            return BackboneAreaId;
        }

        return areaId.Trim();
    }

    /// <summary>
    /// Determines whether a network type denotes point-to-point.
    /// </summary>
    /// <param name="networkType">RouterOS network type text.</param>
    /// <returns><see langword="true"/> for <c>point-to-point</c> or <c>ptp</c>.</returns>
    private static bool IsPointToPoint(string? networkType) =>
        networkType is not null
        && (networkType.Equals("point-to-point", StringComparison.OrdinalIgnoreCase)
            || networkType.Equals("ptp", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Derives the connected subnet of an interface address.
    /// </summary>
    /// <param name="addr">Address row to evaluate.</param>
    /// <param name="subnet">Receives the subnet on success.</param>
    /// <returns><see langword="true"/> when a subnet could be determined.</returns>
    private static bool TryInterfaceSubnet(OspfIpAddressRow addr, out OspfIpPrefix subnet)
    {
        subnet = default;
        if (!OspfIpPrefix.TryParseHost(addr.Address, out _, out int prefixLength))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(addr.Network)
            && OspfIpPrefix.TryParse($"{addr.Network.Trim()}/{prefixLength}", out subnet))
        {
            return true;
        }

        return OspfIpPrefix.TryParse(addr.Address, out subnet);
    }

    /// <summary>
    /// Trims a value, returning null for null or blank input.
    /// </summary>
    /// <param name="value">Text to normalise.</param>
    /// <returns>The trimmed text, or null.</returns>
    private static string? NullIfWhite(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Ensures the derived management <c>/24</c> is available as a consolidation input when needed.
    /// Does not inject a duplicate <c>/24</c> when a broader site matcher already covers it and there
    /// are no more-specific management subdivisions to fold — the MGMT area-range remains the
    /// summarisation artifact in that hierarchy.
    /// </summary>
    private static List<OspfPlanSiteNetworkTemplate> EnsureDerivedManagementSiteMatcher(
        List<OspfPlanSiteNetworkTemplate> sites,
        IReadOnlyList<OspfPlanArea> areas,
        string derivedMgmtCidr,
        List<string> notes)
    {
        OspfPlanArea? site = areas.FirstOrDefault(static a => !a.IsBackbone);
        if (site is null || !OspfIpPrefix.TryParse(derivedMgmtCidr, out OspfIpPrefix mgmt))
        {
            return sites;
        }

        List<OspfIpPrefix> siteNets = sites
            .Where(s => string.Equals(s.AreaName, site.Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s =>
                OspfNetworkList.TryParseAll(s.NetworkCidr, out List<OspfIpPrefix> nets, out _)
                    ? nets
                    : [])
            .ToList();

        bool exactPresent = siteNets.Any(n => n.Equals(mgmt));
        if (exactPresent)
        {
            return sites;
        }

        bool coveredByBroader = siteNets.Any(n => n.Contains(mgmt) && !n.Equals(mgmt));
        bool hasMoreSpecific = siteNets.Any(n => mgmt.Contains(n) && !n.Equals(mgmt));

        if (coveredByBroader && !hasMoreSpecific)
        {
            notes.Add(
                $"Derived management /24 '{derivedMgmtCidr}' is already covered by a broader site matcher; "
                + "not injecting a duplicate /24 into interface-templates "
                + "(MGMT area-range remains the summarisation artifact).");
            return sites;
        }

        notes.Add(
            hasMoreSpecific
                ? $"Adding derived management /24 '{derivedMgmtCidr}' so subdivided management matchers can fold during consolidation."
                : $"Adding derived management /24 '{derivedMgmtCidr}' as a site matcher input (no covering site network was present).");
        List<OspfPlanSiteNetworkTemplate> result = sites.ToList();
        result.Add(new OspfPlanSiteNetworkTemplate
        {
            AreaName = site.Name,
            Interfaces = OspfSiteMatcherConsolidation.AllInterfaces,
            NetworkCidr = mgmt.ToCidrString(),
            Comment = OspfFleetNaming.ManagementRangeComment,
            Passive = true,
        });
        return result;
    }

    /// <summary>
    /// Ensures the site area carries a MGMT area range for each derived management <c>/24</c>.
    /// Existing MGMT-commented ranges whose CIDR is not in the derived set are removed.
    /// </summary>
    private static List<OspfPlanAreaRange> EnsureManagementAreaRanges(
        List<OspfPlanAreaRange> ranges,
        IReadOnlyList<OspfPlanArea> areas,
        IReadOnlyList<string> derivedMgmtCidrs,
        List<string> notes,
        List<string> errors)
    {
        OspfPlanArea? site = areas.FirstOrDefault(static a => !a.IsBackbone);
        if (site is null)
        {
            errors.Add("Cannot attach derived management /24: site area is missing.");
            return ranges;
        }

        HashSet<string> required = new(
            derivedMgmtCidrs.Where(static c => !string.IsNullOrWhiteSpace(c)).Select(static c => c.Trim()),
            StringComparer.OrdinalIgnoreCase);
        if (required.Count == 0)
        {
            return ranges;
        }

        List<OspfPlanAreaRange> result = ranges.ToList();
        List<OspfPlanAreaRange> existingMgmt = result
            .Where(r =>
                string.Equals(r.AreaName, site.Name, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(r.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase)
                    || required.Contains(r.RangeCidr ?? string.Empty)))
            .ToList();

        foreach (OspfPlanAreaRange row in existingMgmt)
        {
            if (!required.Contains(row.RangeCidr ?? string.Empty))
            {
                notes.Add(
                    $"Removing MGMT area range '{row.RangeCidr}' on '{row.AreaName}' because it is not in the "
                    + $"derived management set [{string.Join(", ", required.OrderBy(static s => s, StringComparer.Ordinal))}].");
                result.Remove(row);
                continue;
            }

            if (!string.Equals(row.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add(
                    $"Normalising management range '{row.RangeCidr}' comment '{row.Comment ?? "<none>"}' to "
                    + $"{OspfFleetNaming.ManagementRangeComment}.");
                result.Remove(row);
                result.Add(new OspfPlanAreaRange
                {
                    AreaName = site.Name,
                    RangeCidr = row.RangeCidr!,
                    Comment = OspfFleetNaming.ManagementRangeComment,
                    Advertise = row.Advertise ?? true,
                });
            }
        }

        HashSet<string> present = new(
            result
                .Where(r =>
                    string.Equals(r.AreaName, site.Name, StringComparison.OrdinalIgnoreCase)
                    && required.Contains(r.RangeCidr ?? string.Empty))
                .Select(r => r.RangeCidr!),
            StringComparer.OrdinalIgnoreCase);

        foreach (string cidr in required.OrderBy(static s => s, StringComparer.Ordinal))
        {
            if (present.Contains(cidr))
            {
                continue;
            }

            notes.Add($"Adding mandatory MGMT area range '{cidr}' on site area '{site.Name}'.");
            result.Add(new OspfPlanAreaRange
            {
                AreaName = site.Name,
                RangeCidr = cidr,
                Comment = OspfFleetNaming.ManagementRangeComment,
                Advertise = true,
            });
        }

        return result;
    }

    /// <summary>Normalises the plan's derived management prefix set for callers and hashing.</summary>
    internal static IReadOnlyList<string> GetDerivedManagementCidrs(OspfMigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.DerivedManagementCidrs.Count > 0)
        {
            return plan.DerivedManagementCidrs
                .Where(static c => !string.IsNullOrWhiteSpace(c))
                .Select(static c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static s => s, StringComparer.Ordinal)
                .ToList();
        }

        return string.IsNullOrWhiteSpace(plan.DerivedManagementCidr)
            ? []
            : [plan.DerivedManagementCidr.Trim()];
    }

    /// <summary>
    /// Copies a plan with a replacement list of validation errors.
    /// </summary>
    /// <param name="plan">Plan to copy.</param>
    /// <param name="errors">Validation errors for the copy.</param>
    /// <returns>A new plan; the content hash is not carried over.</returns>
    private static OspfMigrationPlan ClonePlan(OspfMigrationPlan plan, IReadOnlyList<string> errors) =>
        new()
        {
            ImportId = plan.ImportId,
            JobId = plan.JobId,
            RouterIp = plan.RouterIp,
            Identity = plan.Identity,
            SourceRouterOsVersion = plan.SourceRouterOsVersion,
            Architecture = plan.Architecture,
            SourceKind = plan.SourceKind,
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
            DerivedManagementCidrs = plan.DerivedManagementCidrs,
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            SupersededTemplates = plan.SupersededTemplates,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = plan.NormalisationNotes,
            ValidationErrors = errors.ToList(),
        };

    /// <summary>
    /// Canonical hashed representation: camelCase JSON of the fields below (excludes
    /// <see cref="OspfMigrationPlan.ContentHash"/>, normalisation notes, and validation errors).
    /// </summary>
    internal static string ComputeContentHash(OspfMigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        OspfMigrationPlanHashPayload forHash = new()
        {
            ImportId = plan.ImportId,
            JobId = plan.JobId,
            RouterIp = plan.RouterIp,
            Identity = plan.Identity,
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
            DerivedManagementCidrs = GetDerivedManagementCidrs(plan),
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            SupersededTemplates = plan.SupersededTemplates,
            OspfV3ToRemove = plan.OspfV3ToRemove,
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            forHash,
            OspfPlanHashJsonContext.Default.OspfMigrationPlanHashPayload);
        byte[] hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Verifies that a plan carries a content hash equal to a freshly computed one.
    /// </summary>
    /// <param name="plan">Plan to verify.</param>
    /// <returns><see langword="true"/> when the stored hash is present and matches.</returns>
    internal static bool ContentHashMatches(OspfMigrationPlan plan) =>
        !string.IsNullOrWhiteSpace(plan.ContentHash)
        && string.Equals(plan.ContentHash, ComputeContentHash(plan), StringComparison.OrdinalIgnoreCase);
}
