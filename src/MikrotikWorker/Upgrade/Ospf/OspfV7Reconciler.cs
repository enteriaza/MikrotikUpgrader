// -----------------------------------------------------------------------------
// File: OspfV7Reconciler.cs
//
// Description:
// Applies a validated OSPF migration plan onto RouterOS 7: desired-state
// reconcile of instances, areas, templates, filters, and cleanup of superseded
// objects with backbone safety gates.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Logging;
using tik4net;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Reconciles live RouterOS 7 OSPF configuration to a validated migration plan.
/// </summary>
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

        // Resolve logical plan identities onto the live ROS7 inventory before any mutation.
        // Loopback /32 is the authoritative Router ID — read IP addresses independently of OSPF menus.
        OspfV7Inventory liveInventory = await ops.ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<OspfIpAddressRow> ipAddresses = await ops
            .ReadIpAddressInventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        OspfLiveIdentityResolutionResult identity = OspfLiveIdentityResolver.Resolve(
            plan,
            liveInventory,
            ipAddresses);
        if (!identity.Succeeded || identity.Binding is null)
        {
            throw new InvalidOperationException(
                "OSPF live identity resolution failed before mutation: "
                + (identity.Detail ?? "unspecified identity error."));
        }

        OspfMigrationPlan livePlan = OspfLiveIdentityResolver.RemapPlan(plan, identity.Binding);
        // Desired-state notes: discover live templates superseded by the remapped plan before mutation.
        // Cleanup classification also evaluates desired-state replacement directly against the live
        // inventory, so ROS7 auto-converted subdivisions do not depend on ROS6 capture provenance.
        livePlan = OspfLiveSupersededProvenance.EnrichFromLiveInventory(livePlan, liveInventory);
        logger.LogInformation(
            "OSPF live identity resolved. LogicalInstance={LogicalInstance}, LiveInstance={LiveInstance}, LiveInstanceId={LiveInstanceId}, LoopbackRid={LoopbackRid}, LoopbackIf={LoopbackIf}, AreaMaps={AreaMaps}, SupersededProvenance={Superseded}",
            identity.Binding.LogicalInstanceName,
            identity.Binding.LiveInstanceName,
            identity.Binding.LiveInstanceId ?? "<none>",
            identity.Binding.DerivedLoopbackRouterId,
            identity.Binding.LoopbackInterfaceName ?? "<none>",
            string.Join(
                ", ",
                identity.Binding.Areas.Values.Select(static a => $"{a.LogicalName}->{a.LiveName}")),
            livePlan.SupersededTemplates.Count);

        string targetInstanceName = string.IsNullOrWhiteSpace(livePlan.TargetInstanceName)
            ? OspfFleetNaming.Ros7InstanceName
            : livePlan.TargetInstanceName;
        string? legacyRouterIdReference = identity.Binding.LiveRouterId;

        // Validate mandatory OSPFv3 removal selection against the pre-mutation inventory. Fail closed
        // before any RouterOS write when selection is ambiguous (e.g. colliding area names).
        OspfV3RemovalSelection.Result v3Selection = OspfV3RemovalSelection.Build(liveInventory);
        if (!v3Selection.Succeeded)
        {
            throw new InvalidOperationException(
                "OSPFv3 removal selection failed before mutation: "
                + (v3Selection.FailureDetail ?? "unspecified selection failure."));
        }

        if (v3Selection.InstanceIds.Count > 0)
        {
            logger.LogInformation(
                "OSPFv3 removal selection validated before mutation (templates={Templates}, ranges={Ranges}, areas={Areas}, instances={Instances})",
                v3Selection.TemplateIds.Count,
                v3Selection.RangeIds.Count,
                v3Selection.AreaIds.Count,
                v3Selection.InstanceIds.Count);
        }

        // Mutation phase (caller must already have completed the verified central backup gate).
        await ops.EnsureOspfInstanceNameAsync(
                connection,
                identity.Binding.LiveInstanceName,
                targetInstanceName,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(livePlan.InstanceName, targetInstanceName, StringComparison.OrdinalIgnoreCase))
        {
            livePlan = ClonePlanWithInstanceName(livePlan, targetInstanceName);
        }

        OspfManagementPlaneBaseline baseline = await ops
            .CaptureManagementPlaneBaselineAsync(
                connection,
                livePlan,
                managementHost ?? livePlan.RouterIp,
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
        await ops.ClearOspfInstanceFilterReferencesAsync(connection, livePlan.InstanceName, cancellationToken)
            .ConfigureAwait(false);
        await ops.ClearOspfRedistributionAsync(connection, livePlan.InstanceName, cancellationToken)
            .ConfigureAwait(false);
        await ops.SetInstanceRouterIdAsync(connection, livePlan.InstanceName, livePlan.RouterId, cancellationToken)
            .ConfigureAwait(false);
        // Conservative /routing/id cleanup — retains entries when non-OSPF consumers cannot be ruled out.
        await ops.TryRemoveUnusedRoutingIdAsync(connection, legacyRouterIdReference, cancellationToken)
            .ConfigureAwait(false);

        // Mandatory OSPFv3 removal BEFORE any OSPFv2 area/template reconciliation that could collide
        // with same-named OSPFv3 areas via name/area-id lookup.
        await ops.RemoveOspfV3Async(connection, livePlan, cancellationToken).ConfigureAwait(false);
        await AssertOspfV3FullyRemovedAsync(connection, cancellationToken).ConfigureAwait(false);
        await ops.AssertBackboneStillActiveAsync(
                connection,
                livePlan,
                baseline,
                cancellationToken,
                requireFleetCost: false)
            .ConfigureAwait(false);

        foreach (OspfPlanArea area in livePlan.Areas)
        {
            await ops.EnsureOspfAreaAsync(connection, livePlan.InstanceName, area, cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation(
                "OSPF area ensured name={Name} area-id={AreaId} type={Type} default-cost={DefaultCost} source-default-cost={SourceDefaultCost}",
                area.Name,
                area.AreaId,
                area.TargetType,
                area.DefaultCost ?? "<unset>",
                area.SourceDefaultCost ?? "<unset>");
        }

        foreach (OspfPlanAreaRange range in livePlan.AreaRanges)
        {
            await ops.EnsureOspfAreaRangeAsync(connection, range, cancellationToken).ConfigureAwait(false);
        }

        // Active backhauls first — never allow later passive operations to touch them.
        // Mid-mutation backbone asserts omit fleet-cost conformity until every backhaul is ensured.
        foreach (OspfPlanBackhaulTemplate backhaul in livePlan.ActiveBackhauls)
        {
            string? priorCost = backhaul.SourceCost;
            await ops.EnsureBackhaulTemplateAsync(connection, backhaul, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(priorCost)
                && priorCost != OspfMigrationPlanner.FleetBackhaulCost.ToString())
            {
                logger.LogInformation(
                    "Backhaul '{Interface}' cost normalised {From} → {To}",
                    backhaul.Interface,
                    priorCost,
                    OspfMigrationPlanner.FleetBackhaulCost);
            }

            await ops.AssertBackboneStillActiveAsync(
                    connection,
                    livePlan,
                    baseline,
                    cancellationToken,
                    requireFleetCost: false)
                .ConfigureAwait(false);
        }

        foreach (OspfPlanLoopbackTemplate loopback in livePlan.PassiveLoopbacks)
        {
            await ops.EnsureLoopbackTemplateAsync(connection, loopback, livePlan.ActiveBackhauls, cancellationToken)
                .ConfigureAwait(false);
            await ops.AssertBackboneStillActiveAsync(
                    connection,
                    livePlan,
                    baseline,
                    cancellationToken,
                    requireFleetCost: false)
                .ConfigureAwait(false);
        }

        foreach (OspfPlanSiteNetworkTemplate site in livePlan.PassiveSiteNetworks)
        {
            await ops.EnsureSiteNetworkTemplateAsync(connection, site, livePlan.ActiveBackhauls, cancellationToken)
                .ConfigureAwait(false);
        }

        // Mandatory explicit site readback before any KnownLegacy deletion. Ensure* also asserts,
        // but cleanup must not start unless this gate succeeds independently.
        foreach (OspfPlanSiteNetworkTemplate site in livePlan.PassiveSiteNetworks)
        {
            await ops.AssertSiteNetworkTemplateStateAsync(connection, site, cancellationToken)
                .ConfigureAwait(false);
        }

        await ops.AssertBackboneStillActiveAsync(
                connection,
                livePlan,
                baseline,
                cancellationToken,
                requireFleetCost: true)
            .ConfigureAwait(false);

        await ops.RemoveObsoleteTemplatesAsync(connection, livePlan, cancellationToken)
            .ConfigureAwait(false);

        foreach (OspfPlanSiteNetworkTemplate site in livePlan.PassiveSiteNetworks)
        {
            await ops.AssertSiteNetworkTemplateStateAsync(connection, site, cancellationToken)
                .ConfigureAwait(false);
        }

        await ops.AssertBackboneStillActiveAsync(
                connection,
                livePlan,
                baseline,
                cancellationToken,
                requireFleetCost: true)
            .ConfigureAwait(false);

        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogInformation("OSPF v7 reconciliation commands completed; verification required next");
        }

        return baseline;
    }

    /// <summary>
    /// Re-reads live inventory and fails closed when any OSPFv3 or OSPFv3-orphan residual remains.
    /// </summary>
    private async Task AssertOspfV3FullyRemovedAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        OspfV7Inventory afterRemoval = await ops.ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        OspfV3ResidualDetector.Result residual = OspfV3ResidualDetector.Evaluate(afterRemoval);
        if (!residual.IsClean)
        {
            string prefix = residual.InventoryIncomplete
                ? "Incomplete OSPF inventory after mandatory OSPFv3 removal; refusing OSPFv2 "
                  + "area/template reconciliation. "
                : "OSPFv3 residual configuration remains after mandatory removal; refusing OSPFv2 "
                  + "area/template reconciliation. ";
            throw new InvalidOperationException(prefix + residual.Detail);
        }
    }

    /// <summary>
    /// Returns a copy of <paramref name="plan"/> whose <see cref="OspfMigrationPlan.InstanceName"/> is
    /// <paramref name="instanceName"/> (used after a live instance rename to <c>default-v2</c>).
    /// </summary>
    private static OspfMigrationPlan ClonePlanWithInstanceName(OspfMigrationPlan plan, string instanceName) =>
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
            InstanceName = instanceName,
            RouterId = plan.RouterId,
            TargetInstanceName = plan.TargetInstanceName,
            TargetBackboneAreaName = plan.TargetBackboneAreaName,
            TargetSiteAreaName = plan.TargetSiteAreaName,
            LoopbackInterfaceName = plan.LoopbackInterfaceName,
            LoopbackCidr = plan.LoopbackCidr,
            ObservedInScopeLoopbackAddresses = plan.ObservedInScopeLoopbackAddresses,
            IgnoredOutsideRangeLoopbackAddresses = plan.IgnoredOutsideRangeLoopbackAddresses,
            DerivedManagementCidr = plan.DerivedManagementCidr,
            DerivedManagementCidrs = OspfMigrationPlanner.GetDerivedManagementCidrs(plan),
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
            NormalisationNotes = plan.NormalisationNotes
                .Append($"OSPFv2 instance normalised to fleet name '{instanceName}'.")
                .ToList(),
            ValidationErrors = plan.ValidationErrors,
        };
}

