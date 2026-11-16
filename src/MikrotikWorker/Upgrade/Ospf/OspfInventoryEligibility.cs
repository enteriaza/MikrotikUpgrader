// -----------------------------------------------------------------------------
// File: OspfInventoryEligibility.cs
//
// Description:
// Global OSPF migration policy: only demonstrably enabled RouterOS objects may
// enter decision-making collections. Raw inventory may retain disabled rows for
// diagnostics; eligible views exclude them.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Administrative enablement state captured from RouterOS <c>disabled</c> (or equivalent).
/// </summary>
internal enum OspfAdminState
{
    /// <summary>Explicitly enabled, or RouterOS omitted <c>disabled</c> (enabled default).</summary>
    Enabled = 0,

    /// <summary>Explicitly disabled.</summary>
    Disabled = 1,

    /// <summary>Present but unrecognised/malformed; never treated as enabled.</summary>
    Unknown = 2,
}

/// <summary>
/// Shared eligibility boundary for OSPF migration decisions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mandatory rule:</b> disabled RouterOS configuration objects are excluded from all OSPF
/// migration decisions, inference, validation, reconciliation and corroborating evidence.
/// </para>
/// <para>
/// Raw inventory may retain disabled objects for audit diagnostics. All planners, resolvers and
/// validators must consume <see cref="Eligible"/> views (or equivalent predicates), never raw
/// collections that still contain disabled rows.
/// </para>
/// </remarks>
internal static class OspfInventoryEligibility
{
    /// <summary>
    /// Parses a RouterOS disabled-property value into an admin state.
    /// </summary>
    /// <remarks>
    /// RouterOS commonly omits the property when enabled (<see cref="OspfAdminState.Enabled"/>).
    /// Empty string means flag-present (disabled). Unrecognised non-empty values are
    /// <see cref="OspfAdminState.Unknown"/> and must fail closed.
    /// </remarks>
    public static OspfAdminState ParseAdminState(string? rawDisabled)
    {
        if (rawDisabled is null)
        {
            // Property omitted → RouterOS enabled default.
            return OspfAdminState.Enabled;
        }

        if (rawDisabled.Length == 0)
        {
            // Flag-style presence.
            return OspfAdminState.Disabled;
        }

        if (rawDisabled.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || rawDisabled.Equals("true", StringComparison.OrdinalIgnoreCase)
            || rawDisabled.Equals("1", StringComparison.Ordinal))
        {
            return OspfAdminState.Disabled;
        }

        if (rawDisabled.Equals("no", StringComparison.OrdinalIgnoreCase)
            || rawDisabled.Equals("false", StringComparison.OrdinalIgnoreCase)
            || rawDisabled.Equals("0", StringComparison.Ordinal))
        {
            return OspfAdminState.Enabled;
        }

        return OspfAdminState.Unknown;
    }

    /// <summary>True only for demonstrably enabled objects.</summary>
    public static bool IsEligible(OspfAdminState state) => state == OspfAdminState.Enabled;

    /// <summary>True when the object is explicitly disabled.</summary>
    public static bool IsExplicitlyDisabled(OspfAdminState state) => state == OspfAdminState.Disabled;

    /// <summary>
    /// Row is eligible only when admin state is enabled and the legacy <c>Disabled</c> flag is false.
    /// Unknown admin state never participates.
    /// </summary>
    private static bool IsEligible(OspfAdminState adminState, bool disabledFlag) =>
        adminState == OspfAdminState.Enabled && !disabledFlag;

    /// <summary>IP address eligibility.</summary>
    public static bool IsEligible(OspfIpAddressRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsEligible(row.AdminState, row.Disabled);
    }

    /// <summary>Interface-template eligibility.</summary>
    public static bool IsEligible(OspfInterfaceTemplateRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsEligible(row.AdminState, row.Disabled);
    }

    /// <summary>OSPF instance eligibility for decision paths (selection, RID corroboration, etc.).</summary>
    public static bool IsEligible(OspfInstanceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsEligible(row.AdminState, row.Disabled);
    }

    /// <summary>OSPF area eligibility.</summary>
    public static bool IsEligible(OspfAreaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsEligible(row.AdminState, row.Disabled);
    }

    /// <summary>OSPF area-range eligibility.</summary>
    public static bool IsEligible(OspfAreaRangeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsEligible(row.AdminState, row.Disabled);
    }

    /// <summary>v6 OSPF network eligibility.</summary>
    public static bool IsEligible(OspfNetworkRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsEligible(row.AdminState, row.Disabled);
    }

    /// <summary>v6 OSPF interface eligibility.</summary>
    public static bool IsEligible(OspfInterfaceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsEligible(row.AdminState, row.Disabled);
    }

    /// <summary>Routing-filter eligibility.</summary>
    public static bool IsEligible(OspfRoutingFilterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsEligible(row.AdminState, row.Disabled);
    }

    /// <summary>Live template-inventory eligibility (cleanup / post-apply).</summary>
    public static bool IsEligible(OspfTemplateInventoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return IsEligible(row.AdminState, row.Disabled);
    }

    /// <summary>Eligible IP addresses for policy decisions.</summary>
    public static IReadOnlyList<OspfIpAddressRow> Eligible(IEnumerable<OspfIpAddressRow> rows) =>
        rows.Where(IsEligible).ToList();

    /// <summary>Eligible interface templates.</summary>
    public static IReadOnlyList<OspfInterfaceTemplateRow> Eligible(IEnumerable<OspfInterfaceTemplateRow> rows) =>
        rows.Where(IsEligible).ToList();

    /// <summary>Eligible OSPF instances.</summary>
    public static IReadOnlyList<OspfInstanceRow> Eligible(IEnumerable<OspfInstanceRow> rows) =>
        rows.Where(IsEligible).ToList();

    /// <summary>Eligible OSPF areas.</summary>
    public static IReadOnlyList<OspfAreaRow> Eligible(IEnumerable<OspfAreaRow> rows) =>
        rows.Where(IsEligible).ToList();

    /// <summary>Eligible OSPF area ranges.</summary>
    public static IReadOnlyList<OspfAreaRangeRow> Eligible(IEnumerable<OspfAreaRangeRow> rows) =>
        rows.Where(IsEligible).ToList();

    /// <summary>Eligible v6 networks.</summary>
    public static IReadOnlyList<OspfNetworkRow> Eligible(IEnumerable<OspfNetworkRow> rows) =>
        rows.Where(IsEligible).ToList();

    /// <summary>Eligible v6 OSPF interfaces.</summary>
    public static IReadOnlyList<OspfInterfaceRow> Eligible(IEnumerable<OspfInterfaceRow> rows) =>
        rows.Where(IsEligible).ToList();

    /// <summary>Eligible routing filters.</summary>
    public static IReadOnlyList<OspfRoutingFilterRow> Eligible(IEnumerable<OspfRoutingFilterRow> rows) =>
        rows.Where(IsEligible).ToList();

    /// <summary>
    /// Applies parsed admin state onto a row's <c>Disabled</c>/<c>AdminState</c> pair used by capture mappers.
    /// </summary>
    public static (bool Disabled, OspfAdminState AdminState) FromRaw(string? rawDisabled)
    {
        OspfAdminState state = ParseAdminState(rawDisabled);
        return (state == OspfAdminState.Disabled, state);
    }

    /// <summary>
    /// Diagnostic label for an excluded disabled (or unknown) IP address.
    /// </summary>
    public static string FormatExcludedAddress(OspfIpAddressRow row) =>
        $"{row.Interface}={row.Address}";
}
