using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests;

/// <summary>
/// Test double that satisfies OSPF gates without live RouterOS OSPF inventory.
/// Workflow tests that assert package/RB behaviour use this unless OSPF-specific.
/// </summary>
internal sealed class PassingOspfMigrationService : IOspfMigrationService
{
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

    public Task<OspfApplyGateResult> ApplyAndVerifyAsync(
        ITikConnection connection,
        MikrotikScheduledJob job,
        string identity,
        OspfMigrationPlan? inMemoryPlan,
        CancellationToken cancellationToken) =>
        Task.FromResult(OspfApplyGateResult.Ok(inMemoryPlan ?? CreateMinimalValidPlan(job, identity, "7.23.8", null)));

    public void DeletePlan(MikrotikScheduledJob job)
    {
    }

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
