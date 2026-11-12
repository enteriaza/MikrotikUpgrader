namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Resolves deterministic ROS7 target identities onto the live RouterOS 7 inventory.
/// </summary>
/// <remarks>
/// Expected live objects are derived from fleet naming conventions — never by blind string
/// rewrite or “only available candidate” selection:
/// instance <c>default-v2</c>, backbone <c>backbone-v2</c>/<c>0.0.0.0</c>,
/// site <c>{Identity}-v2</c>/<c>{Loopback}</c>, Router ID = Loopback <c>/32</c>.
/// Live <c>.id</c> values are ephemeral for the current inventory snapshot only.
/// </remarks>
internal static class OspfLiveIdentityResolver
{
    public static OspfLiveIdentityResolutionResult Resolve(
        OspfMigrationPlan plan,
        OspfV7Inventory inventory,
        IReadOnlyList<OspfIpAddressRow> ipAddresses)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(ipAddresses);

        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(plan.Identity))
        {
            return Failed("Plan router identity is missing; cannot derive deterministic site area name.");
        }

        // Use the immutable plan Router ID to disambiguate multiple in-scope Loopback addresses.
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(ipAddresses, plan.RouterId);
        if (!loopback.Succeeded || string.IsNullOrWhiteSpace(loopback.RouterId))
        {
            return Failed(loopback.Errors.Count > 0
                ? loopback.Errors
                : ["Loopback Router ID derivation failed."]);
        }

        string derivedRid = loopback.RouterId!;
        if (!OspfLoopbackRouterId.EqualsRouterId(plan.RouterId, derivedRid))
        {
            return Failed(
                $"Plan router-id '{plan.RouterId}' does not match Loopback /32-derived Router ID '{derivedRid}'.");
        }

        if (!string.IsNullOrWhiteSpace(plan.LoopbackCidr)
            && !string.Equals(plan.LoopbackCidr, loopback.LoopbackCidr, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(
                $"Plan Loopback CIDR '{plan.LoopbackCidr}' does not match live Loopback '{loopback.LoopbackCidr}'.");
        }

        string expectedInstance = string.IsNullOrWhiteSpace(plan.TargetInstanceName)
            ? OspfFleetNaming.Ros7InstanceName
            : plan.TargetInstanceName;
        string expectedBackbone = string.IsNullOrWhiteSpace(plan.TargetBackboneAreaName)
            ? OspfFleetNaming.Ros7BackboneAreaName
            : plan.TargetBackboneAreaName;
        string expectedSite = string.IsNullOrWhiteSpace(plan.TargetSiteAreaName)
            ? OspfFleetNaming.Ros7SiteAreaName(plan.Identity)
            : plan.TargetSiteAreaName;

        List<OspfInstanceRow> v2Instances = inventory.Instances
            .Where(static i => !string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<OspfInstanceRow> named = v2Instances
            .Where(i => string.Equals(i.Name, expectedInstance, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (named.Count == 0)
        {
            return Failed($"Expected ROS7 OSPFv2 instance '{expectedInstance}' is missing.");
        }

        if (named.Count > 1)
        {
            return Failed($"Multiple live OSPFv2 instances named '{expectedInstance}'; refusing.");
        }

        OspfInstanceRow instance = named[0];
        if (string.IsNullOrWhiteSpace(instance.RouterId))
        {
            return Failed(
                $"Live OSPFv2 instance '{instance.Name}' has no router-id; Loopback RID validation requires it.");
        }

        if (!OspfLoopbackRouterId.EqualsRouterId(instance.RouterId, derivedRid))
        {
            return Failed(
                $"Live OSPFv2 instance '{instance.Name}' router-id '{instance.RouterId}' conflicts with "
                + $"Loopback /32-derived Router ID '{derivedRid}'.");
        }

        // Refuse to silently use a differently named sole instance.
        if (v2Instances.Count == 1
            && !string.Equals(v2Instances[0].Name, expectedInstance, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(
                $"Sole live OSPFv2 instance is '{v2Instances[0].Name}', expected deterministic '{expectedInstance}'.");
        }

        OspfAreaRow? backbone = ResolveExpectedArea(
            inventory.Areas,
            expectedInstance,
            expectedBackbone,
            OspfFleetNaming.BackboneAreaId,
            "backbone",
            errors);
        OspfAreaRow? site = ResolveExpectedArea(
            inventory.Areas,
            expectedInstance,
            expectedSite,
            derivedRid,
            "site",
            errors);

        if (errors.Count > 0 || backbone is null || site is null)
        {
            return Failed(errors.Count > 0 ? errors : ["Deterministic area resolution failed."]);
        }

        if (string.Equals(backbone.Name, site.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Failed(
                $"Backbone and site resolve to the same live area '{backbone.Name}'; refusing.");
        }

        Dictionary<string, OspfLiveAreaBinding> areaBindings = new(StringComparer.Ordinal);
        foreach (OspfPlanArea planned in plan.Areas)
        {
            if (planned.IsBackbone
                || string.Equals(planned.AreaId, OspfFleetNaming.BackboneAreaId, StringComparison.Ordinal))
            {
                areaBindings[planned.Name] = new OspfLiveAreaBinding
                {
                    LogicalName = planned.Name,
                    LiveName = backbone.Name!,
                    AreaId = OspfFleetNaming.BackboneAreaId,
                    LiveObjectId = backbone.Id,
                };
            }
            else
            {
                areaBindings[planned.Name] = new OspfLiveAreaBinding
                {
                    LogicalName = planned.Name,
                    LiveName = site.Name!,
                    AreaId = derivedRid,
                    LiveObjectId = site.Id,
                };
            }
        }

        foreach (string referenced in CollectReferencedAreaNames(plan))
        {
            if (!areaBindings.ContainsKey(referenced))
            {
                errors.Add(
                    $"Plan references area '{referenced}' on templates/ranges but that area is not present in plan.Areas.");
            }
        }

        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        return new OspfLiveIdentityResolutionResult
        {
            Succeeded = true,
            Binding = new OspfLiveIdentityBinding
            {
                LogicalInstanceName = plan.InstanceName,
                LiveInstanceName = instance.Name!,
                LiveInstanceId = instance.Id,
                LiveRouterId = instance.RouterId,
                DerivedLoopbackRouterId = derivedRid,
                LoopbackInterfaceName = loopback.LoopbackInterfaceName,
                Areas = areaBindings,
            },
        };
    }

    public static OspfMigrationPlan RemapPlan(OspfMigrationPlan plan, OspfLiveIdentityBinding binding)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(binding);

        string MapArea(string logical) =>
            binding.Areas.TryGetValue(logical, out OspfLiveAreaBinding? mapped)
                ? mapped.LiveName
                : throw new InvalidOperationException(
                    $"OSPF live identity binding is missing area mapping for '{logical}'.");

        return new OspfMigrationPlan
        {
            ImportId = plan.ImportId,
            JobId = plan.JobId,
            RouterIp = plan.RouterIp,
            Identity = plan.Identity,
            SourceRouterOsVersion = plan.SourceRouterOsVersion,
            Architecture = plan.Architecture,
            SourceKind = plan.SourceKind,
            ContentHash = plan.ContentHash,
            InstanceName = binding.LiveInstanceName,
            RouterId = plan.RouterId,
            TargetInstanceName = plan.TargetInstanceName,
            TargetBackboneAreaName = plan.TargetBackboneAreaName,
            TargetSiteAreaName = plan.TargetSiteAreaName,
            LoopbackInterfaceName = binding.LoopbackInterfaceName ?? plan.LoopbackInterfaceName,
            LoopbackCidr = plan.LoopbackCidr,
            ObservedInScopeLoopbackAddresses = plan.ObservedInScopeLoopbackAddresses,
            IgnoredOutsideRangeLoopbackAddresses = plan.IgnoredOutsideRangeLoopbackAddresses,
            DerivedManagementCidr = plan.DerivedManagementCidr,
            Areas = plan.Areas
                .Select(a => new OspfPlanArea
                {
                    Name = MapArea(a.Name),
                    AreaId = a.AreaId,
                    IsBackbone = a.IsBackbone,
                    TargetType = a.TargetType,
                    NoSummaries = a.NoSummaries,
                    DefaultCost = a.DefaultCost,
                    SourceDefaultCost = a.SourceDefaultCost,
                })
                .ToList(),
            AreaRanges = plan.AreaRanges
                .Select(r => new OspfPlanAreaRange
                {
                    AreaName = MapArea(r.AreaName),
                    RangeCidr = r.RangeCidr,
                    Comment = r.Comment,
                    Advertise = r.Advertise,
                })
                .ToList(),
            ActiveBackhauls = plan.ActiveBackhauls
                .Select(b => new OspfPlanBackhaulTemplate
                {
                    AreaName = MapArea(b.AreaName),
                    Interface = b.Interface,
                    NetworkCidr = b.NetworkCidr,
                    Cost = b.Cost,
                    Type = b.Type,
                    Passive = b.Passive,
                    SourceCost = b.SourceCost,
                })
                .ToList(),
            PassiveLoopbacks = plan.PassiveLoopbacks
                .Select(l => new OspfPlanLoopbackTemplate
                {
                    AreaName = MapArea(l.AreaName),
                    NetworkCidr = l.NetworkCidr,
                    Interface = l.Interface,
                    Passive = l.Passive,
                })
                .ToList(),
            PassiveSiteNetworks = plan.PassiveSiteNetworks
                .Select(s => new OspfPlanSiteNetworkTemplate
                {
                    AreaName = MapArea(s.AreaName),
                    Interfaces = s.Interfaces,
                    NetworkCidr = s.NetworkCidr,
                    Comment = s.Comment,
                    Passive = s.Passive,
                    CapturedSemantics = s.CapturedSemantics,
                })
                .ToList(),
            SupersededTemplates = plan.SupersededTemplates,
            RemoveAllRoutingFilters = plan.RemoveAllRoutingFilters,
            ClearAllRedistribution = plan.ClearAllRedistribution,
            RemoveAllOspfV3 = plan.RemoveAllOspfV3,
            OspfV3ToRemove = plan.OspfV3ToRemove,
            NormalisationNotes = plan.NormalisationNotes
                .Append(
                    $"Live ROS7 identity resolved: instance '{plan.InstanceName}' → '{binding.LiveInstanceName}' "
                    + $"(Loopback RID {binding.DerivedLoopbackRouterId}); "
                    + string.Join(
                        "; ",
                        binding.Areas.Values.Select(static a => $"area '{a.LogicalName}' → '{a.LiveName}'")))
                .ToList(),
            ValidationErrors = plan.ValidationErrors,
        };
    }

    private static OspfAreaRow? ResolveExpectedArea(
        IReadOnlyList<OspfAreaRow> liveAreas,
        string liveInstanceName,
        string expectedName,
        string expectedAreaId,
        string role,
        List<string> errors)
    {
        List<OspfAreaRow> emptyInstance = liveAreas
            .Where(static a => string.IsNullOrWhiteSpace(a.Instance))
            .ToList();
        if (emptyInstance.Any(a =>
                string.Equals(a.Name, expectedName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(a.AreaId)
                    && string.Equals(a.AreaId.Trim(), expectedAreaId, StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add(
                $"Live OSPF {role} area '{expectedName}' (area-id={expectedAreaId}) has empty instance ownership; "
                + "RouterOS 7 requires an explicit instance.");
            return null;
        }

        List<OspfAreaRow> underInstance = liveAreas
            .Where(a =>
                !string.IsNullOrWhiteSpace(a.Instance)
                && string.Equals(a.Instance, liveInstanceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<OspfAreaRow> byName = underInstance
            .Where(a => string.Equals(a.Name, expectedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count > 1)
        {
            errors.Add($"Multiple live {role} areas named '{expectedName}' under instance '{liveInstanceName}'.");
            return null;
        }

        if (byName.Count == 0)
        {
            List<OspfAreaRow> foreign = liveAreas
                .Where(a =>
                    string.Equals(a.Name, expectedName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(a.Instance)
                    && !string.Equals(a.Instance, liveInstanceName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (foreign.Count > 0)
            {
                errors.Add(
                    $"Expected {role} area '{expectedName}' belongs to instance '{foreign[0].Instance}', "
                    + $"not '{liveInstanceName}'.");
            }
            else
            {
                errors.Add(
                    $"Expected ROS7 {role} area '{expectedName}' (area-id={expectedAreaId}) is missing "
                    + $"under instance '{liveInstanceName}'.");
            }

            return null;
        }

        OspfAreaRow area = byName[0];
        string liveAreaId = string.IsNullOrWhiteSpace(area.AreaId)
            ? OspfFleetNaming.BackboneAreaId
            : area.AreaId.Trim();
        // ROS7 export may omit area-id for backbone (defaults to 0.0.0.0).
        if (string.IsNullOrWhiteSpace(area.AreaId)
            && string.Equals(expectedAreaId, OspfFleetNaming.BackboneAreaId, StringComparison.Ordinal))
        {
            liveAreaId = OspfFleetNaming.BackboneAreaId;
        }

        if (!string.Equals(liveAreaId, expectedAreaId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Live {role} area '{area.Name}' area-id '{area.AreaId ?? "<unset>"}' is incompatible with "
                + $"expected '{expectedAreaId}'.");
            return null;
        }

        List<OspfAreaRow> byAreaId = underInstance
            .Where(a =>
                string.Equals(
                    string.IsNullOrWhiteSpace(a.AreaId) ? OspfFleetNaming.BackboneAreaId : a.AreaId.Trim(),
                    expectedAreaId,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byAreaId.Count > 1)
        {
            errors.Add(
                $"Multiple live areas share area-id '{expectedAreaId}' under instance '{liveInstanceName}'.");
            return null;
        }

        return area;
    }

    private static IEnumerable<string> CollectReferencedAreaNames(OspfMigrationPlan plan)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (OspfPlanAreaRange range in plan.AreaRanges)
        {
            if (!string.IsNullOrWhiteSpace(range.AreaName))
            {
                names.Add(range.AreaName);
            }
        }

        foreach (OspfPlanBackhaulTemplate bh in plan.ActiveBackhauls)
        {
            if (!string.IsNullOrWhiteSpace(bh.AreaName))
            {
                names.Add(bh.AreaName);
            }
        }

        foreach (OspfPlanLoopbackTemplate loop in plan.PassiveLoopbacks)
        {
            if (!string.IsNullOrWhiteSpace(loop.AreaName))
            {
                names.Add(loop.AreaName);
            }
        }

        foreach (OspfPlanSiteNetworkTemplate site in plan.PassiveSiteNetworks)
        {
            if (!string.IsNullOrWhiteSpace(site.AreaName))
            {
                names.Add(site.AreaName);
            }
        }

        return names;
    }

    private static OspfLiveIdentityResolutionResult Failed(string error) => Failed([error]);

    private static OspfLiveIdentityResolutionResult Failed(IReadOnlyList<string> errors) =>
        new()
        {
            Succeeded = false,
            Errors = errors.ToList(),
        };
}

internal sealed class OspfLiveIdentityResolutionResult
{
    public required bool Succeeded { get; init; }

    public OspfLiveIdentityBinding? Binding { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public string? Detail => Succeeded ? null : string.Join("; ", Errors);
}

internal sealed class OspfLiveIdentityBinding
{
    public required string LogicalInstanceName { get; init; }

    public required string LiveInstanceName { get; init; }

    public string? LiveInstanceId { get; init; }

    public string? LiveRouterId { get; init; }

    public required string DerivedLoopbackRouterId { get; init; }

    public string? LoopbackInterfaceName { get; init; }

    public required IReadOnlyDictionary<string, OspfLiveAreaBinding> Areas { get; init; }
}

internal sealed class OspfLiveAreaBinding
{
    public required string LogicalName { get; init; }

    public required string LiveName { get; init; }

    public required string AreaId { get; init; }

    public string? LiveObjectId { get; init; }
}
