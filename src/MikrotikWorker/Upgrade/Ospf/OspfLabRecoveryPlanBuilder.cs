// -----------------------------------------------------------------------------
// File: OspfLabRecoveryPlanBuilder.cs
//
// Description:
// Builds a fleet-standard OSPF migration plan for RouterOS 7 recovery when
// auto-converted live templates are incomplete relative to the pre-upgrade
// intended topology (site/PPPoE matchers and area ranges). Uses live v7
// identity/areas/addresses plus a local pre-upgrade .rsc reference.
//
// -----------------------------------------------------------------------------

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
    /// <summary>
    /// Matches an export <c>add</c> line and captures its attribute body.
    /// </summary>
    private static readonly Regex AddLineRegex = new(
        @"^\s*add\s+(?<body>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches <c>key=value</c> attributes, where the value may be double-quoted.
    /// </summary>
    private static readonly Regex AttrRegex = new(
        @"(?<key>[A-Za-z0-9_-]+)=(?<value>""[^""]*""|[^\s\\]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds a pre-upgrade <c>.rsc</c> export for a router, checking candidates from oldest to newest, that contains the OSPF network and area-range sections and the PPPoE range marker.
    /// </summary>
    /// <param name="exportsDirectory">Directory searched for <c>.rsc</c> files.</param>
    /// <param name="identity">Router identity expected in the file name.</param>
    /// <param name="routerIp">Router IP expected in the file name, as an alternative to the identity.</param>
    /// <returns>The path of the first qualifying file, or null when none qualifies.</returns>
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

    /// <summary>
    /// Builds a recovery plan by mapping the pre-upgrade reference topology onto live RouterOS 7 areas and addresses.
    /// Live passive site templates not covered by the reference are imported so consolidation records their legacy provenance.
    /// </summary>
    /// <param name="importId">Import identifier of the scheduled job.</param>
    /// <param name="jobId">Scheduled job identifier.</param>
    /// <param name="routerIp">Router management address.</param>
    /// <param name="identity">RouterOS identity.</param>
    /// <param name="routerOsVersion">Installed RouterOS version.</param>
    /// <param name="architecture">Device architecture, when known.</param>
    /// <param name="live">Live RouterOS 7 OSPF inventory.</param>
    /// <param name="addresses">Live IP address inventory.</param>
    /// <param name="referenceExportPath">Path of the pre-upgrade <c>.rsc</c> reference export.</param>
    /// <returns>A validation result holding the plan, or a failure when the reference cannot be mapped.</returns>
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

    /// <summary>
    /// Copies a plan with replacement normalisation notes. The content hash is preserved because notes are excluded from it.
    /// </summary>
    /// <param name="plan">Plan to copy.</param>
    /// <param name="notes">Notes for the copy.</param>
    /// <returns>The copied plan.</returns>
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

    /// <summary>
    /// Creates a failed validation result with an otherwise empty plan.
    /// </summary>
    /// <param name="detail">Reason for the failure.</param>
    /// <returns>A result whose plan carries the single validation error.</returns>
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

    /// <summary>
    /// Maps reference area names to live area names by exact name, then by prefix, with <c>backbone</c> mapped to the live backbone area.
    /// </summary>
    /// <param name="reference">Parsed reference topology.</param>
    /// <param name="liveAreas">Live OSPF areas.</param>
    /// <returns>Live area names keyed by reference area name; unmappable areas are omitted.</returns>
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

    /// <summary>
    /// Parses the <c>/routing ospf network</c> and <c>/routing ospf area range</c> sections of a RouterOS export.
    /// </summary>
    /// <param name="exportText">Text of the <c>.rsc</c> export.</param>
    /// <returns>The networks and ranges found.</returns>
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

    /// <summary>
    /// Joins export lines continued with a trailing backslash and drops blank lines.
    /// </summary>
    /// <param name="text">Raw export text.</param>
    /// <returns>The logical lines.</returns>
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

    /// <summary>
    /// Parses <c>key=value</c> attributes from an export line body, unquoting quoted values.
    /// </summary>
    /// <param name="body">Text following <c>add</c>.</param>
    /// <returns>Attributes keyed case-insensitively by name.</returns>
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
    /// Reference topology network used when building a lab recovery plan.
    /// </summary>
    internal sealed record ReferenceNetwork(string Area, string Network, string? Comment);
    /// <summary>
    /// Reference area-range used when building a lab recovery plan.
    /// </summary>
    internal sealed record ReferenceRange(string Area, string Range, string? Comment);
    /// <summary>
    /// Reference topology bundle for lab OSPF recovery plan construction.
    /// </summary>
    internal sealed record ReferenceTopology(
        IReadOnlyList<ReferenceNetwork> Networks,
        IReadOnlyList<ReferenceRange> Ranges);
}
