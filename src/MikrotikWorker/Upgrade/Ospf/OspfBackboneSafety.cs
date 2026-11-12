// -----------------------------------------------------------------------------
// File: OspfBackboneSafety.cs
//
// Description:
// Explicit OSPF role invariants and pre-mutation backbone / management-plane
// safety gates used before and after OSPF apply.
//
// -----------------------------------------------------------------------------

using System.Net;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Explicit OSPF role invariants and pre-mutation backbone / management-plane safety gates.
/// </summary>
internal enum OspfTemplateRole
{
    /// <summary>Designated active backhaul / backbone adjacency template.</summary>
    ActiveBackhaul = 0,

    /// <summary>Passive loopback template.</summary>
    PassiveLoopback = 1,

    /// <summary>Passive site or customer matcher template.</summary>
    PassiveSiteOrCustomer = 2,
}

/// <summary>
/// Pre-mutation validation that every planned active backhaul is explicitly active, /30, backbone,
/// isolated, and cannot be overridden by other plan entries.
/// </summary>
internal static class OspfBackboneSafety
{
    /// <summary>
    /// Validates plan backhaul/role invariants against current IP address inventory; returns error strings.
    /// </summary>
    public static IReadOnlyList<string> ValidatePlanInvariants(
        OspfMigrationPlan plan,
        IReadOnlyList<OspfIpAddressRow> addresses)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(addresses);

