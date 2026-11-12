// -----------------------------------------------------------------------------
// File: OspfMigrationService.cs
//
// Description:
// Coordinates OSPF migration stages used by RouterOsV6UpgradeExecutor: capture
// and fleet-policy validation (optionally persist plan), then apply/reconcile
// to RouterOS 7 desired state with pre-apply backbone invariants, verification,
// and post-verify backbone assertion. Also exposes plan deletion for cleanup
// and failure-preservation paths.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;
using MikrotikWorker.Logging;
using MikrotikWorker.Scheduling;
using tik4net;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// OSPF capture/validate and apply/verify gates for the upgrade workflow.
/// </summary>
internal interface IOspfMigrationService
{
    /// <summary>
    /// Captures live OSPF state, builds and validates a migration plan, and optionally persists it
    /// when the RouterOS 7 stage may run (<paramref name="persistPlan"/>).
    /// </summary>
    Task<OspfGateResult> CaptureAndValidateAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        string routerOsVersion,
        string? architecture,
        bool persistPlan,
        CancellationToken cancellationToken);

    /// <summary>
    /// Re-validates plan invariants against current addresses, reconciles v7 OSPF desired state,
    /// verifies configuration, and asserts backbone/management safety after verify.
    /// Uses <paramref name="inMemoryPlan"/> or loads from the plan store.
    /// </summary>
    Task<OspfApplyGateResult> ApplyAndVerifyAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        OspfMigrationPlan? inMemoryPlan,
        CancellationToken cancellationToken,
        string? managementHost = null);

    /// <summary>
    /// Deletes any persisted migration plan for the job (best-effort cleanup).
    /// </summary>
    void DeletePlan(MikrotikScheduledJob job);
}

/// <summary>
/// Coordinates OSPF capture, validation, persistence, apply, and verification for the upgrade executor.
/// </summary>
internal sealed class OspfMigrationService(
    IOspfMikrotikOperations ops,
    IOspfMigrationPlanStore planStore,
    OspfV7Reconciler reconciler,
    OspfV7MigrationVerifier verifier,
    ILogger<OspfMigrationService> logger) : IOspfMigrationService
{
    /// <inheritdoc />
    public async Task<OspfGateResult> CaptureAndValidateAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        string routerOsVersion,
        string? architecture,
        bool persistPlan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(job);

        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogInformation("OSPF migration capture started");
        }

        OspfCaptureSourceKind kind = RouterOsVersion.TryParse(routerOsVersion, out RouterOsVersion ver) && ver.IsV7OrLater
            ? OspfCaptureSourceKind.RouterOsV7
            : OspfCaptureSourceKind.RouterOsV6;

        OspfCaptureSnapshot snapshot;
        try
        {
            snapshot = await ops.CaptureAsync(
                    connection,
                    new OspfCaptureRequest
                    {
                        ImportId = job.ImportId,
                        JobId = job.JobId,
                        RouterIp = job.RouterIp,
                        Identity = identity,
                        RouterOsVersion = routerOsVersion,
                        Architecture = architecture,
                        SourceKind = kind,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OSPF capture failed");
            return OspfGateResult.CaptureFailed(ex.Message);
        }

        using (DeviceLogging.BeginServiceOnly())
        {
            logger.LogInformation(
                "OSPF capture completed. Kind={Kind}, Instances={Instances}, Areas={Areas}, Networks={Networks}, Templates={Templates}, Filters={Filters}, OspfV3={OspfV3}",
                snapshot.SourceKind,
                snapshot.Instances.Count,
                snapshot.Areas.Count,
                snapshot.Networks.Count,
                snapshot.InterfaceTemplates.Count,
                snapshot.RoutingFilters.Count,
                snapshot.OspfV3Objects.Count);
        }

        OspfPlanValidationResult validation = OspfMigrationPlanner.Build(snapshot);
        foreach (string note in validation.Plan.NormalisationNotes)
        {
            logger.LogInformation("OSPF policy normalisation: {Note}", note);
        }

        if (!validation.Succeeded)
        {
            using (DeviceLogging.BeginServiceSummary())
            {
                logger.LogError(
                    "OSPF migration plan validation failed: {Detail}",
                    string.Join("; ", validation.Plan.ValidationErrors));
            }

            return OspfGateResult.ValidationFailed(
                string.Join("; ", validation.Plan.ValidationErrors),
                validation.Plan);
        }

        using (DeviceLogging.BeginServiceSummary())
        {
            logger.LogInformation(
                "OSPF migration plan validated. RouterId={RouterId}, Backhauls={Backhauls}, Loopbacks={Loopbacks}, SiteMatchers={Sites}",
                validation.Plan.RouterId,
                validation.Plan.ActiveBackhauls.Count,
                validation.Plan.PassiveLoopbacks.Count,
                validation.Plan.PassiveSiteNetworks.Count);
        }

        if (persistPlan)
        {
            await planStore.SaveAsync(validation.Plan, cancellationToken).ConfigureAwait(false);
        }

        return OspfGateResult.Ok(validation.Plan);
    }

    /// <inheritdoc />
    public async Task<OspfApplyGateResult> ApplyAndVerifyAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        OspfMigrationPlan? inMemoryPlan,
        CancellationToken cancellationToken,
        string? managementHost = null)
    {
        OspfMigrationPlan? plan = inMemoryPlan
            ?? await planStore.TryLoadAsync(job, identity, cancellationToken).ConfigureAwait(false);
        if (plan is null || !plan.IsValid)
        {
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfPlanValidationFailed,
                "OSPF migration plan missing, corrupt, or invalid after RouterOS 7 verification.");
        }

        string effectiveManagementHost = string.IsNullOrWhiteSpace(managementHost)
            ? job.RouterIp.ToString()
            : managementHost.Trim();

        IReadOnlyList<OspfIpAddressRow> currentAddresses;
        try
        {
            currentAddresses = await ops.ReadIpAddressInventoryAsync(connection, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pre-apply OSPF address inventory read failed; refusing to apply");
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfMigrationApplyFailed,
                $"Cannot re-validate backhaul isolation: address inventory incomplete ({ex.Message}).");
        }

        if (plan.ActiveBackhauls.Count > 0 && currentAddresses.Count == 0)
        {
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfMigrationApplyFailed,
                "Cannot re-validate backhaul isolation: address inventory is empty.");
        }

        IReadOnlyList<string> invariantErrors = OspfBackboneSafety.ValidatePlanInvariants(plan, currentAddresses);
        if (invariantErrors.Count > 0)
        {
            logger.LogError(
                "Pre-apply OSPF backbone/role invariants failed: {Detail}",
                string.Join("; ", invariantErrors));
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfPlanValidationFailed,
                string.Join("; ", invariantErrors));
        }

        OspfManagementPlaneBaseline baseline;
        try
        {
            baseline = await reconciler
                .ApplyAsync(connection, plan, managementHost: effectiveManagementHost, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OspfBackboneMutationGuardException ex)
        {
            logger.LogError(ex, "OSPF backbone/management safety abort");
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfMigrationApplyFailed,
                ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OSPF migration apply failed");
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfMigrationApplyFailed,
                ex.Message);
        }

        OspfVerificationResult verification;
        try
        {
            verification = await verifier.VerifyAsync(connection, plan, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OSPF migration verification threw");
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfMigrationVerificationFailed,
                ex.Message);
        }

        if (!verification.Succeeded)
        {
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfMigrationVerificationFailed,
                verification.Detail ?? "OSPF verification failed.");
        }

        try
        {
            await ops.AssertBackboneStillActiveAsync(connection, plan, baseline, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OspfBackboneMutationGuardException ex)
        {
            logger.LogError(ex, "OSPF backbone/management safety abort after verification");
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfMigrationVerificationFailed,
                ex.Message);
        }

        return OspfApplyGateResult.Ok(plan);
    }

    /// <inheritdoc />
    public void DeletePlan(MikrotikScheduledJob job) => planStore.Delete(job);
}

