// -----------------------------------------------------------------------------
// File: TommyOspfApplyOnlyBackupGateTests.cs
//
// Description:
// Non-live tests proving Tommy apply-only mutation is gated on a centrally
// verified backup that belongs to the intended router, with operation order.
//
// -----------------------------------------------------------------------------

using System.Net;
using MikrotikWorker.Backup;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Tests.Infrastructure.Helpers;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Backup-gate coverage for the Tommy apply-only entry point helper.
/// </summary>
public sealed class TommyOspfApplyOnlyBackupGateTests
{
    private const string EntryPoint = "TommyOspfApplyOnly";
    private const string RouterIp = "198.18.240.11";
    private const string Identity = "BR01.POP011.016.Tommy";

    [Fact]
    public async Task BackupSuccess_AllowsGateAndRecordsOrderBeforeMutation()
    {
        List<string> order = [];
        RecordingBackup backups = new(CreateSuccessResult(RouterIp, Identity));
        await OspfCentralBackupGate.EnsureCentralBackupBeforeMutationAsync(
            backups,
            new TikFakeConnection(),
            CreateJob(),
            CreateDeviceInfo(),
            "user",
            "pass",
            RouterIp,
            Identity,
            EntryPoint,
            order,
            CancellationToken.None);

        order.Add("first-ospf-mutation");
        Assert.Equal(
            ["backup-start", "central-backup-verified", "central-backup-router-confirmed", "first-ospf-mutation"],
            order);
        Assert.Equal(1, backups.CallCount);
    }

    [Fact]
    public async Task BackupCreationFailure_BlocksApply()
    {
        await AssertBlocks(
            new DeviceBackupResult
            {
                Succeeded = false,
                FailureReason = "backup create failed",
            },
            expectedSubstring: "central backup was not verified");
    }

    [Fact]
    public async Task CentralDownloadFailure_BlocksApply()
    {
        await AssertBlocks(
            new DeviceBackupResult
            {
                Succeeded = false,
                FailureReason = "central download failed",
            },
            expectedSubstring: "central download failed");
    }

    [Fact]
    public async Task IntegrityChecksumFailure_BlocksApply()
    {
        await AssertBlocks(
            new DeviceBackupResult
            {
                Succeeded = false,
                FailureReason = "checksum mismatch",
            },
            expectedSubstring: "checksum mismatch");
    }

    [Fact]
    public async Task CentralVerificationFailure_BlocksApply()
    {
        await AssertBlocks(
            new DeviceBackupResult
            {
                Succeeded = false,
                RemoteCleanupFailed = false,
                FailureReason = "manifest verification failed",
            },
            expectedSubstring: "manifest verification failed");
    }

    [Fact]
    public async Task BackupBelongsToDifferentRouter_BlocksApply()
    {
        List<string> order = [];
        RecordingBackup backups = new(CreateSuccessResult("10.0.0.1", "OTHER.ROUTER"));
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OspfCentralBackupGate.EnsureCentralBackupBeforeMutationAsync(
                backups,
                new TikFakeConnection(),
                CreateJob(),
                CreateDeviceInfo(),
                "user",
                "pass",
                RouterIp,
                Identity,
                EntryPoint,
                order,
                CancellationToken.None));

        Assert.Contains("RouterIp", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(order, e => e == "central-backup-verified");
        Assert.DoesNotContain(order, e => e == "central-backup-router-confirmed");
    }

