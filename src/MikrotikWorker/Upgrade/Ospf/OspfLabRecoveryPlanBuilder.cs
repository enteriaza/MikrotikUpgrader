using System.Net;
using System.Text.RegularExpressions;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Builds a fleet-standard OSPF migration plan for RouterOS 7 recovery when auto-converted live
/// templates are incomplete relative to the pre-upgrade intended topology (site/PPPoE matchers and
/// area ranges). Uses live v7 identity/areas/addresses plus a local pre-upgrade <c>.rsc</c> reference.
/// </summary>
internal static class OspfLabRecoveryPlanBuilder
{
    private static readonly Regex AddLineRegex = new(
        @"^\s*add\s+(?<body>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttrRegex = new(
        @"(?<key>[A-Za-z0-9_-]+)=(?<value>""[^""]*""|[^\s\\]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? FindPreUpgradeExportPath(string exportsDirectory, string identity, string routerIp)
    {
        if (!Directory.Exists(exportsDirectory))
        {
            return null;
        }

        IEnumerable<string> candidates = Directory
            .EnumerateFiles(exportsDirectory, "*.rsc", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return name.Contains(identity, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(routerIp, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(static path => File.GetLastWriteTimeUtc(path));

        foreach (string path in candidates)
        {
            string text = File.ReadAllText(path);
            if (text.Contains("Active PPPoE Range", StringComparison.Ordinal)
                && text.Contains("/routing ospf network", StringComparison.OrdinalIgnoreCase)
                && text.Contains("/routing ospf area range", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    public static OspfPlanValidationResult BuildFromLiveAndReference(
        Guid importId,
        string jobId,
        IPAddress routerIp,
        string identity,
        string routerOsVersion,
        string? architecture,
        OspfV7Inventory live,
        IReadOnlyList<OspfIpAddressRow> addresses,
        string referenceExportPath)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceExportPath);

        ReferenceTopology reference = ParseReferenceExport(File.ReadAllText(referenceExportPath));
        if (reference.Networks.Count == 0)
        {
            return Failed("Reference export contained no /routing ospf network statements.");
        }

        Dictionary<string, string> areaNameMap = BuildAreaNameMap(reference, live.Areas);
        List<string> mapErrors = [];
        foreach (ReferenceNetwork net in reference.Networks)
        {
            if (!areaNameMap.ContainsKey(net.Area))
            {
                mapErrors.Add($"Cannot map reference OSPF area '{net.Area}' to a live RouterOS 7 area.");
            }
        }

        foreach (ReferenceRange range in reference.Ranges)
        {
            if (!areaNameMap.ContainsKey(range.Area))
            {
                mapErrors.Add($"Cannot map reference OSPF area-range area '{range.Area}' to a live RouterOS 7 area.");
            }
        }

        if (mapErrors.Count > 0)
        {
            return Failed(string.Join(" ", mapErrors.Distinct(StringComparer.Ordinal)));
        }

        string backboneLiveName = live.Areas.FirstOrDefault(static a =>
                string.Equals(a.AreaId, OspfMigrationPlanner.BackboneAreaId, StringComparison.Ordinal))
            ?.Name
            ?? "backbone-v2";

        List<OspfInterfaceTemplateRow> templates = [];
        List<OspfAreaRangeRow> ranges = [];
        List<string> notes =
        [
            $"Recovery plan reconstructed from reference export '{Path.GetFileName(referenceExportPath)}' mapped onto live RouterOS 7 area names.",
        ];

        foreach (ReferenceNetwork net in reference.Networks)
        {
            string liveArea = areaNameMap[net.Area];
            bool isBackbone = string.Equals(liveArea, backboneLiveName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(net.Area, "backbone", StringComparison.OrdinalIgnoreCase);

            if (!OspfIpPrefix.TryParse(net.Network, out OspfIpPrefix prefix))
            {
                return Failed($"Reference network '{net.Network}' is not a valid IPv4 prefix.");
            }

            if (isBackbone && prefix.PrefixLength == 32)
            {
                OspfIpAddressRow? loopAddr = addresses.FirstOrDefault(a =>
                    OspfIpPrefix.TryParseHost(a.Address, out uint host, out _)
                    && host == prefix.NetworkAddress);
                templates.Add(new OspfInterfaceTemplateRow
                {
                    Area = liveArea,
                    Interfaces = loopAddr?.Interface,
                    Networks = prefix.ToCidrString(),
                    Passive = true,
                    Disabled = false,
                    Comment = net.Comment,
                });
                continue;
            }

            if (isBackbone)
            {
                OspfIpAddressRow? bhAddr = addresses.FirstOrDefault(a =>
                    TryInterfaceSubnet(a, out OspfIpPrefix subnet)
                    && (subnet.Equals(prefix) || prefix.Contains(subnet)));
                if (bhAddr is null || string.IsNullOrWhiteSpace(bhAddr.Interface))
                {
                    return Failed(
                        $"Reference backhaul '{prefix.ToCidrString()}' could not be correlated to a live interface address.");
                }

                if (!TryInterfaceSubnet(bhAddr, out OspfIpPrefix exact))
                {
                    return Failed($"Live backhaul address '{bhAddr.Address}' could not be parsed as a subnet.");
                }

                templates.Add(new OspfInterfaceTemplateRow
                {
                    Area = liveArea,
                    Interfaces = bhAddr.Interface,
                    Networks = exact.ToCidrString(),
                    Type = "ptp",
                    Cost = "10",
                    Passive = false,
                    Disabled = false,
                });
                continue;
            }

            templates.Add(new OspfInterfaceTemplateRow
            {
                Area = liveArea,
                Networks = prefix.ToCidrString(),
                Passive = true,
                Disabled = false,
                Comment = net.Comment,
            });
        }

        foreach (ReferenceRange range in reference.Ranges)
        {
            ranges.Add(new OspfAreaRangeRow
            {
                Area = areaNameMap[range.Area],
                Range = range.Range,
                Comment = range.Comment,
                Advertise = true,
            });
        }

        // Include live auto-converted passive site templates so planner consolidation records
        // complete KnownLegacy provenance for cleanup (ROS7 conversion leaves per-VLAN matchers).
        int liveSiteImported = 0;
        foreach (OspfInterfaceTemplateRow liveTemplate in live.Templates.Where(static t => !t.Disabled))
        {
            string? areaName = liveTemplate.Area;
            if (string.IsNullOrWhiteSpace(areaName))
            {
                continue;
            }

            bool isBackbone = string.Equals(areaName, backboneLiveName, StringComparison.OrdinalIgnoreCase)
                || live.Areas.Any(a =>
                    string.Equals(a.Name, areaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.AreaId, OspfMigrationPlanner.BackboneAreaId, StringComparison.Ordinal));
            if (isBackbone || !liveTemplate.Passive)
            {
                continue;
            }

            if (!OspfNetworkList.TryParseAll(liveTemplate.Networks, out List<OspfIpPrefix> liveNets, out _))
            {
                continue;
            }

            bool alreadyRepresented = templates.Any(t =>
                string.Equals(t.Area, areaName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Interfaces ?? string.Empty, liveTemplate.Interfaces ?? string.Empty, StringComparison.Ordinal)
                && OspfNetworkList.SetsEqual(t.Networks, liveTemplate.Networks));
            if (alreadyRepresented)
            {
                continue;
            }

            // Skip if every live network is already covered by a reference-derived site template network
            // with interfaces=all intent (broad matchers). Still import when interfaces are specific.
            bool specificInterfaces = !string.IsNullOrWhiteSpace(liveTemplate.Interfaces)
                && !OspfSiteMatcherConsolidation.IsAllInterfaces(liveTemplate.Interfaces);
            if (!specificInterfaces
                && liveNets.All(n => templates.Any(t =>
                    string.Equals(t.Area, areaName, StringComparison.OrdinalIgnoreCase)
                    && OspfNetworkList.TryParseAll(t.Networks, out List<OspfIpPrefix> refNets, out _)
                    && refNets.Any(r => r.Equals(n) || r.Contains(n)))))
            {
                continue;
            }

            templates.Add(new OspfInterfaceTemplateRow
            {
                Id = liveTemplate.Id,
                Area = liveTemplate.Area,
                Interfaces = liveTemplate.Interfaces,
                Networks = liveTemplate.Networks,
                Type = liveTemplate.Type,
                Cost = liveTemplate.Cost,
                Passive = liveTemplate.Passive,
                Disabled = liveTemplate.Disabled,
                Comment = liveTemplate.Comment,
                Auth = liveTemplate.Auth,
                AuthId = liveTemplate.AuthId,
                AuthKey = liveTemplate.AuthKey,
                AuthenticationKey = liveTemplate.AuthenticationKey,
                Priority = liveTemplate.Priority,
                UseBfd = liveTemplate.UseBfd,
                HelloInterval = liveTemplate.HelloInterval,
                DeadInterval = liveTemplate.DeadInterval,
                RetransmitInterval = liveTemplate.RetransmitInterval,
                TransmitDelay = liveTemplate.TransmitDelay,
                InstanceId = liveTemplate.InstanceId,
                PrefixList = liveTemplate.PrefixList,
                VlinkNeighborId = liveTemplate.VlinkNeighborId,
                VlinkTransitArea = liveTemplate.VlinkTransitArea,
            });
            liveSiteImported++;
        }

        if (liveSiteImported > 0)
        {
            notes.Add(
                $"Imported {liveSiteImported} live passive site template(s) for consolidation/KnownLegacy provenance.");
        }

        OspfCaptureSnapshot snapshot = new()
        {
            ImportId = importId,
            JobId = jobId,
            RouterIp = routerIp,
            Identity = identity,
            RouterOsVersion = routerOsVersion,
            Architecture = architecture,
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances = live.Instances,
            Areas = live.Areas,
            AreaRanges = ranges,
            InterfaceTemplates = templates,
            IpAddresses = addresses,
            OspfV3Objects = live.OspfV3Objects,
        };

        OspfPlanValidationResult result = OspfMigrationPlanner.Build(snapshot);
        if (result.Succeeded)
        {
            // Re-attach recovery note without breaking content hash: notes are excluded from hash.
            var plan = result.Plan;
            List<string> mergedNotes = notes.Concat(plan.NormalisationNotes).ToList();
            result = new OspfPlanValidationResult
            {
                Plan = CloneWithNotes(plan, mergedNotes),
            };
        }

        return result;
    }

    private static OspfMigrationPlan CloneWithNotes(OspfMigrationPlan plan, IReadOnlyList<string> notes) =>
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
            SupersededTemplates = plan.SupersededTemplates,
            RemoveAllRoutingFilters = plan.RemoveAllRoutingFilters,
            ClearAllRedistribution = plan.ClearAllRedistribution,
            RemoveAllOspfV3 = plan.RemoveAllOspfV3,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = notes,
            ValidationErrors = plan.ValidationErrors,
        };

    private static OspfPlanValidationResult Failed(string detail) =>
        new()
        {
            Plan = new OspfMigrationPlan
            {
                ImportId = Guid.Empty,
                JobId = string.Empty,
                RouterIp = string.Empty,
                Identity = string.Empty,
                SourceRouterOsVersion = string.Empty,
                Architecture = null,
                SourceKind = OspfCaptureSourceKind.RouterOsV7,
                InstanceName = string.Empty,
                RouterId = string.Empty,
                ValidationErrors = [detail],
            },
        };

    private static Dictionary<string, string> BuildAreaNameMap(
        ReferenceTopology reference,
        IReadOnlyList<OspfAreaRow> liveAreas)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> refAreas = reference.Networks.Select(static n => n.Area)
            .Concat(reference.Ranges.Select(static r => r.Area))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string refArea in refAreas)
        {
            OspfAreaRow? exact = liveAreas.FirstOrDefault(a =>
                string.Equals(a.Name, refArea, StringComparison.OrdinalIgnoreCase));
            if (exact?.Name is not null)
            {
                map[refArea] = exact.Name;
                continue;
            }

            OspfAreaRow? prefixed = liveAreas.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Name)
                && a.Name.StartsWith(refArea, StringComparison.OrdinalIgnoreCase));
            if (prefixed?.Name is not null)
            {
                map[refArea] = prefixed.Name;
                continue;
            }

            if (refArea.Equals("backbone", StringComparison.OrdinalIgnoreCase))
            {
                OspfAreaRow? bb = liveAreas.FirstOrDefault(static a =>
                    string.Equals(a.AreaId, OspfMigrationPlanner.BackboneAreaId, StringComparison.Ordinal));
                if (bb?.Name is not null)
                {
                    map[refArea] = bb.Name;
                }
            }
        }

        return map;
    }

    internal static ReferenceTopology ParseReferenceExport(string exportText)
    {
        List<ReferenceNetwork> networks = [];
        List<ReferenceRange> ranges = [];
        string? section = null;
        List<string> logicalLines = FlattenContinuationLines(exportText);

        foreach (string line in logicalLines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("/routing ospf area range", StringComparison.OrdinalIgnoreCase))
            {
                section = "range";
                continue;
            }

            if (trimmed.StartsWith("/routing ospf network", StringComparison.OrdinalIgnoreCase))
            {
                section = "network";
                continue;
            }

            if (trimmed.StartsWith('/'))
            {
                section = null;
                continue;
            }

            Match add = AddLineRegex.Match(trimmed);
            if (!add.Success || section is null)
            {
                continue;
            }

            Dictionary<string, string> attrs = ParseAttributes(add.Groups["body"].Value);
            if (section == "network"
                && attrs.TryGetValue("area", out string? area)
                && attrs.TryGetValue("network", out string? network))
            {
                attrs.TryGetValue("comment", out string? comment);
                networks.Add(new ReferenceNetwork(area, network, comment));
            }
            else if (section == "range"
                     && attrs.TryGetValue("area", out string? rangeArea)
                     && attrs.TryGetValue("range", out string? range))
            {
                attrs.TryGetValue("comment", out string? comment);
                ranges.Add(new ReferenceRange(rangeArea, range, comment));
            }
        }

        return new ReferenceTopology(networks, ranges);
    }

    private static List<string> FlattenContinuationLines(string text)
    {
        // MikroTik exports use a trailing '\' to continue a logical line onto the next physical line.
        string normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        while (true)
        {
            string next = Regex.Replace(
                normalised,
                @"\\\n[ \t]*",
                string.Empty,
                RegexOptions.CultureInvariant);
            if (string.Equals(next, normalised, StringComparison.Ordinal))
            {
                break;
            }

            normalised = next;
        }

        return normalised
            .Split('\n', StringSplitOptions.None)
            .Where(static l => !string.IsNullOrWhiteSpace(l))
            .ToList();
    }

    private static Dictionary<string, string> ParseAttributes(string body)
    {
        Dictionary<string, string> attrs = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttrRegex.Matches(body))
        {
            string key = match.Groups["key"].Value;
            string value = match.Groups["value"].Value;
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            attrs[key] = value;
        }

        return attrs;
    }

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

    internal sealed record ReferenceNetwork(string Area, string Network, string? Comment);
    internal sealed record ReferenceRange(string Area, string Range, string? Comment);
    internal sealed record ReferenceTopology(
        IReadOnlyList<ReferenceNetwork> Networks,
        IReadOnlyList<ReferenceRange> Ranges);
}