        List<string> errors = [];
        HashSet<string> backboneNames = plan.Areas
            .Where(static a => a.IsBackbone)
            .Select(static a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (backboneNames.Count == 0)
        {
            errors.Add("Plan has no backbone area; active backhaul cannot be validated.");
        }

        foreach (OspfPlanBackhaulTemplate bh in plan.ActiveBackhauls)
        {
            if (bh.Passive)
            {
                errors.Add(
                    $"Active backhaul '{bh.Interface}' {bh.NetworkCidr} has Passive=true; role ActiveBackhaul requires Passive=false.");
            }

            if (string.IsNullOrWhiteSpace(bh.Interface))
            {
                errors.Add($"Active backhaul {bh.NetworkCidr} is missing interface name.");
            }

            if (!OspfIpPrefix.TryParse(bh.NetworkCidr, out OspfIpPrefix net) || net.PrefixLength != 30)
            {
                errors.Add(
                    $"Active backhaul '{bh.Interface}' network '{bh.NetworkCidr}' must be an exact /30.");
            }

            if (bh.Cost != OspfMigrationPlanner.FleetBackhaulCost)
            {
                errors.Add(
                    $"Active backhaul '{bh.Interface}' cost is {bh.Cost}; required {OspfMigrationPlanner.FleetBackhaulCost}.");
            }

            if (!string.Equals(bh.Type, "ptp", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Active backhaul '{bh.Interface}' type must be ptp (observed '{bh.Type}').");
            }

            if (!backboneNames.Contains(bh.AreaName))
            {
                errors.Add(
                    $"Active backhaul '{bh.Interface}' area '{bh.AreaName}' is not the backbone area.");
            }

            List<OspfIpAddressRow> onIface = addresses
                .Where(a => string.Equals(a.Interface, bh.Interface, StringComparison.Ordinal))
                .ToList();
            if (onIface.Count == 0)
            {
                errors.Add(
                    $"Active backhaul '{bh.Interface}' has no local IPv4 address; cannot validate /30 association.");
            }
            else if (OspfIpPrefix.TryParse(bh.NetworkCidr, out OspfIpPrefix expected))
            {
                List<OspfIpAddressRow> matching = onIface
                    .Where(a => TryHostInPrefix(a, expected))
                    .ToList();
                if (matching.Count != 1)
                {
                    errors.Add(
                        $"Active backhaul '{bh.Interface}' {bh.NetworkCidr} does not map to exactly one local address (matches={matching.Count}).");
                }
            }

            // No other plan entry may target the same interface+network as passive.
            foreach (OspfPlanLoopbackTemplate loop in plan.PassiveLoopbacks)
            {
                if (string.Equals(loop.Interface, bh.Interface, StringComparison.Ordinal)
                    && string.Equals(Normalize(loop.NetworkCidr), Normalize(bh.NetworkCidr), StringComparison.Ordinal))
                {
                    errors.Add(
                        $"Loopback plan entry conflicts with active backhaul '{bh.Interface}' {bh.NetworkCidr}.");
                }
            }

            if (!OspfIpPrefix.TryParse(bh.NetworkCidr, out OspfIpPrefix bhNet))
            {
                continue;
            }

            foreach (OspfPlanSiteNetworkTemplate site in plan.PassiveSiteNetworks)
            {
                foreach (string siteCidr in OspfNetworkList.Split(site.NetworkCidr))
                {
                    if (!OspfIpPrefix.TryParse(siteCidr, out OspfIpPrefix siteNet))
                    {
                        continue;
                    }

                    // interfaces=all + a covering networks= matcher would activate the backhaul under OR matching.
                    if (siteNet.Equals(bhNet) || siteNet.Contains(bhNet))
                    {
                        errors.Add(
                            $"Site matcher '{siteCidr}' covers active backhaul network '{bh.NetworkCidr}'; fail-closed.");
                    }
                }
            }
        }

        foreach (OspfPlanLoopbackTemplate loop in plan.PassiveLoopbacks)
        {
            if (!loop.Passive)
            {
                errors.Add($"Loopback '{loop.NetworkCidr}' must be Passive=true.");
            }

            if (!OspfIpPrefix.TryParse(loop.NetworkCidr, out OspfIpPrefix p) || p.PrefixLength != 32)
            {
                errors.Add($"Loopback '{loop.NetworkCidr}' must be a /32.");
            }
        }

        foreach (OspfPlanSiteNetworkTemplate site in plan.PassiveSiteNetworks)
        {
            if (!site.Passive)
            {
                errors.Add($"Site matcher '{site.NetworkCidr}' must be Passive=true.");
            }

            if (string.IsNullOrWhiteSpace(site.Interfaces))
            {
                errors.Add(
                    $"Site matcher '{site.NetworkCidr}' in area '{site.AreaName}' is missing an interfaces matcher.");
            }

            if (OspfNetworkList.Split(site.NetworkCidr).Count == 0)
            {
                errors.Add($"Site matcher in area '{site.AreaName}' has an empty networks list.");
            }
        }

        foreach (IGrouping<string, OspfPlanSiteNetworkTemplate> siteGroup in plan.PassiveSiteNetworks
                     .GroupBy(static s => s.AreaName, StringComparer.OrdinalIgnoreCase))
        {
            List<OspfPlanSiteNetworkTemplate> duplicates = siteGroup
                .GroupBy(
                    static s => (
                        OspfSiteMatcherConsolidation.IsAllInterfaces(s.Interfaces)
                            ? "all"
                            : s.Interfaces?.Trim() ?? "",
                        OspfNetworkList.JoinCanonical(OspfNetworkList.Split(s.NetworkCidr))),
                    EqualityComparer<(string, string)>.Default)
                .Where(static g => g.Count() > 1)
                .SelectMany(static g => g)
                .ToList();
            if (duplicates.Count > 0)
            {
                errors.Add(
                    $"Area '{siteGroup.Key}' has duplicate planned site matcher identities; fail-closed.");
            }
        }

        errors.AddRange(OspfMigrationPlanner.ValidateBackhaulTemplateIsolation(plan.ActiveBackhauls, addresses));
        return errors;
    }

    /// <summary>
    /// True when the live template matches a planned designated active backhaul.
    /// </summary>
    public static bool IsDesignatedBackhaulTemplate(
        OspfInterfaceTemplateRow template,
        OspfPlanBackhaulTemplate backhaul) =>
        string.Equals(template.Area, backhaul.AreaName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(template.Interfaces, backhaul.Interface, StringComparison.Ordinal)
        && string.Equals(Normalize(template.Networks), Normalize(backhaul.NetworkCidr), StringComparison.Ordinal);

    /// <summary>
    /// True when the live template .id is one of the designated backhaul ids in the plan.
    /// </summary>
    public static bool IsDesignatedBackhaulId(
        string? id,
        IReadOnlyList<OspfInterfaceTemplateRow> templates,
        IReadOnlyList<OspfPlanBackhaulTemplate> backhauls)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        OspfInterfaceTemplateRow? row = templates.FirstOrDefault(t =>
            string.Equals(t.Id, id, StringComparison.Ordinal));
        if (row is null)
        {
            return false;
        }

        return backhauls.Any(b => IsDesignatedBackhaulTemplate(row, b));
    }

    /// <summary>
    /// Determines whether an interface address belongs to the expected subnet with the same prefix length.
    /// </summary>
    /// <param name="addr">Interface address row.</param>
    /// <param name="expected">Expected subnet.</param>
    /// <returns><see langword="true"/> when the address is in the subnet at the same prefix length.</returns>
    private static bool TryHostInPrefix(OspfIpAddressRow addr, OspfIpPrefix expected)
    {
        if (!OspfIpPrefix.TryParseHost(addr.Address, out uint host, out int len))
        {
            return false;
        }

        if (len == expected.PrefixLength
            && !string.IsNullOrWhiteSpace(addr.Network)
            && OspfIpPrefix.TryParse($"{addr.Network.Trim()}/{len}", out OspfIpPrefix subnet)
            && subnet.Equals(expected))
        {
            return true;
        }

        return expected.ContainsHost(host) && len == expected.PrefixLength;
    }

    /// <summary>
    /// Normalises a CIDR for comparison, falling back to the trimmed text when it cannot be parsed.
    /// </summary>
    /// <param name="cidr">CIDR text.</param>
    /// <returns>The normalised CIDR, or an empty string for null.</returns>
    private static string Normalize(string? cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p) ? p.ToCidrString() : cidr?.Trim() ?? string.Empty;
}

/// <summary>
/// Snapshot of management-relevant OSPF / routing state before backbone mutations.
/// Route fields are read-only RouterOS observations — they are not end-to-end connectivity proof.
/// </summary>
internal sealed class OspfManagementPlaneBaseline
{
    /// <summary>
    /// Management host address used for route observation.
    /// </summary>
    public required string ManagementHost { get; init; }
    /// <summary>
    /// Observed backbone/backhaul interface name when known.
    /// </summary>
    public required string? BackboneInterface { get; init; }
    /// <summary>
    /// Live .id of the designated backbone template when known.
    /// </summary>
    public required string? BackboneTemplateId { get; init; }
    /// <summary>
    /// Backbone area name observed/expected.
    /// </summary>
    public required string? BackboneArea { get; init; }
    /// <summary>
    /// Backbone/backhaul network prefix.
    /// </summary>
    public required string? BackboneNetwork { get; init; }
    /// <summary>
    /// True when the backbone template was active at baseline capture.
    /// </summary>
    public required bool BackboneWasActive { get; init; }
    /// <summary>
    /// True when the backbone template was passive (unsafe).
    /// </summary>
    public required bool BackboneTemplatePassive { get; init; }
    /// <summary>
    /// True when the backbone template was disabled.
    /// </summary>
    public required bool BackboneTemplateDisabled { get; init; }
    /// <summary>
    /// Observed OSPF neighbour address when known.
    /// </summary>
    public required string? NeighbourAddress { get; init; }
    /// <summary>
    /// Observed OSPF neighbour router-id when known.
    /// </summary>
    public required string? NeighbourRouterId { get; init; }
    /// <summary>
    /// Observed OSPF neighbour state string.
    /// </summary>
    public required string? NeighbourState { get; init; }
    /// <summary>
    /// True when a FULL neighbour was observed at baseline.
    /// </summary>
    public required bool NeighbourWasFull { get; init; }