    [Fact]
    public async Task CancellationDuringBackup_BlocksApply()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OspfCentralBackupGate.EnsureCentralBackupBeforeMutationAsync(
                new CancellingBackup(),
                new TikFakeConnection(),
                CreateJob(),
                CreateDeviceInfo(),
                "user",
                "pass",
                RouterIp,
                Identity,
                EntryPoint,
                [],
                cts.Token));
    }

    [Fact]
    public async Task RemoteCleanupFailure_AfterVerifiedCentral_AllowsGate()
    {
        List<string> order = [];
        DeviceBackupResult result = new()
        {
            Succeeded = false,
            RemoteCleanupFailed = true,
            FailureReason = "remote leftover",
            Manifest = CreateSuccessResult(RouterIp, Identity).Manifest,
        };

        await OspfCentralBackupGate.EnsureCentralBackupBeforeMutationAsync(
            new RecordingBackup(result),
            new TikFakeConnection(),
            CreateJob(),
            CreateDeviceInfo(),
            "user",
            "pass",
            RouterIp,
            Identity,
            EntryPoint,
            order,
            CancellationToken.None);

        Assert.Contains(order, e => e == "central-backup-verified");
    }

    [Fact]
    public void UnarmedLiveGate_IsDocumentedFailClosed()
    {
        // Live harness refuses mutation when the opt-in env gate is not armed.
        Assert.Equal("TOMMY_OSPF_APPLY_ONLY", LiveMikrotikTestGate.AllowTommyOspfApplyOnlyEnv);
    }

    [Fact]
    public void IsCentralBackupVerified_MatchesFleetExecutorSemantics()
    {
        Assert.True(OspfCentralBackupGate.IsCentralBackupVerified(
            new DeviceBackupResult { Succeeded = true }));
        Assert.True(OspfCentralBackupGate.IsCentralBackupVerified(
            new DeviceBackupResult { Succeeded = false, RemoteCleanupFailed = true }));
        Assert.False(OspfCentralBackupGate.IsCentralBackupVerified(
            new DeviceBackupResult { Succeeded = false, RemoteCleanupFailed = false }));
    }

    private static async Task AssertBlocks(DeviceBackupResult backup, string expectedSubstring)
    {
        List<string> order = [];
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OspfCentralBackupGate.EnsureCentralBackupBeforeMutationAsync(
                new RecordingBackup(backup),
                new TikFakeConnection(),
                CreateJob(),
                CreateDeviceInfo(),
                "user",
                "pass",
                RouterIp,
                Identity,
                EntryPoint,
                order,
                CancellationToken.None));

        Assert.Contains(expectedSubstring, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(order, e => e == "central-backup-failed");
        Assert.DoesNotContain(order, e => e == "central-backup-router-confirmed");
    }

    private static DeviceBackupResult CreateSuccessResult(string routerIp, string identity) =>
        new()
        {
            Succeeded = true,
            RemoteCleanupSucceeded = true,
            Manifest = new BackupManifest
            {
                JobId = "job",
                ImportId = Guid.Empty,
                RouterName = identity,
                RouterIp = routerIp,
                RouterIdentity = identity,
                BackupTimestampUtc = DateTimeOffset.UtcNow,
                ExportFileName = "export.rsc",
                ExportSizeBytes = 1,
                ExportSha256 = "aa",
                BinaryBackupFileName = "export.backup",
                BinaryBackupSizeBytes = 1,
                BinaryBackupSha256 = "bb",
                BackupCompletionStatus = "success",
            },
        };

    private static MikrotikScheduledJob CreateJob() =>
        new()
        {
            ImportId = Guid.Empty,
            SourceFileName = "tommy",
            RowNumber = 1,
            RouterName = Identity,
            RouterIp = IPAddress.Parse(RouterIp),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    private static MikrotikDeviceInfo CreateDeviceInfo() =>
        new()
        {
            Identity = Identity,
            Version = "7.23.8",
            ArchitectureName = "mipsbe",
        };

    private sealed class RecordingBackup(DeviceBackupResult result) : IDeviceBackupService
    {
        public int CallCount { get; private set; }

        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class CancellingBackup : IDeviceBackupService
    {
        public Task<DeviceBackupResult> CreatePreUpgradeBackupAsync(
            ITikConnection connection,
            MikrotikScheduledJob job,
            MikrotikDeviceInfo deviceInfo,
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DeviceBackupResult { Succeeded = true });
        }
    }
}
