using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Builds an immutable fleet-standard <see cref="OspfMigrationPlan"/> from a capture snapshot.
/// Configuration-driven; no router-identity or sample-prefix hardcoding.
/// </summary>
internal static class OspfMigrationPlanner
{
    public const int FleetBackhaulCost = 10;
    public const string BackboneAreaId = "0.0.0.0";

    private static readonly JsonSerializerOptions HashJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

        OspfInstanceRow? instance = SelectInstance(snapshot, backbone, errors);
        string routerId = instance?.RouterId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(routerId))
        {
            errors.Add("OSPF router-id is missing or empty.");
        }

        string instanceName = ResolveInstanceName(instance, snapshot.SourceKind);

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
            string? defaultCost = null;
            string? sourceDefaultCost = NullIfWhite(area.DefaultCost);
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

            if (!isBb && area.Type is not null
                && !area.Type.Equals("stub", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(area.Type)
                && !area.Type.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add(
                    $"Area '{area.Name}' source type '{area.Type}' normalised to stub (fleet standard).");
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
                DefaultCost = defaultCost,
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

        // Collapse per-VLAN / per-network site matchers into one interfaces=all template per site area.
        // Record superseded source identities as positive KnownLegacy provenance for cleanup.
        OspfSiteConsolidationResult consolidation =
            OspfSiteMatcherConsolidation.ConsolidateWithProvenance(sites, ranges, notes);
        sites = consolidation.Consolidated.ToList();
        List<OspfSupersededTemplateIdentity> superseded = consolidation.Superseded.ToList();

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

        List<OspfV3ObjectRef> v3 = snapshot.OspfV3Objects.ToList();
        if (v3.Count > 0)
        {
            notes.Add($"OSPFv3 objects queued for removal: {v3.Count}.");
        }

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
        plan = new OspfMigrationPlan
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
            Areas = plan.Areas,
            AreaRanges = plan.AreaRanges,
            ActiveBackhauls = plan.ActiveBackhauls,
            PassiveLoopbacks = plan.PassiveLoopbacks,
            PassiveSiteNetworks = plan.PassiveSiteNetworks,
            SupersededTemplates = plan.SupersededTemplates,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = plan.NormalisationNotes,
            ValidationErrors = errors,
        };
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
    /// Canonical hashed representation: camelCase JSON of the fields below (excludes
    /// <see cref="OspfMigrationPlan.ContentHash"/>, normalisation notes, and validation errors).
    /// </summary>
    internal static string ComputeContentHash(OspfMigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var forHash = new
        {
            plan.ImportId,
            plan.JobId,
            plan.RouterIp,
            plan.Identity,
            plan.InstanceName,
            plan.RouterId,
            plan.Areas,
            plan.AreaRanges,
            plan.ActiveBackhauls,
            plan.PassiveLoopbacks,
            plan.PassiveSiteNetworks,
            plan.SupersededTemplates,
            plan.OspfV3ToRemove,
        };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(forHash, HashJsonOptions);
        byte[] hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash);
    }

    internal static bool ContentHashMatches(OspfMigrationPlan plan) =>
        !string.IsNullOrWhiteSpace(plan.ContentHash)
        && string.Equals(plan.ContentHash, ComputeContentHash(plan), StringComparison.OrdinalIgnoreCase);
}
