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
    /// <summary>
    /// Entry-point label recorded in gate telemetry for Tommy apply-only.
    /// </summary>
    private const string EntryPoint = "TommyOspfApplyOnly";

    /// <summary>
    /// Lab Tommy router IP used as the intended backup ownership identity.
    /// </summary>
    private const string RouterIp = "198.18.240.11";

    /// <summary>
    /// Lab Tommy system identity string paired with <see cref="RouterIp"/>.
    /// </summary>
    private const string Identity = "BR01.POP011.016.Tommy";

    /// <summary>
    /// Scenario: central backup succeeds for the intended router before any OSPF mutation marker.
    /// Expected: order is backup-start → central-backup-verified → router-confirmed, then the mutation marker; one backup call.
    /// Invariant: mutation may proceed only after a verified central backup confirmed for this router.
    /// </summary>
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

    /// <summary>
    /// Scenario: backup service returns Succeeded=false with a create failure reason.
    /// Expected: gate throws mentioning central backup was not verified and records central-backup-failed.
    /// Invariant: create failure blocks apply; router-confirmed is never recorded.
    /// </summary>
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

    /// <summary>
    /// Scenario: backup fails because central download failed.
    /// Expected: gate throws including the download failure text and blocks router confirmation.
    /// Invariant: central download failure is a hard apply block.
    /// </summary>
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

    /// <summary>
    /// Scenario: backup fails on integrity checksum mismatch.
    /// Expected: gate throws including checksum mismatch and blocks apply.
    /// Invariant: checksum failure is treated as unverified central backup.
    /// </summary>
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

    /// <summary>
    /// Scenario: backup fails on central manifest verification.
    /// Expected: gate throws including manifest verification failed and blocks apply.
    /// Invariant: manifest verification failure blocks mutation.
    /// </summary>
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

    /// <summary>
    /// Scenario: backup succeeds but the manifest belongs to a different router IP/identity.
    /// Expected: gate throws mentioning RouterIp after central-backup-verified; router-confirmed is absent.
    /// Invariant: a verified backup for the wrong router must not arm the apply gate.
    /// </summary>
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

    /// <summary>
    /// Scenario: cancellation token is already cancelled when the gate invokes backup.
    /// Expected: OperationCanceledException (or derived) propagates; apply does not proceed.
    /// Invariant: cancellation during backup blocks mutation rather than treating it as success.
    /// </summary>
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

    /// <summary>
    /// Scenario: Succeeded=false solely because remote cleanup failed after a verified central copy.
    /// Expected: gate still records central-backup-verified and allows progression.
    /// Invariant: remote leftover cleanup failure after verified central storage does not block apply.
    /// </summary>
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

    /// <summary>
    /// Scenario: documents the live harness environment variable name for Tommy apply-only.
    /// Expected: constant equals TOMMY_OSPF_APPLY_ONLY.
    /// Invariant: unarmed live gate remains fail-closed unless that opt-in env is set.
    /// </summary>
    [Fact]
    public void UnarmedLiveGate_IsDocumentedFailClosed()
    {
        // Live harness refuses mutation when the opt-in env gate is not armed.
        Assert.Equal("TOMMY_OSPF_APPLY_ONLY", LiveMikrotikTestGate.AllowTommyOspfApplyOnlyEnv);
    }

    /// <summary>
    /// Scenario: IsCentralBackupVerified against success, remote-cleanup-only failure, and hard failure.
    /// Expected: true for Succeeded and for RemoteCleanupFailed; false for other failures.
    /// Invariant: gate verification semantics match the fleet executor's central-backup predicate.
    /// </summary>
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

    /// <summary>
    /// Asserts the gate throws for <paramref name="backup"/>, includes <paramref name="expectedSubstring"/>,
    /// records central-backup-failed, and never records router-confirmed.
    /// </summary>
    /// <param name="backup">Backup result returned by the recording fake.</param>
    /// <param name="expectedSubstring">Substring required in the thrown InvalidOperationException message.</param>
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

    /// <summary>
    /// Builds a successful DeviceBackupResult whose manifest claims <paramref name="routerIp"/>/<paramref name="identity"/>.
    /// </summary>
    /// <param name="routerIp">Router IP embedded in the manifest.</param>
    /// <param name="identity">Router identity embedded in the manifest.</param>
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

    /// <summary>
    /// Minimal scheduled job describing the Tommy lab router for gate invocations.
    /// </summary>
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

    /// <summary>
    /// Minimal device info for Tommy (identity, RouterOS version, architecture).
    /// </summary>
    private static MikrotikDeviceInfo CreateDeviceInfo() =>
        new()
        {
            Identity = Identity,
            Version = "7.23.8",
            ArchitectureName = "mipsbe",
        };

    /// <summary>
    /// <see cref="IDeviceBackupService"/> that returns a fixed result and counts invocations.
    /// Models only CreatePreUpgradeBackupAsync; does not perform real backup I/O.
    /// </summary>
    private sealed class RecordingBackup(DeviceBackupResult result) : IDeviceBackupService
    {
        /// <summary>
        /// Number of times CreatePreUpgradeBackupAsync was invoked.
        /// </summary>
        public int CallCount { get; private set; }

        /// <summary>
        /// Increments <see cref="CallCount"/>, honours cancellation, and returns the seeded result.
        /// </summary>
        /// <param name="connection">Unused fake connection.</param>
        /// <param name="job">Unused job.</param>
        /// <param name="deviceInfo">Unused device info.</param>
        /// <param name="username">Unused credentials.</param>
        /// <param name="password">Unused credentials.</param>
        /// <param name="cancellationToken">Token checked before returning.</param>
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

    /// <summary>
    /// Backup fake that throws on cancellation before returning success; used to prove cancel blocks apply.
    /// Does not perform I/O; success path is unreachable when the token is already cancelled.
    /// </summary>
    private sealed class CancellingBackup : IDeviceBackupService
    {
        /// <summary>
        /// Throws if cancelled; otherwise returns a success result (not used by the cancel scenario).
        /// </summary>
        /// <param name="connection">Unused.</param>
        /// <param name="job">Unused.</param>
        /// <param name="deviceInfo">Unused.</param>
        /// <param name="username">Unused.</param>
        /// <param name="password">Unused.</param>
        /// <param name="cancellationToken">Token that must throw when cancelled.</param>
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
