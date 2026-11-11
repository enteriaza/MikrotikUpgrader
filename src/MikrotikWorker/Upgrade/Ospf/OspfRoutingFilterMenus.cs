namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Version-specific RouterOS routing-filter menu paths and OSPF migration cleanup scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cleanup scope (approved OSPF migration policy: no routing filters, no filter dependence):</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <b>RouterOS v6 — removed:</b> all rows under <c>/routing/filter</c>
/// (legacy filter table).
/// </item>
/// <item>
/// <b>RouterOS v7 — removed:</b>
/// <list type="bullet">
/// <item><c>/routing/filter/rule</c> — primary filter rules (required menu; inventory/removal fail-closed).</item>
/// <item><c>/routing/filter/select-rule</c> — select-rule filters when the menu exists.
/// If the menu is positively absent (<c>no such command</c>), it is treated as unsupported and skipped.
/// Any other inventory/removal error fails closed.</item>
/// </list>
/// </item>
/// <item>
/// <b>Out of scope (not deleted):</b>
/// <c>/routing/filter/num-list</c> and <c>/routing/filter/community-list</c>.
/// These are supporting catalogue/list objects that may be shared with BGP or other routing features;
/// they do not themselves apply OSPF filtering. Orphaned lists after rule removal are left intact.
/// </item>
/// <item>
/// OSPFv2 <c>in-filter</c>/<c>out-filter</c> instance references remain cleared separately
/// via <see cref="IOspfMikrotikOperations.ClearOspfInstanceFilterReferencesAsync"/>.
/// </item>
/// </list>
/// <para>
/// Never interpret an unsupported/legacy command failure as successful empty cleanup on the
/// version-required menu path.
/// </para>
/// </remarks>
internal static class OspfRoutingFilterMenus
{
    public const string V6FilterPrint = "/routing/filter/print";
    public const string V6FilterRemove = "/routing/filter/remove";

    public const string V7FilterRulePrint = "/routing/filter/rule/print";
    public const string V7FilterRuleRemove = "/routing/filter/rule/remove";

    public const string V7FilterSelectRulePrint = "/routing/filter/select-rule/print";
    public const string V7FilterSelectRuleRemove = "/routing/filter/select-rule/remove";

    /// <summary>Supporting catalogues — never mutated by OSPF migration cleanup.</summary>
    public const string V7NumListPrint = "/routing/filter/num-list/print";

    /// <summary>Supporting catalogues — never mutated by OSPF migration cleanup.</summary>
    public const string V7CommunityListPrint = "/routing/filter/community-list/print";
}
