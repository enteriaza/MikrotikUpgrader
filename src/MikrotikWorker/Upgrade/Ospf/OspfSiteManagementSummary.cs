// -----------------------------------------------------------------------------
// File: OspfSiteManagementSummary.cs
//
// Description:
// Derives site management summary /24 prefixes from interface-assigned
// addresses inside 10.0.0.0/8, excluding Loopback /32, designated backhaul
// /30s, all /30 transport addresses, disabled addresses, and PPPoE roles.
// Multiple /24s on the same established *_MGMT interface are accepted as
// intentional management expansion (never combined into a broader supernet).
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Derives site management summary <c>/24</c> prefixes from interface-assigned addresses
/// inside <c>10.0.0.0/8</c>, excluding Loopback <c>/32</c>, designated backhaul <c>/30</c>s,
/// all point-to-point <c>/30</c> addresses, disabled address rows, and PPPoE roles.
/// </summary>
internal static class OspfSiteManagementSummary
{
    /// <summary>
    /// Infrastructure address space <c>10.0.0.0/8</c>; only addresses inside it can form the management summary.
    /// </summary>
    private static readonly OspfIpPrefix TenDotZero = ParseRequired("10.0.0.0/8");

    /// <summary>
    /// Derives the site management <c>/24</c> prefix set from interface addresses.
    /// </summary>
    /// <remarks>
    /// Fleet rules:
    /// <list type="bullet">
    /// <item>Never derive management candidates from <c>/30</c> addresses.</item>
    /// <item>Exclude disabled IP address entries entirely.</item>
    /// <item><c>*_MGMT</c> interface names are strong management evidence.</item>
    /// <item>Subdivided management <c>/26</c>/<c>/27</c> addresses collapse into their common <c>/24</c>.</item>
    /// <item>
    /// Distinct <c>/24</c>s on the same established management interface are accepted as intentional
    /// expansion and must not be rejected as ambiguous or merged into a broader supernet.
    /// </item>
    /// </list>
    /// </remarks>
    public static OspfSiteManagementSummaryResult Derive(
        IReadOnlyList<OspfIpAddressRow> ipAddresses,
        OspfLoopbackRouterIdResult loopback,
        IReadOnlyList<OspfPlanBackhaulTemplate> backhauls,
        IReadOnlyList<string>? corroboratingManagementCidrs = null)
    {
        ArgumentNullException.ThrowIfNull(ipAddresses);
        ArgumentNullException.ThrowIfNull(loopback);
        ArgumentNullException.ThrowIfNull(backhauls);

        if (!loopback.Succeeded
            || string.IsNullOrWhiteSpace(loopback.RouterId)
            || string.IsNullOrWhiteSpace(loopback.LoopbackInterfaceName))
        {
            return Failed(
                loopback.Errors.Count > 0
                    ? loopback.Errors
                    : ["Loopback must be resolved before deriving the site management /24."]);
        }

        List<OspfIpPrefix> backhaulNets = [];
        foreach (OspfPlanBackhaulTemplate bh in backhauls)
        {
            if (!OspfIpPrefix.TryParse(bh.NetworkCidr, out OspfIpPrefix net) || net.PrefixLength != 30)
            {
                return Failed(
                    $"Backhaul '{bh.Interface}' network '{bh.NetworkCidr}' is not a usable /30 for management /24 exclusion.");
            }

            backhaulNets.Add(net);
        }

        Dictionary<string, List<string>> contributorsByCidr = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> interfacesByCidr = new(StringComparer.Ordinal);
        List<string> ignoredOutside = [];
        List<string> ignoredPppoe = [];
        List<string> ignoredDisabled = [];
        List<string> ignoredTransport30 = [];
        List<string> diagnostics = [];

        // Decision path uses only eligible (enabled) addresses. Disabled/unknown rows are
        // diagnostic-only and must never enter contributorsByCidr.
        foreach (OspfIpAddressRow row in ipAddresses)
        {
            if (string.IsNullOrWhiteSpace(row.Interface) || string.IsNullOrWhiteSpace(row.Address))
            {
                continue;
            }

            if (!OspfInventoryEligibility.IsEligible(row))
            {
                string excluded = OspfInventoryEligibility.FormatExcludedAddress(row);
                ignoredDisabled.Add(excluded);
                diagnostics.Add(
                    row.AdminState == OspfAdminState.Unknown
                        ? $"Excluded unknown-disabled-state: {excluded}"
                        : $"Excluded disabled: {excluded}");
                continue;
            }

            if (OspfLoopbackRouterId.IsSupportedLoopbackInterfaceName(row.Interface))
            {
                diagnostics.Add($"excluded loopback {row.Interface}={row.Address}");
                continue;
            }

            if (IsPppoeRoleInterface(row.Interface))
            {
                ignoredPppoe.Add($"{row.Interface}={row.Address}");
                diagnostics.Add($"excluded pppoe {row.Interface}={row.Address}");
                continue;
            }

            if (!TryParseAssignedHost(row, out uint host, out int? prefixLength, out string? parseError))
            {
                return Failed(
                    parseError
                    ?? $"Interface '{row.Interface}' address '{row.Address}' cannot be classified for management /24 derivation.");
            }

            // Mandatory: never promote a /30 into a management /24 candidate.
            if (prefixLength == 30)
            {
                ignoredTransport30.Add($"{row.Interface}={row.Address}");
                diagnostics.Add($"excluded /30 transport {row.Interface}={row.Address}");
                continue;
            }

            if (!TenDotZero.ContainsHost(host))
            {
                ignoredOutside.Add($"{row.Interface}={row.Address}");
                diagnostics.Add($"excluded outside-10/8 {row.Interface}={row.Address}");
                continue;
            }

            if (backhaulNets.Any(n => n.ContainsHost(host)))
            {
                diagnostics.Add($"excluded planned-backhaul {row.Interface}={row.Address}");
                continue;
            }

            uint masked = host & (uint.MaxValue << 8); // /24
            string cidr = FormatHost(masked) + "/24";
            if (!contributorsByCidr.TryGetValue(cidr, out List<string>? list))
            {
                list = [];
                contributorsByCidr[cidr] = list;
            }

            if (!interfacesByCidr.TryGetValue(cidr, out HashSet<string>? ifaces))
            {
                ifaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                interfacesByCidr[cidr] = ifaces;
            }

            ifaces.Add(row.Interface.Trim());
            string contributor = $"{row.Interface}={row.Address}->{cidr}";
            list.Add(contributor);
            diagnostics.Add(
                IsMgmtInterfaceName(row.Interface)
                    ? $"candidate MGMT {contributor}"
                    : $"candidate {contributor}");
        }

        if (contributorsByCidr.Count == 0)
        {
            return Failed(
                [
                    "No eligible site management /24 could be derived from interface-assigned addresses "
                    + "in 10.0.0.0/8 after excluding Loopback, disabled, /30 transport, backhaul, and PPPoE role interfaces. "
                    + "Diagnostics: "
                    + string.Join("; ", diagnostics),
                ],
                ignoredDisabled,
                diagnostics);
        }

        if (contributorsByCidr.Count == 1)
        {
            string summary = contributorsByCidr.Keys.Single();
            diagnostics.Add($"selected existing management aggregate {summary}");
            return Succeeded(
                [summary],
                contributorsByCidr[summary],
                ignoredOutside,
                ignoredPppoe,
                ignoredDisabled,
                ignoredTransport30,
                diagnostics);
        }

        // Same-interface expansion only: distinct /24s co-resident on one established *_MGMT
        // interface form a domain. Shared bridge prefixes must not transitively merge unrelated
        // interface-local expansions — multiple inclusion-maximal iface sets fail closed.
        List<HashSet<string>> mgmtComponents = SelectDirectSameInterfaceDomains(interfacesByCidr);
        HashSet<string> mgmtBacked = new(
            mgmtComponents.SelectMany(static c => c),
            StringComparer.Ordinal);
        List<string> nonMgmtOnly = contributorsByCidr.Keys
            .Where(c => !mgmtBacked.Contains(c))
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        if (mgmtComponents.Count == 1)
        {
            HashSet<string> accepted = mgmtComponents[0];
            foreach (string dropped in nonMgmtOnly)
            {
                diagnostics.Add(
                    $"excluded non-MGMT candidate {dropped} after established MGMT-interface domain selection");
            }

            NoteAcceptedExpansion(diagnostics, accepted, contributorsByCidr, interfacesByCidr);
            return Succeeded(
                accepted.OrderBy(static s => s, StringComparer.Ordinal).ToList(),
                accepted.SelectMany(c => contributorsByCidr[c]).ToList(),
                ignoredOutside,
                ignoredPppoe,
                ignoredDisabled,
                ignoredTransport30,
                diagnostics);
        }

        if (mgmtComponents.Count > 1)
        {
            // Prefer uniquely corroborated MGMT evidence over equal-weight disconnected domains.
            HashSet<string> hints = new(
                (corroboratingManagementCidrs ?? [])
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Select(static s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);

            List<HashSet<string>> corroboratedComponents = mgmtComponents
                .Where(component => component.Count(c => hints.Contains(c)) > 0)
                .ToList();
            if (corroboratedComponents.Count == 1)
            {
                HashSet<string> accepted = corroboratedComponents[0];
                NoteAcceptedExpansion(diagnostics, accepted, contributorsByCidr, interfacesByCidr);
                diagnostics.Add(
                    $"selected MGMT domain via area-range/network corroboration: "
                    + string.Join(", ", accepted.OrderBy(static s => s, StringComparer.Ordinal)));
                return Succeeded(
                    accepted.OrderBy(static s => s, StringComparer.Ordinal).ToList(),
                    accepted.SelectMany(c => contributorsByCidr[c]).ToList(),
                    ignoredOutside,
                    ignoredPppoe,
                    ignoredDisabled,
                    ignoredTransport30,
                    diagnostics);
            }

            return Failed(
                [
                    "Multiple distinct eligible site management /24 domains were derived from unrelated "
                    + "management interfaces ("
                    + string.Join(
                        " | ",
                        mgmtComponents.Select(c =>
                            string.Join(", ", c.OrderBy(static s => s, StringComparer.Ordinal))))
                    + "); refusing to select without unambiguous corroborating MGMT evidence. Contributors: "
                    + string.Join(
                        "; ",
                        contributorsByCidr.SelectMany(static kv => kv.Value))
                    + ". Diagnostics: "
                    + string.Join("; ", diagnostics),
                ],
                ignoredDisabled,
                diagnostics);
        }

        // No *_MGMT interfaces among candidates — retain prior corroboration / fail-closed behaviour.
        HashSet<string> nonMgmtHints = new(
            (corroboratingManagementCidrs ?? [])
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim()),
            StringComparer.OrdinalIgnoreCase);

        List<string> corroborated = contributorsByCidr.Keys
            .Where(c => nonMgmtHints.Contains(c))
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();
        if (corroborated.Count == 1)
        {
            string chosen = corroborated[0];
            diagnostics.Add($"selected {chosen} via MGMT area-range/network corroboration");
            return Succeeded(
                [chosen],
                contributorsByCidr[chosen],
                ignoredOutside,
                ignoredPppoe,
                ignoredDisabled,
                ignoredTransport30,
                diagnostics);
        }

        return Failed(
            [
                "Multiple distinct eligible site management /24 prefixes were derived ("
                + string.Join(", ", contributorsByCidr.Keys.OrderBy(static s => s, StringComparer.Ordinal))
                + "); refusing to select without unambiguous corroborating MGMT evidence. Contributors: "
                + string.Join(
                    "; ",
                    contributorsByCidr.SelectMany(static kv => kv.Value))
                + ". Diagnostics: "
                + string.Join("; ", diagnostics),
            ],
            ignoredDisabled,
            diagnostics);
    }

