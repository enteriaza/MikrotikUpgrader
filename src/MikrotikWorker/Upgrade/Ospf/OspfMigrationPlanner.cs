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
    public const int FleetBackhaulCost = 10;
    public const string BackboneAreaId = "0.0.0.0";

    public static OspfPlanValidationResult Build(OspfCaptureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<string> errors = [];
        List<string> notes = [];

        if (snapshot.Instances.Count == 0)
        {
            errors.Add("OSPF instance is missing; empty OSPF configuration is a serious configuration error.");
        }

        OspfAreaRow? backbone = snapshot.Areas.FirstOrDefault(static a =>
            string.Equals(NormalizeAreaId(a.AreaId), BackboneAreaId, StringComparison.Ordinal));
        if (backbone is null)
        {
            errors.Add("Backbone area (area-id 0.0.0.0) is missing.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.Identity))
        {
            errors.Add("Router /system identity name is missing or empty.");
        }

        OspfInstanceRow? instance = SelectInstance(snapshot, backbone, errors);
        string routerId = string.Empty;
        // Corroborate multi-address Loopback selection with the OSPF instance router-id when present.
        OspfLoopbackRouterIdResult loopbackRid = OspfLoopbackRouterId.Derive(
            snapshot.IpAddresses,
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
            if (string.IsNullOrWhiteSpace(instanceRid))
            {
                errors.Add("OSPF instance router-id is missing or empty.");
            }
            else if (!OspfLoopbackRouterId.EqualsRouterId(instanceRid, routerId))
            {
                errors.Add(
                    $"OSPF instance router-id '{instanceRid}' does not match Loopback /32 address "
                    + $"'{loopbackRid.LoopbackCidr}' on '{loopbackRid.LoopbackInterfaceName}'.");
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
                $"Source OSPF instance name '{instance.Name}' differs from deterministic "
                + $"'{instanceName}' for {snapshot.SourceKind}; retaining deterministic logical name.");
        }

        if (snapshot.SourceKind == OspfCaptureSourceKind.RouterOsV6 && snapshot.Networks.Count == 0
            && snapshot.InterfaceTemplates.Count == 0)
        {
            errors.Add("No OSPF network statements (or v7 templates) were found; configuration is incomplete.");
        }

        if (snapshot.SourceKind == OspfCaptureSourceKind.RouterOsV7 && snapshot.InterfaceTemplates.Count == 0
            && snapshot.Networks.Count == 0)
        {
            errors.Add("No OSPF interface-templates were found on RouterOS 7; cannot establish topology.");
        }

        List<OspfPlanArea> areas = [];
        foreach (OspfAreaRow area in snapshot.Areas.Where(a => BelongsToInstance(a, instance)))
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
        foreach (OspfAreaRangeRow range in snapshot.AreaRanges)
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

        if (snapshot.SourceKind == OspfCaptureSourceKind.RouterOsV6)
        {
            ClassifyFromV6(
                snapshot,
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
                snapshot,
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
            errors.Add("No valid active backbone PTP backhaul could be classified.");
        }

        if (loopbacks.Count == 0)
        {
            errors.Add("No backbone loopback /32 advertisement could be classified.");
        }

        // Active template must not use a network that contains another local interface's address
        // on a different interface (would risk unintended activation under OR matching).
        errors.AddRange(ValidateBackhaulTemplateIsolation(backhauls, snapshot.IpAddresses));

        // Derive management /24 and attach the MGMT area-range BEFORE consolidation so FoldPrefix
        // can fold subdivided management matchers into the planned summary during provenance capture.
        string? derivedMgmt = null;
        if (loopbackRid.Succeeded && backhauls.Count > 0)
        {
            IReadOnlyList<string> mgmtHints = OspfSiteManagementSummary.CollectCorroboratingManagementCidrs(
                ranges,
                snapshot.Networks);
            OspfSiteManagementSummaryResult mgmt = OspfSiteManagementSummary.Derive(
                snapshot.IpAddresses,
                loopbackRid,
                backhauls,
                mgmtHints);
            if (!mgmt.Succeeded || string.IsNullOrWhiteSpace(mgmt.ManagementCidr))
            {
                errors.AddRange(mgmt.Errors);
            }
            else
            {
                derivedMgmt = mgmt.ManagementCidr;
                notes.Add(
                    $"Derived site management /24 '{derivedMgmt}' from "
                    + string.Join("; ", mgmt.ContributingAddresses));
                ranges = EnsureManagementAreaRange(
                    ranges,
                    areas,
                    derivedMgmt,
                    notes,
                    errors);
                sites = EnsureDerivedManagementSiteMatcher(sites, areas, derivedMgmt, notes);
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
        errors.AddRange(OspfBackboneSafety.ValidatePlanInvariants(plan, snapshot.IpAddresses));
        plan = ClonePlan(plan, errors);
        plan.ContentHash = ComputeContentHash(plan);

        return new OspfPlanValidationResult { Plan = plan };
    }

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
        foreach (OspfInterfaceTemplateRow template in snapshot.InterfaceTemplates.Where(t => !t.Disabled))
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

            if (isBackbone && !passive && ptp && !string.IsNullOrWhiteSpace(template.Interfaces)
                && OspfIpPrefix.TryParse(template.Networks, out OspfIpPrefix net))
            {
                OspfIpAddressRow? addr = snapshot.IpAddresses.FirstOrDefault(a =>
                    string.Equals(a.Interface, template.Interfaces, StringComparison.Ordinal)
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

            foreach (OspfIpAddressRow addr in addresses)
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

    private static OspfInstanceRow? SelectInstance(
        OspfCaptureSnapshot snapshot,
        OspfAreaRow? backbone,
        List<string> errors)
    {
        if (snapshot.Instances.Count == 0)
        {
            return null;
        }

        if (snapshot.Instances.Count == 1)
        {
            return snapshot.Instances[0];
        }

        // Prefer instance owning backbone; exclude version=3
        IEnumerable<OspfInstanceRow> v2 = snapshot.Instances.Where(static i =>
            !string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase));
        List<OspfInstanceRow> list = v2.ToList();
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

        errors.Add("Multiple OSPF instances present; cannot select the owning instance unambiguously.");
        return list.FirstOrDefault();
    }

    private static string ResolveInstanceName(OspfInstanceRow? instance, OspfCaptureSourceKind kind)
    {
        if (!string.IsNullOrWhiteSpace(instance?.Name))
        {
            return instance.Name!;
        }

        return kind == OspfCaptureSourceKind.RouterOsV7 ? "default-v2" : "default";
    }

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

    private static string? ResolveAreaInstanceName(OspfCaptureSnapshot snapshot, string areaName)
    {
        OspfAreaRow? area = snapshot.Areas.FirstOrDefault(a =>
            string.Equals(a.Name, areaName, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(area?.Instance) ? null : area!.Instance!.Trim();
    }

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

    private static string NormalizeAreaId(string? areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
        {
            return BackboneAreaId;
        }

        return areaId.Trim();
    }

    private static bool IsPointToPoint(string? networkType) =>
        networkType is not null
        && (networkType.Equals("point-to-point", StringComparison.OrdinalIgnoreCase)
            || networkType.Equals("ptp", StringComparison.OrdinalIgnoreCase));

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

    private static List<OspfPlanAreaRange> EnsureManagementAreaRange(
        List<OspfPlanAreaRange> ranges,
        IReadOnlyList<OspfPlanArea> areas,
        string derivedMgmtCidr,
        List<string> notes,
        List<string> errors)
    {
        OspfPlanArea? site = areas.FirstOrDefault(static a => !a.IsBackbone);
        if (site is null)
        {
            errors.Add("Cannot attach derived management /24: site area is missing.");
            return ranges;
        }

        List<OspfPlanAreaRange> result = ranges.ToList();
        List<OspfPlanAreaRange> existingMgmt = result
            .Where(r =>
                string.Equals(r.AreaName, site.Name, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(r.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.RangeCidr, derivedMgmtCidr, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (existingMgmt.Count == 0)
        {
            notes.Add(
                $"Adding mandatory MGMT area range '{derivedMgmtCidr}' on site area '{site.Name}'.");
            result.Add(new OspfPlanAreaRange
            {
                AreaName = site.Name,
                RangeCidr = derivedMgmtCidr,
                Comment = OspfFleetNaming.ManagementRangeComment,
                Advertise = true,
            });
            return result;
        }

        foreach (OspfPlanAreaRange row in existingMgmt)
        {
            if (!string.Equals(row.RangeCidr, derivedMgmtCidr, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add(
                    $"Existing MGMT area range '{row.RangeCidr}' on '{row.AreaName}' differs from derived "
                    + $"'{derivedMgmtCidr}'; replacing with derived fleet management /24.");
                result.Remove(row);
                result.Add(new OspfPlanAreaRange
                {
                    AreaName = site.Name,
                    RangeCidr = derivedMgmtCidr,
                    Comment = OspfFleetNaming.ManagementRangeComment,
                    Advertise = row.Advertise ?? true,
                });
            }
            else if (!string.Equals(row.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add(
                    $"Normalising management range '{row.RangeCidr}' comment '{row.Comment ?? "<none>"}' to "
                    + $"{OspfFleetNaming.ManagementRangeComment}.");
                result.Remove(row);
                result.Add(new OspfPlanAreaRange
                {
                    AreaName = site.Name,
                    RangeCidr = derivedMgmtCidr,
                    Comment = OspfFleetNaming.ManagementRangeComment,
                    Advertise = row.Advertise ?? true,
                });
            }
        }

        return result;
    }

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

    internal static bool ContentHashMatches(OspfMigrationPlan plan) =>
        !string.IsNullOrWhiteSpace(plan.ContentHash)
        && string.Equals(plan.ContentHash, ComputeContentHash(plan), StringComparison.OrdinalIgnoreCase);
}
