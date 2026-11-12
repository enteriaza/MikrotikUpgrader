// -----------------------------------------------------------------------------
// File: PassingOspfMigrationService.cs
//
// Description:
// IOspfMigrationService stub that reports success without touching a router.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
/// <summary>
/// Test double that satisfies OSPF gates without live RouterOS OSPF inventory.
/// Workflow tests that assert package/RB behaviour use this unless OSPF-specific.
/// </summary>
/// <remarks>OSPF migration service stub that always succeeds.</remarks>
internal sealed class PassingOspfMigrationService : IOspfMigrationService
{
    /// <summary>Returns a successful gate result carrying a minimal valid plan; does not touch the router.</summary>
    /// <param name="connection">Router connection (unused).</param>
    /// <param name="job">Job whose import and job ids are copied into the plan.</param>
    /// <param name="identity">Router identity recorded on the plan.</param>
    /// <param name="routerOsVersion">Source RouterOS version recorded on the plan.</param>
    /// <param name="architecture">Architecture recorded on the plan.</param>
    /// <param name="persistPlan">Ignored; nothing is persisted.</param>
    /// <param name="cancellationToken">Token (unused).</param>
    /// <returns>A completed task containing a passing <see cref="OspfGateResult"/>.</returns>
    public Task<OspfGateResult> CaptureAndValidateAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        string routerOsVersion,
        string? architecture,
        bool persistPlan,
        CancellationToken cancellationToken)
    {
        OspfMigrationPlan plan = CreateMinimalValidPlan(job, identity, routerOsVersion, architecture);
        return Task.FromResult(OspfGateResult.Ok(plan));
    }

    /// <summary>Returns a successful apply result without applying or verifying anything.</summary>
    /// <param name="connection">Router connection (unused).</param>
    /// <param name="job">Job used to build a fallback plan.</param>
    /// <param name="identity">Router identity used to build a fallback plan.</param>
    /// <param name="inMemoryPlan">Plan echoed back when supplied; otherwise a minimal valid plan is created.</param>
    /// <param name="cancellationToken">Token (unused).</param>
    /// <param name="managementHost">Management host (unused).</param>
    /// <returns>A completed task containing a passing <see cref="OspfApplyGateResult"/>.</returns>
    public Task<OspfApplyGateResult> ApplyAndVerifyAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        OspfMigrationPlan? inMemoryPlan,
        CancellationToken cancellationToken,
        string? managementHost = null) =>
        Task.FromResult(OspfApplyGateResult.Ok(inMemoryPlan ?? CreateMinimalValidPlan(job, identity, "7.23.8", null)));

    /// <summary>No-op because this fake never persists plans.</summary>
    /// <param name="job">Job whose plan would be deleted (unused).</param>
    public void DeletePlan(MikrotikScheduledJob job)
    {
    }

    /// <summary>Builds a structurally valid single-site OSPF plan with backbone and site areas.</summary>
    /// <param name="job">Job supplying import id, job id and router IP.</param>
    /// <param name="identity">Router identity recorded on the plan.</param>
    /// <param name="version">Source RouterOS version recorded on the plan.</param>
    /// <param name="architecture">Architecture recorded on the plan.</param>
    /// <returns>A minimal plan that passes gate validation.</returns>
    internal static OspfMigrationPlan CreateMinimalValidPlan(
        MikrotikScheduledJob job,
        string identity,
        string version,
        string? architecture) =>
        new()
        {
            ImportId = job.ImportId,
            JobId = job.JobId,
            RouterIp = job.RouterIp.ToString(),
            Identity = identity,
            SourceRouterOsVersion = version,
            Architecture = architecture,
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            ContentHash = "test",
            InstanceName = "default-v2",
            RouterId = "10.0.0.1",
            Areas =
            [
                new OspfPlanArea
                {
                    Name = "backbone",
                    AreaId = "0.0.0.0",
                    IsBackbone = true,
                    TargetType = "default",
                    NoSummaries = false,
                },
                new OspfPlanArea
                {
                    Name = "site",
                    AreaId = "10.0.0.1",
                    IsBackbone = false,
                    TargetType = "stub",
                    NoSummaries = false,
                },
            ],
            AreaRanges =
            [
                new OspfPlanAreaRange { AreaName = "site", RangeCidr = "10.0.0.0/24" },
            ],
            ActiveBackhauls =
            [
                new OspfPlanBackhaulTemplate
                {
                    AreaName = "backbone",
                    Interface = "ether1",
                    NetworkCidr = "10.0.0.0/30",
                    Cost = 10,
                    Type = "ptp",
                    Passive = false,
                },
            ],
            PassiveLoopbacks =
            [
                new OspfPlanLoopbackTemplate
                {
                    AreaName = "backbone",
                    NetworkCidr = "10.0.0.1/32",
                    Interface = "Loopback",
                    Passive = true,
                },
            ],
            PassiveSiteNetworks =
            [
                new OspfPlanSiteNetworkTemplate
                {
                    AreaName = "site",
                    Interfaces = "all",
                    NetworkCidr = "10.0.0.0/24",
                    Passive = true,
                },
            ],
        };
}

/// <summary>
/// Default test double: stage reports skipped (disabled) and performs no RouterOS mutations.
/// </summary>
internal sealed class PassingOspfStaticSummaryRedistributionService : IOspfStaticSummaryRedistributionService
{
    /// <summary>Returns a skipped-disabled result without touching the router.</summary>
    /// <param name="connection">Router connection (unused).</param>
    /// <param name="plan">OSPF plan (unused).</param>
    /// <param name="cancellationToken">Token (unused).</param>
    /// <returns>A completed task containing <see cref="OspfStaticSummaryRedistributionResult.SkippedDisabled"/>.</returns>
    public Task<OspfStaticSummaryRedistributionResult> ApplyAndVerifyAsync(
        ITikConnection connection,
        OspfMigrationPlan? plan,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OspfStaticSummaryRedistributionResult.SkippedDisabled());
}