    /// <summary>Selected <c>/ip/route</c> destination covering <see cref="ManagementHost"/>.</summary>
    public required string? ManagementRouteDst { get; init; }

    /// <summary>
    /// Gateway of the observed management route.
    /// </summary>
    public required string? ManagementRouteGateway { get; init; }
    /// <summary>
    /// Immediate gateway of the observed management route.
    /// </summary>
    public required string? ManagementRouteImmediateGw { get; init; }
    /// <summary>
    /// Outgoing interface of the observed management route.
    /// </summary>
    public required string? ManagementOutgoingInterface { get; init; }
    /// <summary>
    /// True when the observed management route is active.
    /// </summary>
    public required bool ManagementRouteActive { get; init; }
    /// <summary>
    /// True when a covering management route was observed.
    /// </summary>
    public required bool ManagementRouteObserved { get; init; }

    /// <summary>
    /// True when the selected management route's gateway/outgoing interface is tied to the
    /// designated backbone interface or Full neighbour address.
    /// </summary>
    public required bool ManagementRouteDependsOnBackbone { get; init; }

    /// <summary>
    /// Always false from the application: RouterOS route print cannot prove the management host
    /// can still reach this router. External probing is required for that guarantee.
    /// </summary>
    public bool EndToEndManagementConnectivityVerified => false;
}

/// <summary>
/// Thrown when a backbone/management safety gate refuses an OSPF mutation or verify.
/// </summary>
internal sealed class OspfBackboneMutationGuardException : InvalidOperationException
{
    /// <summary>
    /// Creates a backbone/management safety abort exception.
    /// </summary>
    public OspfBackboneMutationGuardException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a backbone/management safety abort exception.
    /// </summary>
    public OspfBackboneMutationGuardException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