/// <summary>
/// Result of OSPF capture and plan validation before the RouterOS 7 / OSPF apply stages.
/// </summary>
internal sealed class OspfGateResult
{
    /// <summary>True when capture and fleet-policy validation succeeded.</summary>
    public bool Succeeded { get; private init; }

    /// <summary>Failure outcome when <see cref="Succeeded"/> is false.</summary>
    public UpgradeOutcome? FailureOutcome { get; private init; }

    /// <summary>Failure detail when unsuccessful.</summary>
    public string? Detail { get; private init; }

    /// <summary>Validated or partially built plan (present on validation failure for diagnostics).</summary>
    public OspfMigrationPlan? Plan { get; private init; }

    /// <summary>Successful capture and validation.</summary>
    public static OspfGateResult Ok(OspfMigrationPlan plan) => new()
    {
        Succeeded = true,
        Plan = plan,
    };

    /// <summary>Capture threw or otherwise failed before a valid plan was built.</summary>
    public static OspfGateResult CaptureFailed(string detail) => new()
    {
        Succeeded = false,
        FailureOutcome = UpgradeOutcome.OspfCaptureFailed,
        Detail = detail,
    };

    /// <summary>Plan was built but failed fleet-policy validation.</summary>
    public static OspfGateResult ValidationFailed(string detail, OspfMigrationPlan plan) => new()
    {
        Succeeded = false,
        FailureOutcome = UpgradeOutcome.OspfPlanValidationFailed,
        Detail = detail,
        Plan = plan,
    };
}

/// <summary>
/// Result of OSPF reconcile/apply and post-apply verification.
/// </summary>
internal sealed class OspfApplyGateResult
{
    /// <summary>True when apply and verification (including backbone assertion) succeeded.</summary>
    public bool Succeeded { get; private init; }

    /// <summary>Failure outcome when unsuccessful.</summary>
    public UpgradeOutcome FailureOutcome { get; private init; }

    /// <summary>Failure detail when unsuccessful.</summary>
    public string? Detail { get; private init; }

    /// <summary>Plan used for apply/verify when successful.</summary>
    public OspfMigrationPlan? Plan { get; private init; }

    /// <summary>Successful apply and verification.</summary>
    public static OspfApplyGateResult Ok(OspfMigrationPlan plan) => new()
    {
        Succeeded = true,
        Plan = plan,
    };

    /// <summary>Failed apply, invariant check, or verification.</summary>
    public static OspfApplyGateResult Failed(UpgradeOutcome outcome, string detail) => new()
    {
        Succeeded = false,
        FailureOutcome = outcome,
        Detail = detail,
    };
}
