using MikrotikWorker.Logging;
using tik4net;

namespace MikrotikWorker.Upgrade.Ospf;

internal sealed class OspfV7Reconciler(
    IOspfMikrotikOperations ops,
    ILogger<OspfV7Reconciler> logger)
{
    /// <summary>
    /// Applies OSPF v7 reconciliation. Returns the pre-mutation management-plane baseline so callers
    /// can re-assert after verification. Throws <see cref="OspfBackboneMutationGuardException"/> to
    /// abort all subsequent mutations — no automatic rollback.
    /// </summary>
    public async Task<OspfManagementPlaneBaseline> ApplyAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        string? managementHost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsValid)
        {
            throw new InvalidOperationException("Refusing to apply invalid OSPF migration plan.");
        }

        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogInformation(
                "OSPF v7 reconciliation started. Instance={Instance}, RouterId={RouterId}, Backhauls={Backhauls}, SiteMatchers={Sites}, Ranges={Ranges}",
                plan.InstanceName,
                plan.RouterId,
                plan.ActiveBackhauls.Count,
                plan.PassiveSiteNetworks.Count,
                plan.AreaRanges.Count);
        }

        OspfManagementPlaneBaseline baseline = await ops
            .CaptureManagementPlaneBaselineAsync(
                connection,
                plan,
                managementHost ?? plan.RouterIp,
                cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Management-plane baseline: host={Host}, backhaulIf={If}, templateId={Id}, wasActive={Active}, passive={Passive}, disabled={Disabled}, area={Area}, net={Net}, neighbour={Nbr}/{Rid} state={State}, routeDst={RouteDst}, gw={Gw}, outIf={OutIf}, routeDependsOnBh={Depends}, e2eVerified={E2E}",
            baseline.ManagementHost,
            baseline.BackboneInterface,
            baseline.BackboneTemplateId,
            baseline.BackboneWasActive,
            baseline.BackboneTemplatePassive,
            baseline.BackboneTemplateDisabled,
            baseline.BackboneArea,
            baseline.BackboneNetwork,
            baseline.NeighbourAddress,
            baseline.NeighbourRouterId,
            baseline.NeighbourState,
            baseline.ManagementRouteDst,
            baseline.ManagementRouteGateway ?? baseline.ManagementRouteImmediateGw,
            baseline.ManagementOutgoingInterface,
            baseline.ManagementRouteDependsOnBackbone,
            baseline.EndToEndManagementConnectivityVerified);

        await ops.RemoveAllRoutingFiltersAsync(connection, cancellationToken).ConfigureAwait(false);
        await ops.ClearOspfInstanceFilterReferencesAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        await ops.ClearOspfRedistributionAsync(connection, plan.InstanceName, cancellationToken)
            .ConfigureAwait(false);
        await ops.SetInstanceRouterIdAsync(connection, plan.InstanceName, plan.RouterId, cancellationToken)
            .ConfigureAwait(false);

        foreach (OspfPlanArea area in plan.Areas)
        {
            await ops.EnsureOspfAreaAsync(connection, plan.InstanceName, area, cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation(
                "OSPF area ensured name={Name} area-id={AreaId} type={Type} default-cost={DefaultCost} source-default-cost={SourceDefaultCost}",
                area.Name,
                area.AreaId,
                area.TargetType,
                area.DefaultCost ?? "<unset>",
                area.SourceDefaultCost ?? "<unset>");
        }

        foreach (OspfPlanAreaRange range in plan.AreaRanges)
        {
            await ops.EnsureOspfAreaRangeAsync(connection, range, cancellationToken).ConfigureAwait(false);
        }

        // Active backhauls first — never allow later passive operations to touch them.
        foreach (OspfPlanBackhaulTemplate backhaul in plan.ActiveBackhauls)
        {
            await ops.EnsureBackhaulTemplateAsync(connection, backhaul, cancellationToken)
                .ConfigureAwait(false);
            await ops.AssertBackboneStillActiveAsync(connection, plan, baseline, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (OspfPlanLoopbackTemplate loopback in plan.PassiveLoopbacks)
        {
            await ops.EnsureLoopbackTemplateAsync(connection, loopback, plan.ActiveBackhauls, cancellationToken)
                .ConfigureAwait(false);
            await ops.AssertBackboneStillActiveAsync(connection, plan, baseline, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (OspfPlanSiteNetworkTemplate site in plan.PassiveSiteNetworks)
        {
            await ops.EnsureSiteNetworkTemplateAsync(connection, site, plan.ActiveBackhauls, cancellationToken)
                .ConfigureAwait(false);
        }

        await ops.AssertBackboneStillActiveAsync(connection, plan, baseline, cancellationToken)
            .ConfigureAwait(false);

        await ops.RemoveObsoleteTemplatesAsync(connection, plan, cancellationToken)
            .ConfigureAwait(false);
        await ops.AssertBackboneStillActiveAsync(connection, plan, baseline, cancellationToken)
            .ConfigureAwait(false);

        await ops.RemoveOspfV3Async(connection, plan, cancellationToken).ConfigureAwait(false);
        await ops.AssertBackboneStillActiveAsync(connection, plan, baseline, cancellationToken)
            .ConfigureAwait(false);

        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogInformation("OSPF v7 reconciliation commands completed; verification required next");
        }

        return baseline;
    }
}

internal sealed class OspfV7MigrationVerifier(
    IOspfMikrotikOperations ops,
    ILogger<OspfV7MigrationVerifier> logger)
{
    public async Task<OspfVerificationResult> VerifyAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(plan);

        List<string> errors = [];
        OspfV7Inventory inventory = await ops.ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        int filters = await ops.CountRoutingFiltersAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        if (filters != 0)
        {
            errors.Add($"Routing filters remain after migration (count={filters}).");
        }

        foreach (OspfInstanceRow instance in inventory.Instances.Where(static i =>
                     !string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (KeyValuePair<string, string?> prop in instance.RedistributeProperties)
            {
                if (!string.IsNullOrWhiteSpace(prop.Value)
                    && !prop.Value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    && !prop.Value.Equals("no", StringComparison.OrdinalIgnoreCase)
                    && prop.Value != "")
                {
                    errors.Add($"Redistribution still set on instance '{instance.Name}': {prop.Key}={prop.Value}.");
                }
            }

            if (!string.IsNullOrWhiteSpace(instance.InFilter))
            {
                errors.Add(
                    $"Stale in-filter remains on OSPFv2 instance '{instance.Name}': '{instance.InFilter}'.");
            }

            if (!string.IsNullOrWhiteSpace(instance.OutFilter))
            {
                errors.Add(
                    $"Stale out-filter remains on OSPFv2 instance '{instance.Name}': '{instance.OutFilter}'.");
            }
        }

        if (inventory.OspfV3Objects.Count > 0
            || inventory.Instances.Any(static i =>
                string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("OSPFv3 configuration remains after removal.");
        }

        foreach (OspfAreaRow area in inventory.Areas)
        {
            bool backbone = string.Equals(area.AreaId, OspfMigrationPlanner.BackboneAreaId, StringComparison.Ordinal);
            if (backbone)
            {
                if (string.Equals(area.Type, "stub", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(area.Type, "nssa", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Backbone area '{area.Name}' must not be stub/nssa (type={area.Type}).");
                }
            }
            else if (!string.Equals(area.Type, "stub", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Non-backbone area '{area.Name}' must be stub (type={area.Type}).");
            }
        }

        foreach (OspfPlanArea planned in plan.Areas)
        {
            OspfAreaRow? live = inventory.Areas.FirstOrDefault(a =>
                string.Equals(a.Name, planned.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.AreaId, planned.AreaId, StringComparison.Ordinal));
            if (live is null)
            {
                errors.Add($"Planned area '{planned.Name}' missing after reconciliation.");
                continue;
            }

            string? liveCost = string.IsNullOrWhiteSpace(live.DefaultCost) ? null : live.DefaultCost.Trim();
            if (planned.DefaultCost is null)
            {
                if (liveCost is not null)
                {
                    errors.Add(
                        $"Area '{planned.Name}' default-cost is stale/unexpected '{liveCost}' (plan requires unset).");
                }
            }
            else if (!string.Equals(liveCost, planned.DefaultCost, StringComparison.Ordinal))
            {
                errors.Add(
                    $"Area '{planned.Name}' default-cost is '{liveCost ?? "<unset>"}', expected '{planned.DefaultCost}'.");
            }

            bool liveNoSummaries = live.NoSummaries == true;
            if (planned.NoSummaries)
            {
                errors.Add(
                    $"Area '{planned.Name}' plan unexpectedly requires no-summaries; fleet policy forbids it.");
            }
            else if (liveNoSummaries)
            {
                errors.Add($"Area '{planned.Name}' has no-summaries present; it must be absent.");
            }

            logger.LogInformation(
                "OSPF area verified name={Name} source={Source} expected-cost={Expected} observed-cost={Observed} no-summaries-absent={Absent}",
                planned.Name,
                planned.SourceDefaultCost ?? "<unset>",
                planned.DefaultCost ?? "<unset>",
                liveCost ?? "<unset>",
                !liveNoSummaries);
        }

        foreach (OspfPlanAreaRange range in plan.AreaRanges)
        {
            bool found = inventory.AreaRanges.Any(r =>
                string.Equals(r.Area, range.AreaName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(r.Range), range.RangeCidr, StringComparison.Ordinal));
            if (!found)
            {
                errors.Add($"Missing area range {range.AreaName} {range.RangeCidr}.");
            }
        }

        // Final acceptance requires exactly the planned templates — disabled clutter also fails.
        IReadOnlyList<OspfInterfaceTemplateRow> allTemplates = inventory.Templates;
        List<OspfInterfaceTemplateRow> enabled = allTemplates.Where(static t => !t.Disabled).ToList();

        foreach (OspfPlanBackhaulTemplate bh in plan.ActiveBackhauls)
        {
            List<OspfInterfaceTemplateRow> matches = enabled
                .Where(t =>
                    !t.Passive
                    && string.Equals(t.Area, bh.AreaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Interfaces, bh.Interface, StringComparison.Ordinal)
                    && string.Equals(Normalize(t.Networks), bh.NetworkCidr, StringComparison.Ordinal)
                    && string.Equals(t.Type, "ptp", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                bool disabledOnly = allTemplates.Any(t =>
                    t.Disabled
                    && string.Equals(t.Interfaces, bh.Interface, StringComparison.Ordinal)
                    && string.Equals(Normalize(t.Networks), bh.NetworkCidr, StringComparison.Ordinal));
                errors.Add(
                    disabledOnly
                        ? $"Backhaul template {bh.Interface} {bh.NetworkCidr} is disabled."
                        : $"Missing active backhaul template {bh.Interface} {bh.NetworkCidr}.");
                continue;
            }

            if (matches.Count > 1)
            {
                errors.Add(
                    $"Duplicate active backhaul templates for {bh.Interface} {bh.NetworkCidr} (count={matches.Count}).");
            }

            OspfInterfaceTemplateRow match = matches[0];
            if (match.Passive)
            {
                errors.Add(
                    $"Backbone passive-state regression: backhaul {bh.Interface} {bh.NetworkCidr} is passive.");
            }

            if (!string.Equals(match.Cost, OspfMigrationPlanner.FleetBackhaulCost.ToString(), StringComparison.Ordinal))
            {
                errors.Add(
                    $"Backhaul {bh.Interface} cost is '{match.Cost}', expected {OspfMigrationPlanner.FleetBackhaulCost}.");
            }
        }

        foreach (OspfPlanLoopbackTemplate loop in plan.PassiveLoopbacks)
        {
            List<OspfInterfaceTemplateRow> matches = enabled
                .Where(t =>
                    t.Passive
                    && string.Equals(t.Area, loop.AreaName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Normalize(t.Networks), loop.NetworkCidr, StringComparison.Ordinal)
                    && (string.IsNullOrWhiteSpace(loop.Interface)
                        || string.IsNullOrWhiteSpace(t.Interfaces)
                        || string.Equals(t.Interfaces, loop.Interface, StringComparison.Ordinal)))
                .ToList();
            if (matches.Count == 0)
            {
                errors.Add($"Missing passive loopback template {loop.NetworkCidr}.");
                continue;
            }

            if (matches.Count > 1)
            {
                errors.Add($"Duplicate passive loopback templates for {loop.NetworkCidr} (count={matches.Count}).");
            }

            OspfInterfaceTemplateRow match = matches[0];
            if (!match.Passive)
            {
                errors.Add($"Loopback template {loop.NetworkCidr} must be passive.");
            }

            if (!string.IsNullOrWhiteSpace(match.Type)
                && (match.Type.Equals("ptp", StringComparison.OrdinalIgnoreCase)
                    || match.Type.Equals("point-to-point", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(
                    $"Loopback template {loop.NetworkCidr} must not be PTP (type={match.Type}).");
            }
        }

        foreach (OspfPlanSiteNetworkTemplate site in plan.PassiveSiteNetworks)
        {
            List<OspfInterfaceTemplateRow> matches = enabled
                .Where(t =>
                    OspfSiteMatcherConsolidation.MatchesPlannedSite(
                        t.Area,
                        t.Interfaces,
                        t.Networks,
                        t.Passive,
                        t.Disabled,
                        site))
                .ToList();
            if (matches.Count == 0)
            {
                errors.Add(
                    $"Missing planned passive site matcher {site.AreaName} interfaces={site.Interfaces} networks={site.NetworkCidr}.");
                continue;
            }

            if (matches.Count > 1)
            {
                errors.Add(
                    $"Duplicate planned site templates for {site.AreaName} interfaces={site.Interfaces} networks={site.NetworkCidr} (count={matches.Count}).");
            }

            if (!OspfSiteMatcherConsolidation.InterfacesEqual(matches[0].Interfaces, site.Interfaces))
            {
                errors.Add(
                    $"Site matcher {site.AreaName} interfaces mismatch live='{matches[0].Interfaces}' plan='{site.Interfaces}'.");
            }

            foreach (string required in OspfNetworkList.Split(site.NetworkCidr))
            {
                if (!OspfNetworkList.Split(matches[0].Networks).Contains(required, StringComparer.Ordinal))
                {
                    errors.Add(
                        $"Site matcher {site.AreaName} is missing required network '{required}'.");
                }
            }
        }

        foreach (OspfInterfaceTemplateRow template in allTemplates)
        {
            bool plannedBackhaul = plan.ActiveBackhauls.Any(b =>
                !template.Disabled
                && !template.Passive
                && string.Equals(b.Interface, template.Interfaces, StringComparison.Ordinal)
                && string.Equals(Normalize(template.Networks), b.NetworkCidr, StringComparison.Ordinal));
            bool plannedLoopback = plan.PassiveLoopbacks.Any(l =>
                !template.Disabled
                && template.Passive
                && string.Equals(Normalize(template.Networks), l.NetworkCidr, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(l.Interface)
                    || string.IsNullOrWhiteSpace(template.Interfaces)
                    || string.Equals(template.Interfaces, l.Interface, StringComparison.Ordinal)));
            bool plannedSite = plan.PassiveSiteNetworks.Any(s =>
                OspfSiteMatcherConsolidation.MatchesPlannedSite(
                    template.Area,
                    template.Interfaces,
                    template.Networks,
                    template.Passive,
                    template.Disabled,
                    s));

            if (plannedBackhaul || plannedLoopback || plannedSite)
            {
                continue;
            }

            if (template.Disabled)
            {
                errors.Add(
                    $"Disabled unplanned template remains interfaces={template.Interfaces} networks={template.Networks}; final inventory must contain exactly the planned templates.");
            }
            else if (!template.Passive)
            {
                errors.Add(
                    $"Unclassified active template remains interfaces={template.Interfaces} networks={template.Networks}.");
            }
            else
            {
                errors.Add(
                    $"Unexpected template remains interfaces={template.Interfaces} networks={template.Networks}.");
            }
        }

        int expectedTemplateCount = plan.ActiveBackhauls.Count
            + plan.PassiveLoopbacks.Count
            + plan.PassiveSiteNetworks.Count;
        if (allTemplates.Count != expectedTemplateCount)
        {
            errors.Add(
                $"Template inventory count is {allTemplates.Count}, expected exactly {expectedTemplateCount} (disabled leftovers are not allowed).");
        }

        using (DeviceLogging.BeginServiceSummary())
        {
            if (errors.Count == 0)
            {
                logger.LogInformation("OSPF v7 configuration verification succeeded");
            }
            else
            {
                logger.LogError("OSPF v7 configuration verification failed ({Count} issues)", errors.Count);
            }
        }

        return new OspfVerificationResult
        {
            Succeeded = errors.Count == 0,
            Errors = errors,
        };
    }

    private static string Normalize(string? cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p) ? p.ToCidrString() : cidr?.Trim() ?? string.Empty;
}

internal sealed class OspfVerificationResult
{
    public required bool Succeeded { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public string? Detail => Succeeded ? null : string.Join("; ", Errors);
}
