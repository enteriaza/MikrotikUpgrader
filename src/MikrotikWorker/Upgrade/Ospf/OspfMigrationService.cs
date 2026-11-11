using MikrotikWorker.Assessment;
using MikrotikWorker.Logging;
using MikrotikWorker.Scheduling;
using tik4net;

namespace MikrotikWorker.Upgrade.Ospf;

internal interface IOspfMigrationService
{
    Task<OspfGateResult> CaptureAndValidateAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        string routerOsVersion,
        string? architecture,
        bool persistPlan,
        CancellationToken cancellationToken);

    Task<OspfApplyGateResult> ApplyAndVerifyAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        OspfMigrationPlan? inMemoryPlan,
        CancellationToken cancellationToken);

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

    public async Task<OspfApplyGateResult> ApplyAndVerifyAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        OspfMigrationPlan? inMemoryPlan,
        CancellationToken cancellationToken)
    {
        OspfMigrationPlan? plan = inMemoryPlan
            ?? await planStore.TryLoadAsync(job, identity, cancellationToken).ConfigureAwait(false);
        if (plan is null || !plan.IsValid)
        {
            return OspfApplyGateResult.Failed(
                UpgradeOutcome.OspfPlanValidationFailed,
                "OSPF migration plan missing, corrupt, or invalid after RouterOS 7 verification.");
        }

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
                .ApplyAsync(connection, plan, managementHost: job.RouterIp.ToString(), cancellationToken)
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

    public void DeletePlan(MikrotikScheduledJob job) => planStore.Delete(job);
}

internal sealed class OspfGateResult
{
    public bool Succeeded { get; private init; }
    public UpgradeOutcome? FailureOutcome { get; private init; }
    public string? Detail { get; private init; }
    public OspfMigrationPlan? Plan { get; private init; }

    public static OspfGateResult Ok(OspfMigrationPlan plan) => new()
    {
        Succeeded = true,
        Plan = plan,
    };

    public static OspfGateResult CaptureFailed(string detail) => new()
    {
        Succeeded = false,
        FailureOutcome = UpgradeOutcome.OspfCaptureFailed,
        Detail = detail,
    };

    public static OspfGateResult ValidationFailed(string detail, OspfMigrationPlan plan) => new()
    {
        Succeeded = false,
        FailureOutcome = UpgradeOutcome.OspfPlanValidationFailed,
        Detail = detail,
        Plan = plan,
    };
}

internal sealed class OspfApplyGateResult
{
    public bool Succeeded { get; private init; }
    public UpgradeOutcome FailureOutcome { get; private init; }
    public string? Detail { get; private init; }
    public OspfMigrationPlan? Plan { get; private init; }

    public static OspfApplyGateResult Ok(OspfMigrationPlan plan) => new()
    {
        Succeeded = true,
        Plan = plan,
    };

    public static OspfApplyGateResult Failed(UpgradeOutcome outcome, string detail) => new()
    {
        Succeeded = false,
        FailureOutcome = outcome,
        Detail = detail,
    };
}