/// <summary>
/// Verifies post-apply OSPF configuration against the migration plan.
/// </summary>
internal sealed class OspfV7MigrationVerifier(
    IOspfMikrotikOperations ops,
    ILogger<OspfV7MigrationVerifier> logger)
{
    /// <summary>
    /// Re-reads the live RouterOS 7 state and checks it against the plan, collecting every mismatch.
    /// </summary>
    /// <param name="connection">Open RouterOS connection.</param>
    /// <param name="plan">Migration plan that was applied.</param>
    /// <param name="cancellationToken">Token to cancel the verification.</param>
    /// <returns>A result that succeeds only when no mismatch was found.</returns>
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
        IReadOnlyList<OspfIpAddressRow> ipAddresses = await ops
            .ReadIpAddressInventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        // Re-resolve against the fresh inventory + Loopback /32 — never reuse apply-time .id values.
        OspfLiveIdentityResolutionResult identity = OspfLiveIdentityResolver.Resolve(
            plan,
            inventory,
            ipAddresses);
        if (!identity.Succeeded || identity.Binding is null)
        {
            return new OspfVerificationResult
            {
                Succeeded = false,
                Errors =
                [
                    "OSPF live identity resolution failed during verification: "
                    + (identity.Detail ?? "unspecified identity error."),
                ],
            };
        }

        if (!OspfLoopbackRouterId.EqualsRouterId(plan.RouterId, identity.Binding.DerivedLoopbackRouterId)
            || !OspfLoopbackRouterId.EqualsRouterId(
                identity.Binding.LiveRouterId,
                identity.Binding.DerivedLoopbackRouterId))
        {
            return new OspfVerificationResult
            {
                Succeeded = false,
                Errors =
                [
                    "Loopback /32 Router ID changed or diverged during migration verification "
                    + $"(plan='{plan.RouterId}', live='{identity.Binding.LiveRouterId}', "
                    + $"loopback='{identity.Binding.DerivedLoopbackRouterId}').",
                ],
            };
        }

        string expectedInstanceName = string.IsNullOrWhiteSpace(plan.TargetInstanceName)
            ? OspfFleetNaming.Ros7InstanceName
            : plan.TargetInstanceName.Trim();
        if (!string.Equals(
                identity.Binding.LiveInstanceName,
                expectedInstanceName,
                StringComparison.OrdinalIgnoreCase))
        {
            return new OspfVerificationResult
            {
                Succeeded = false,
                Errors =
                [
                    $"Live OSPFv2 instance name is '{identity.Binding.LiveInstanceName}', "
                    + $"expected fleet name '{expectedInstanceName}'.",
                ],
            };
        }

        OspfInstanceRow? verifiedInstance = inventory.Instances.FirstOrDefault(i =>
            string.Equals(i.Name, identity.Binding.LiveInstanceName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.Id, identity.Binding.LiveInstanceId, StringComparison.Ordinal));
        verifiedInstance ??= inventory.Instances.FirstOrDefault(i =>
            string.Equals(i.Name, expectedInstanceName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase));
        if (verifiedInstance is null)
        {
            return new OspfVerificationResult
            {
                Succeeded = false,
                Errors = [$"Fleet OSPFv2 instance '{expectedInstanceName}' is missing from live inventory."],
            };
        }

        if (string.Equals(verifiedInstance.Version, "3", StringComparison.OrdinalIgnoreCase))
        {
            return new OspfVerificationResult
            {
                Succeeded = false,
                Errors =
                [
                    $"Live instance '{verifiedInstance.Name}' is OSPFv3 (version=3); "
                    + "OSPFv2 migration target verification failed.",
                ],
            };
        }

        OspfMigrationPlan livePlan = OspfLiveIdentityResolver.RemapPlan(plan, identity.Binding);

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

        OspfV3ResidualDetector.Result v3Residual = OspfV3ResidualDetector.Evaluate(inventory);
        if (!v3Residual.IsClean)
        {
            errors.Add(
                v3Residual.InventoryIncomplete
                    ? "Incomplete OSPF inventory cannot prove OSPFv3 removal: " + v3Residual.Detail
                    : "OSPFv3 residual configuration remains after removal: " + v3Residual.Detail);
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

        foreach (OspfPlanArea planned in livePlan.Areas)
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

        foreach (OspfPlanAreaRange range in livePlan.AreaRanges)
        {
            bool found = inventory.AreaRanges.Any(r =>
                string.Equals(r.Area, range.AreaName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Normalize(r.Range), range.RangeCidr, StringComparison.Ordinal));
            if (!found)
            {
                errors.Add($"Missing area range {range.AreaName} {range.RangeCidr}.");
            }
        }

        errors.AddRange(VerifyManagementSummarisationPostconditions(livePlan, inventory));

        // Final acceptance compares the eligible OSPFv2 decision inventory against planned roles
        // plus explicitly preserved unmanaged residuals. Disabled OSPFv2 templates are invisible
        // to migration decisions and must not affect equality/count. Unknown admin state fails closed.
        IReadOnlyList<OspfInterfaceTemplateRow> allTemplates = inventory.Templates;
        List<OspfInterfaceTemplateRow> enabled =
            allTemplates.Where(OspfInventoryEligibility.IsEligible).ToList();
        Dictionary<string, string> areaInstances = BuildAreaInstanceMap(inventory);

        foreach (OspfPlanBackhaulTemplate bh in livePlan.ActiveBackhauls)
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
                    (t.Disabled || t.AdminState == OspfAdminState.Disabled)
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

        foreach (OspfPlanLoopbackTemplate loop in livePlan.PassiveLoopbacks)
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

        foreach (OspfPlanSiteNetworkTemplate site in livePlan.PassiveSiteNetworks)
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

        int preservedUnmanagedCount = 0;
        int disabledExcludedCount = 0;
        foreach (OspfInterfaceTemplateRow template in allTemplates)
        {
            if (template.AdminState == OspfAdminState.Unknown)
            {
                errors.Add(
                    $"Unplanned template with unknown disabled-state remains interfaces={template.Interfaces} networks={template.Networks}; refuse to treat as enabled.");
                continue;
            }

            if (template.AdminState == OspfAdminState.Disabled || template.Disabled)
            {
                // Disabled OSPFv2 objects are invisible to migration decisions; do not delete or
                // fail verification solely because they remain in raw inventory.
                disabledExcludedCount++;
                continue;
            }

            if (!OspfInventoryEligibility.IsEligible(template))
            {
                errors.Add(
                    $"Template interfaces={template.Interfaces} networks={template.Networks} is not eligible for OSPFv2 verification; refuse to treat as enabled.");
                continue;
            }

            bool plannedBackhaul = livePlan.ActiveBackhauls.Any(b =>
                !template.Passive
                && string.Equals(b.Interface, template.Interfaces, StringComparison.Ordinal)
                && string.Equals(Normalize(template.Networks), b.NetworkCidr, StringComparison.Ordinal));
            bool plannedLoopback = livePlan.PassiveLoopbacks.Any(l =>
                template.Passive
                && string.Equals(Normalize(template.Networks), l.NetworkCidr, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(l.Interface)
                    || string.IsNullOrWhiteSpace(template.Interfaces)
                    || string.Equals(template.Interfaces, l.Interface, StringComparison.Ordinal)));
            bool plannedSite = livePlan.PassiveSiteNetworks.Any(s =>
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

            OspfTemplateInventoryRow residualRow = ToCleanupInventoryRow(template, areaInstances);
            OspfTemplateClassification residualClass = OspfTemplateCleanupClassifier.ClassifyUnplannedResidual(
                residualRow,
                livePlan,
                out string residualReason);

            if (residualClass == OspfTemplateClassification.PreservedUnmanaged)
            {
                preservedUnmanagedCount++;
                logger.LogInformation(
                    "Preserved unmanaged OSPF template remains interfaces={Interfaces} networks={Networks} ({Reason})",
                    template.Interfaces,
                    template.Networks,
                    residualReason);
                continue;
            }

            if (residualClass == OspfTemplateClassification.KnownLegacy)
            {
                errors.Add(
                    $"Unexpected superseded/legacy template remains interfaces={template.Interfaces} networks={template.Networks}; {residualReason}");
                continue;
            }

            if (!template.Passive)
            {
                errors.Add(
                    $"Unclassified active template remains interfaces={template.Interfaces} networks={template.Networks}; {residualReason}");
            }
            else
            {
                errors.Add(
                    $"Unexpected template remains interfaces={template.Interfaces} networks={template.Networks}; {residualReason}");
            }
        }

        int expectedEligibleTemplateCount = livePlan.ActiveBackhauls.Count
                                            + livePlan.PassiveLoopbacks.Count
                                            + livePlan.PassiveSiteNetworks.Count
                                            + preservedUnmanagedCount;
        if (enabled.Count != expectedEligibleTemplateCount)
        {
            errors.Add(
                $"Eligible OSPFv2 template inventory count is {enabled.Count}, expected exactly {expectedEligibleTemplateCount} "
                + $"(planned={livePlan.ActiveBackhauls.Count + livePlan.PassiveLoopbacks.Count + livePlan.PassiveSiteNetworks.Count}, "
                + $"preserved-unmanaged={preservedUnmanagedCount}; "
                + $"raw-total={allTemplates.Count}, disabled-excluded={disabledExcludedCount}).");
        }

        using (DeviceLogging.BeginServiceSummary())
        {
            if (errors.Count == 0)
            {
                logger.LogInformation(
                    "OSPF v7 configuration verification succeeded. "
                    + "Management summarisation configuration is reconciled "
                    + "(site coverage of DerivedManagementCidr, advertised MGMT area-range, "
                    + "no obsolete more-specific management templates). "
                    + "Limitation: Type-3/LSA summary advertisement content is not independently "
                    + "inspected by this worker — summarisation advertisement is therefore not claimed.");
            }
            else
            {
                logger.LogError("OSPF v7 configuration verification failed ({Count} issues)", errors.Count);
            }
        }

        return new OspfVerificationResult { Succeeded = errors.Count == 0, Errors = errors, };
    }

    /// <summary>
    /// Verifies management summarisation <b>configuration</b> postconditions:
    /// planned site coverage of the derived <c>/24</c> (exact or broader covering aggregate),
    /// advertised MGMT area-range present, and no conflicting more-specific management templates.
    /// Does <b>not</b> independently verify Type-3 LSA advertisement content — that limitation is
    /// reported by the caller; configuration presence alone is not treated as advertisement proof.
    /// </summary>
    internal static IReadOnlyList<string> VerifyManagementSummarisationPostconditions(
        OspfMigrationPlan livePlan,
        OspfV7Inventory inventory)
    {
        List<string> errors = [];
        IReadOnlyList<string> derivedSet = OspfMigrationPlanner.GetDerivedManagementCidrs(livePlan);
        if (derivedSet.Count == 0)
        {
            errors.Add(
                "OSPF management summarisation postcondition failed: plan is missing DerivedManagementCidr.");
            return errors;
        }

        foreach (string derivedCidr in derivedSet)
        {
            if (!OspfIpPrefix.TryParse(derivedCidr, out OspfIpPrefix mgmt))
            {
                errors.Add(
                    $"OSPF management summarisation postcondition failed: derived management CIDR '{derivedCidr}' is invalid.");
                continue;
            }

            OspfPlanSiteNetworkTemplate? siteWithMgmt = livePlan.PassiveSiteNetworks.FirstOrDefault(s =>
                OspfNetworkList.TryParseAll(s.NetworkCidr, out List<OspfIpPrefix> nets, out _)
                && nets.Any(n => n.Equals(mgmt) || n.Contains(mgmt)));
            if (siteWithMgmt is null)
            {
                errors.Add(
                    $"OSPF management summarisation postcondition failed: planned site matchers do not cover "
                    + $"derived management summary '{derivedCidr}'.");
            }
            else if (!OspfSiteMatcherConsolidation.IsAllInterfaces(siteWithMgmt.Interfaces))
            {
                errors.Add(
                    $"OSPF management summarisation postcondition failed: management coverage "
                    + $"'{derivedCidr}' is not on interfaces=all "
                    + $"(interfaces='{siteWithMgmt.Interfaces}').");
            }

            OspfPlanAreaRange? plannedMgmtRange = livePlan.AreaRanges.FirstOrDefault(r =>
                string.Equals(Normalize(r.RangeCidr), mgmt.ToCidrString(), StringComparison.Ordinal)
                && (string.Equals(r.Comment, OspfFleetNaming.ManagementRangeComment, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(r.Comment)));
            OspfAreaRangeRow? liveMgmtRange = inventory.AreaRanges.FirstOrDefault(r =>
                string.Equals(Normalize(r.Range), mgmt.ToCidrString(), StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(r.Area)
                    || siteWithMgmt is null
                    || string.Equals(r.Area, siteWithMgmt.AreaName, StringComparison.OrdinalIgnoreCase)));
            if (plannedMgmtRange is null || liveMgmtRange is null)
            {
                errors.Add(
                    $"OSPF management summarisation postcondition failed: MGMT area range "
                    + $"'{derivedCidr}' is missing "
                    + $"(plan-range={plannedMgmtRange is not null}, live-range={liveMgmtRange is not null}).");
            }
            else if (liveMgmtRange.Advertise == false)
            {
                errors.Add(
                    $"OSPF management summarisation postcondition failed: MGMT area range "
                    + $"'{derivedCidr}' has advertise=no.");
            }

            foreach (OspfInterfaceTemplateRow template in
                     inventory.Templates.Where(OspfInventoryEligibility.IsEligible))
            {
                if (siteWithMgmt is not null
                    && OspfSiteMatcherConsolidation.MatchesPlannedSite(
                        template.Area,
                        template.Interfaces,
                        template.Networks,
                        template.Passive,
                        template.Disabled,
                        siteWithMgmt))
                {
                    continue;
                }

                if (!OspfNetworkList.TryParseAll(template.Networks, out List<OspfIpPrefix> liveNets, out _))
                {
                    continue;
                }

                bool conflicts = liveNets.Any(n => mgmt.Contains(n) && !n.Equals(mgmt));
                if (!conflicts)
                {
                    continue;
                }

                if (siteWithMgmt is not null
                    && !string.Equals(template.Area, siteWithMgmt.AreaName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                errors.Add(
                    $"OSPF management summarisation postcondition failed: obsolete more-specific management "
                    + $"template remains area={template.Area} interfaces={template.Interfaces} "
                    + $"networks={template.Networks} under summary '{derivedCidr}'.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Normalises a CIDR for comparison, falling back to the trimmed text when it cannot be parsed.
    /// </summary>
    /// <param name="cidr">CIDR text.</param>
    /// <returns>The normalised CIDR, or an empty string for null.</returns>
    private static string Normalize(string? cidr) =>
        OspfIpPrefix.TryParse(cidr, out OspfIpPrefix p) ? p.ToCidrString() : cidr?.Trim() ?? string.Empty;

    /// <summary>Maps area name → owning instance name from the fresh verification inventory.</summary>
    private static Dictionary<string, string> BuildAreaInstanceMap(OspfV7Inventory inventory)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (OspfAreaRow area in inventory.Areas)
        {
            if (string.IsNullOrWhiteSpace(area.Name) || string.IsNullOrWhiteSpace(area.Instance))
            {
                continue;
            }

            map[area.Name] = area.Instance;
        }

        return map;
    }

    /// <summary>
    /// Projects an interface-template inventory row into the cleanup classifier's sanitised shape.
    /// </summary>
    private static OspfTemplateInventoryRow ToCleanupInventoryRow(
        OspfInterfaceTemplateRow template,
        IReadOnlyDictionary<string, string> areaInstances)
    {
        string? areaInstance = null;
        if (!string.IsNullOrWhiteSpace(template.Area)
            && areaInstances.TryGetValue(template.Area, out string? resolved))
        {
            areaInstance = resolved;
        }

        OspfTemplateSemanticIdentity.AuthKeyResolution authKey =
            OspfTemplateSemanticIdentity.ResolveAuthKey(template.AuthKey, template.AuthenticationKey);

        return new OspfTemplateInventoryRow
        {
            Id = string.IsNullOrWhiteSpace(template.Id) ? "<verify>" : template.Id,
            Area = template.Area,
            Interfaces = template.Interfaces,
            Networks = template.Networks,
            Type = template.Type,
            Cost = template.Cost,
            Passive = template.Passive,
            Disabled = template.Disabled,
            AdminState = template.AdminState,
            Auth = template.Auth,
            AuthId = template.AuthId,
            AuthKeyPresent = authKey.Present,
            AuthKeyFingerprint = authKey.Fingerprint,
            AuthKeyAmbiguous = authKey.Ambiguous,
            Priority = template.Priority,
            UseBfd = template.UseBfd,
            HelloInterval = template.HelloInterval,
            DeadInterval = template.DeadInterval,
            RetransmitInterval = template.RetransmitInterval,
            TransmitDelay = template.TransmitDelay,
            InstanceId = template.InstanceId,
            AreaInstanceName = areaInstance,
            PrefixList = template.PrefixList,
            VlinkNeighborId = template.VlinkNeighborId,
            VlinkTransitArea = template.VlinkTransitArea,
            Comment = OspfTemplateSemanticIdentity.NormalizeComment(template.Comment),
        };
    }
}

/// <summary>
/// Result of post-apply OSPF configuration verification.
/// </summary>
internal sealed class OspfVerificationResult
{
    /// <summary>
    /// Gets a value indicating whether verification found no problems.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// One message per mismatch between the live ROS7 OSPF configuration and the migration plan
    /// (identity, areas, templates, backhaul cost and similar). Empty when <see cref="Succeeded"/> is
    /// <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Single-line failure message built by joining <see cref="Errors"/> with <c>"; "</c>, suitable for
    /// logs and gate failures. <see langword="null"/> when verification succeeded.
    /// </summary>
    public string? Detail => Succeeded ? null : string.Join("; ", Errors);
}