    /// <summary>
    /// Collect MGMT-commented area ranges and explicit management-style site network /24s as hints.
    /// </summary>
    public static IReadOnlyList<string> CollectCorroboratingManagementCidrs(
        IReadOnlyList<OspfPlanAreaRange> ranges,
        IReadOnlyList<OspfNetworkRow>? sourceNetworks = null)
    {
        HashSet<string> hints = new(StringComparer.OrdinalIgnoreCase);
        foreach (OspfPlanAreaRange range in ranges)
        {
            if (!string.Equals(range.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (OspfIpPrefix.TryParse(range.RangeCidr, out OspfIpPrefix prefix) && prefix.PrefixLength == 24)
            {
                hints.Add(prefix.ToCidrString());
            }
        }

        if (sourceNetworks is not null)
        {
            foreach (OspfNetworkRow network in sourceNetworks)
            {
                bool mgmtComment =
                    string.Equals(network.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase)
                    || (network.Comment?.Contains("MGMT", StringComparison.OrdinalIgnoreCase) ?? false)
                    || string.IsNullOrWhiteSpace(network.Comment);

                if (!mgmtComment)
                {
                    continue;
                }

                if (OspfIpPrefix.TryParse(network.Network, out OspfIpPrefix net)
                    && net.PrefixLength == 24
                    && TenDotZero.ContainsHost(net.NetworkAddress))
                {
                    hints.Add(net.ToCidrString());
                }
            }
        }

        return hints.OrderBy(static s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Determines whether an interface name denotes a PPPoE role interface, whose addresses never contribute to management.
    /// </summary>
    internal static bool IsPppoeRoleInterface(string interfaceName)
    {
        string n = interfaceName.Trim();
        return n.Contains("pppoe", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("<pppoe-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the interface name ends with <c>_MGMT</c> or contains <c>MGMT</c> as a VLAN role marker.</summary>
    internal static bool IsMgmtInterfaceName(string interfaceName)
    {
        string n = interfaceName.Trim();
        return n.EndsWith("_MGMT", StringComparison.OrdinalIgnoreCase)
               || n.Contains("_MGMT", StringComparison.OrdinalIgnoreCase)
               || n.EndsWith(".MGMT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Selects management domains from direct same-interface evidence only.
    /// </summary>
    /// <remarks>
    /// Each established <c>*_MGMT</c> interface contributes the set of eligible <c>/24</c>s observed on
    /// that interface. Domains are the inclusion-maximal among those per-interface sets.
    /// A unique maximal set is accepted (Cashane-style expansion; subset-only interfaces are
    /// corroborating evidence, not bridges). Multiple distinct maximals remain ambiguous —
    /// shared bridge prefixes must not transitively merge unrelated expansions.
    /// </remarks>
    private static List<HashSet<string>> SelectDirectSameInterfaceDomains(
        IReadOnlyDictionary<string, HashSet<string>> interfacesByCidr)
    {
        Dictionary<string, HashSet<string>> cidrsByMgmtIface = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string cidr, HashSet<string> ifaces) in interfacesByCidr)
        {
            foreach (string iface in ifaces.Where(IsMgmtInterfaceName))
            {
                if (!cidrsByMgmtIface.TryGetValue(iface, out HashSet<string>? set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    cidrsByMgmtIface[iface] = set;
                }

                set.Add(cidr);
            }
        }

        if (cidrsByMgmtIface.Count == 0)
        {
            return [];
        }

        // Deterministic snapshot of per-interface sets (ordered by interface name).
        List<HashSet<string>> ifaceSets = cidrsByMgmtIface
            .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static kv => new HashSet<string>(kv.Value, StringComparer.Ordinal))
            .ToList();

        List<HashSet<string>> maximals = [];
        foreach (HashSet<string> candidate in ifaceSets.OrderByDescending(static s => s.Count)
                     .ThenBy(static s => string.Join('\u001f', s.OrderBy(static x => x, StringComparer.Ordinal)), StringComparer.Ordinal))
        {
            bool properSubsetOfExisting = maximals.Any(m =>
                candidate.IsProperSubsetOf(m));
            if (properSubsetOfExisting)
            {
                continue;
            }

            // Drop any previously recorded maximal that this candidate properly contains.
            maximals.RemoveAll(m => m.IsProperSubsetOf(candidate));

            bool alreadyPresent = maximals.Any(m => m.SetEquals(candidate));
            if (!alreadyPresent)
            {
                maximals.Add(new HashSet<string>(candidate, StringComparer.Ordinal));
            }
        }

        // Stable order of domains for diagnostics / corroboration selection.
        return maximals
            .OrderBy(static m => string.Join(',', m.OrderBy(static s => s, StringComparer.Ordinal)), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Records diagnostic lines for the selected primary management aggregate and any additional
    /// same-interface <c>/24</c> expansions that were accepted without merging into a supernet.
    /// </summary>
    /// <param name="diagnostics">Mutable selection trail.</param>
    /// <param name="accepted">Distinct accepted management <c>/24</c> CIDRs.</param>
    /// <param name="contributorsByCidr">Address rows that contributed to each CIDR (for primary choice).</param>
    /// <param name="interfacesByCidr">Interfaces observed for each CIDR (for expansion evidence text).</param>
    private static void NoteAcceptedExpansion(
        List<string> diagnostics,
        HashSet<string> accepted,
        IReadOnlyDictionary<string, List<string>> contributorsByCidr,
        IReadOnlyDictionary<string, HashSet<string>> interfacesByCidr)
    {
        List<string> ordered = accepted.OrderBy(static s => s, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        string primary = ChoosePrimary(ordered, contributorsByCidr);
        diagnostics.Add($"selected existing management aggregate {primary}");

        foreach (string cidr in ordered.Where(c => !string.Equals(c, primary, StringComparison.Ordinal)))
        {
            string ifaceEvidence = string.Join(
                ", ",
                interfacesByCidr[cidr]
                    .Where(IsMgmtInterfaceName)
                    .OrderBy(static s => s, StringComparer.OrdinalIgnoreCase));
            diagnostics.Add(
                $"accepted additional management expansion {cidr} on established management interface(s) "
                + $"[{ifaceEvidence}]");
        }
    }

    /// <summary>
    /// Chooses the primary management aggregate: most contributing addresses, then ordinal CIDR order.
    /// </summary>
    /// <param name="orderedCidrs">Accepted management CIDRs in stable order.</param>
    /// <param name="contributorsByCidr">Contributor lists used as the primary ranking key.</param>
    /// <returns>The CIDR selected as <see cref="OspfSiteManagementSummaryResult.ManagementCidr"/>.</returns>
    private static string ChoosePrimary(
        IReadOnlyList<string> orderedCidrs,
        IReadOnlyDictionary<string, List<string>> contributorsByCidr)
    {
        // Prefer the aggregate with the most address contributors (usually the subdivided base).
        return orderedCidrs
            .OrderByDescending(c =>
                contributorsByCidr.TryGetValue(c, out List<string>? rows) ? rows.Count : 0)
            .ThenBy(static c => c, StringComparer.Ordinal)
            .First();
    }

    /// <summary>
    /// Parses an inventory address row into a host integer and optional prefix length.
    /// </summary>
    /// <param name="row">Enabled <c>/ip/address</c> inventory row.</param>
    /// <param name="host">Host IPv4 as a big-endian 32-bit value on success.</param>
    /// <param name="prefixLength">Prefix length when the row includes <c>/n</c>; otherwise <see langword="null"/>.</param>
    /// <param name="error">Human-readable parse failure when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the address is a valid IPv4 host assignment.</returns>
    private static bool TryParseAssignedHost(
        OspfIpAddressRow row,
        out uint host,
        out int? prefixLength,
        out string? error)
    {
        host = 0;
        prefixLength = null;
        error = null;
        string trimmed = row.Address!.Trim();
        int slash = trimmed.IndexOf('/');
        string addrPart = slash < 0 ? trimmed : trimmed[..slash];
        if (slash >= 0)
        {
            if (!int.TryParse(trimmed[(slash + 1)..], out int len) || len is < 0 or > 32)
            {
                error = $"Interface '{row.Interface}' address '{row.Address}' has a malformed prefix.";
                return false;
            }

            prefixLength = len;
        }

        if (!IPAddress.TryParse(addrPart, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            error = $"Interface '{row.Interface}' address '{row.Address}' is not a valid IPv4 address.";
            return false;
        }

        byte[] b = ip.GetAddressBytes();
        host = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        return true;
    }

    /// <summary>Formats a big-endian host integer as a dotted IPv4 string for diagnostics.</summary>
    /// <param name="host">Host address packed as used by containment checks.</param>
    /// <returns>Dotted-quad representation.</returns>
    private static string FormatHost(uint host)
    {
        byte[] bytes =
        [
            (byte)(host >> 24),
            (byte)(host >> 16),
            (byte)(host >> 8),
            (byte)host,
        ];
        return new IPAddress(bytes).ToString();
    }

    /// <summary>Parses a hard-coded CIDR constant or throws if the constant is malformed.</summary>
    /// <param name="cidr">CIDR text (for example <c>10.0.0.0/8</c>).</param>
    /// <returns>Parsed prefix.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="cidr"/> cannot be parsed.</exception>
    private static OspfIpPrefix ParseRequired(string cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p)
            ? p
            : throw new InvalidOperationException(cidr);

    /// <summary>
    /// Builds a successful management-summary result with ordered CIDRs, exclusion evidence, and diagnostics.
    /// </summary>
    /// <param name="cidrs">Accepted management <c>/24</c> aggregates.</param>
    /// <param name="contributing">Address rows that contributed to discovery.</param>
    /// <param name="ignoredOutside">Hosts outside the intentional <c>10.0.0.0/8</c> management scope.</param>
    /// <param name="ignoredPppoe">PPPoE-style addresses excluded from management candidates.</param>
    /// <param name="ignoredDisabled">Disabled address rows retained only for diagnostics.</param>
    /// <param name="ignoredTransport30">Exact <c>/30</c> transport addresses excluded from management.</param>
    /// <param name="diagnostics">Selection trail (primary + expansions).</param>
    /// <returns>Succeeded result with primary CIDR chosen from diagnostics when present.</returns>
    private static OspfSiteManagementSummaryResult Succeeded(
        IReadOnlyList<string> cidrs,
        IReadOnlyList<string> contributing,
        IReadOnlyList<string> ignoredOutside,
        IReadOnlyList<string> ignoredPppoe,
        IReadOnlyList<string> ignoredDisabled,
        IReadOnlyList<string> ignoredTransport30,
        IReadOnlyList<string> diagnostics)
    {
        List<string> ordered = cidrs
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Select(static c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToList();

        string? primary = ordered.Count == 0
            ? null
            : ordered[0];

        // When expansion is present, prefer the aggregate that appeared first in diagnostics as primary
        // if ChoosePrimary already recorded it; otherwise keep ordinal first for stable single-prefix cases.
        string? selectedPrimary = diagnostics
            .Select(static d =>
            {
                const string marker = "selected existing management aggregate ";
                return d.StartsWith(marker, StringComparison.Ordinal)
                    ? d[marker.Length..].Trim()
                    : null;
            })
            .FirstOrDefault(static s => !string.IsNullOrWhiteSpace(s));
        if (!string.IsNullOrWhiteSpace(selectedPrimary) && ordered.Contains(selectedPrimary, StringComparer.Ordinal))
        {
            primary = selectedPrimary;
        }

        return new()
        {
            Succeeded = true,
            ManagementCidr = primary,
            ManagementCidrs = ordered,
            ContributingAddresses = contributing.ToList(),
            IgnoredOutsideRangeAddresses = ignoredOutside.ToList(),
            IgnoredPppoeAddresses = ignoredPppoe.ToList(),
            IgnoredDisabledAddresses = ignoredDisabled.ToList(),
            IgnoredTransportSlash30Addresses = ignoredTransport30.ToList(),
            SelectionDiagnostics = diagnostics.ToList(),
        };
    }

    /// <summary>Builds a failed result from a single error string.</summary>
    /// <param name="error">Fail-closed reason.</param>
    /// <returns>Failed summary with empty optional exclusion lists.</returns>
    private static OspfSiteManagementSummaryResult Failed(string error) => Failed([error]);

    /// <summary>
    /// Builds a failed result while optionally preserving disabled-address and diagnostic evidence
    /// so failure logs remain consistent with what was considered.
    /// </summary>
    /// <param name="errors">One or more fail-closed reasons.</param>
    /// <param name="ignoredDisabled">Disabled rows observed before failure, when available.</param>
    /// <param name="diagnostics">Partial selection trail, when available.</param>
    /// <returns>Failed summary (<see cref="OspfSiteManagementSummaryResult.Succeeded"/> is <see langword="false"/>).</returns>
    private static OspfSiteManagementSummaryResult Failed(
        IReadOnlyList<string> errors,
        IReadOnlyList<string>? ignoredDisabled = null,
        IReadOnlyList<string>? diagnostics = null) =>
        new()
        {
            Succeeded = false,
            Errors = errors.ToList(),
            // Preserve exclusion evidence on failure so diagnostics never contradict decision paths.
            IgnoredDisabledAddresses = ignoredDisabled?.ToList() ?? [],
            SelectionDiagnostics = diagnostics?.ToList() ?? [],
        };
}

/// <summary>
/// Derived site management summary /24 set and supporting details.
/// </summary>
internal sealed class OspfSiteManagementSummaryResult
{
    /// <summary>Whether management discovery produced a usable required-prefix set.</summary>
    /// <value><see langword="true"/> when <see cref="ManagementCidrs"/> may be used for planning.</value>
    public required bool Succeeded { get; init; }

    /// <summary>Primary management aggregate (stable single-value view for callers).</summary>
    public string? ManagementCidr { get; init; }

    /// <summary>
    /// Full set of required management <c>/24</c> aggregates, including same-interface expansions.
    /// Never a broader supernet of distinct <c>/24</c>s.
    /// </summary>
    public IReadOnlyList<string> ManagementCidrs { get; init; } = [];

    /// <summary>Address rows that contributed to accepted management aggregates.</summary>
    public IReadOnlyList<string> ContributingAddresses { get; init; } = [];

    /// <summary>Hosts ignored because they fall outside the intentional <c>10.0.0.0/8</c> management scope.</summary>
    public IReadOnlyList<string> IgnoredOutsideRangeAddresses { get; init; } = [];

    /// <summary>PPPoE or similar customer-facing addresses excluded from management discovery.</summary>
    public IReadOnlyList<string> IgnoredPppoeAddresses { get; init; } = [];

    /// <summary>Disabled address rows excluded from discovery.</summary>
    public IReadOnlyList<string> IgnoredDisabledAddresses { get; init; } = [];

    /// <summary>Point-to-point <c>/30</c> addresses excluded from management candidates.</summary>
    public IReadOnlyList<string> IgnoredTransportSlash30Addresses { get; init; } = [];

    /// <summary>Explainable include/exclude/select trail for diagnostics and logs.</summary>
    public IReadOnlyList<string> SelectionDiagnostics { get; init; } = [];

    /// <summary>Fail-closed reasons when <see cref="Succeeded"/> is <see langword="false"/>.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Joined <see cref="Errors"/> for logging; <see langword="null"/> on success.</summary>
    public string? Detail => Succeeded ? null : string.Join("; ", Errors);
}
