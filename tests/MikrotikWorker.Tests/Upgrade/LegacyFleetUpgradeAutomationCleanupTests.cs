// -----------------------------------------------------------------------------
// File: LegacyFleetUpgradeAutomationCleanupTests.cs
//
// Description:
// Unit tests for removal/verification of legacy FleetUpgrade script and
// scheduler objects.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade;
/// <summary>
/// Unit tests for legacy FleetUpgrade script/scheduler cleanup.
/// </summary>
public sealed class LegacyFleetUpgradeAutomationCleanupTests
{
    /// <summary>
    /// With no legacy scheduler or script present, cleanup succeeds with all found and removed counts at 0 and an
    /// empty removal order. It still queried both the scheduler and the script counts at least once.
    /// </summary>
    [Fact]
    public async Task NeitherExists_SucceedsWithoutRemovals()
    {
        FakeOps ops = new();
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.SchedulerFound);
        Assert.Equal(0, result.ScriptFound);
        Assert.Equal(0, result.SchedulerRemoved);
        Assert.Equal(0, result.ScriptRemoved);
        Assert.Empty(ops.RemovalOrder);
        Assert.True(ops.SchedulerCountCalls >= 1);
        Assert.True(ops.ScriptCountCalls >= 1);
    }

    /// <summary>
    /// With only the legacy scheduler present, cleanup succeeds, finds and removes 1 scheduler and 0 scripts, and
    /// removes only <c>scheduler:</c> plus the legacy scheduler name. The router has no schedulers left afterwards.
    /// </summary>
    [Fact]
    public async Task SchedulerOnly_RemovedAndVerified()
    {
        FakeOps ops = new()
        {
            PresentSchedulers = [LegacyFleetUpgradeObjectNames.SchedulerName],
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.SchedulerFound);
        Assert.Equal(0, result.ScriptFound);
        Assert.Equal(1, result.SchedulerRemoved);
        Assert.Equal(0, result.ScriptRemoved);
        Assert.Equal(["scheduler:" + LegacyFleetUpgradeObjectNames.SchedulerName], ops.RemovalOrder);
        Assert.Empty(ops.PresentSchedulers);
    }

    /// <summary>
    /// With only the legacy script present, cleanup succeeds, finds and removes 1 script and 0 schedulers, and
    /// removes only <c>script:</c> plus the legacy script name. The router has no scripts left afterwards.
    /// </summary>
    [Fact]
    public async Task ScriptOnly_Removed()
    {
        FakeOps ops = new()
        {
            PresentScripts = [LegacyFleetUpgradeObjectNames.ScriptName],
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.SchedulerFound);
        Assert.Equal(1, result.ScriptFound);
        Assert.Equal(0, result.SchedulerRemoved);
        Assert.Equal(1, result.ScriptRemoved);
        Assert.Equal(["script:" + LegacyFleetUpgradeObjectNames.ScriptName], ops.RemovalOrder);
        Assert.Empty(ops.PresentScripts);
    }

    /// <summary>
    /// When both objects exist, cleanup succeeds and the removal order is the scheduler first, then the script, so
    /// the scheduler cannot fire a script that is already gone. Neither object remains afterwards.
    /// </summary>
    [Fact]
    public async Task Both_RemovesSchedulerBeforeScript()
    {
        FakeOps ops = new()
        {
            PresentSchedulers = [LegacyFleetUpgradeObjectNames.SchedulerName],
            PresentScripts = [LegacyFleetUpgradeObjectNames.ScriptName],
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                "scheduler:" + LegacyFleetUpgradeObjectNames.SchedulerName,
                "script:" + LegacyFleetUpgradeObjectNames.ScriptName,
            ],
            ops.RemovalOrder);
        Assert.Empty(ops.PresentSchedulers);
        Assert.Empty(ops.PresentScripts);
    }

    /// <summary>
    /// Two schedulers and three scripts with the exact legacy names are all found and removed, and none remain.
    /// </summary>
    [Fact]
    public async Task DuplicateExactNames_AllRemoved()
    {
        FakeOps ops = new()
        {
            PresentSchedulers =
            [
                LegacyFleetUpgradeObjectNames.SchedulerName,
                LegacyFleetUpgradeObjectNames.SchedulerName,
            ],
            PresentScripts =
            [
                LegacyFleetUpgradeObjectNames.ScriptName,
                LegacyFleetUpgradeObjectNames.ScriptName,
                LegacyFleetUpgradeObjectNames.ScriptName,
            ],
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.SchedulerFound);
        Assert.Equal(3, result.ScriptFound);
        Assert.Empty(ops.PresentSchedulers);
        Assert.Empty(ops.PresentScripts);
    }

    /// <summary>
    /// Cleanup succeeds but leaves the scheduler <c>FleetUpgrade-startup-old</c> and the script
    /// <c>FleetUpgrade2</c> in place: only exact name matches are removed.
    /// </summary>
    [Fact]
    public async Task SimilarlyNamedUnrelated_Preserved()
    {
        FakeOps ops = new()
        {
            PresentSchedulers =
            [
                LegacyFleetUpgradeObjectNames.SchedulerName,
                "FleetUpgrade-startup-old",
            ],
            PresentScripts =
            [
                LegacyFleetUpgradeObjectNames.ScriptName,
                "FleetUpgrade2",
            ],
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["FleetUpgrade-startup-old"], ops.PresentSchedulers);
        Assert.Equal(["FleetUpgrade2"], ops.PresentScripts);
    }

    /// <summary>
    /// If removing the scheduler throws, cleanup does not succeed. The failure reason mentions 'scheduler' and the
    /// underlying 'scheduler remove boom' message, and the failed removal is not recorded in the removal order.
    /// </summary>
    [Fact]
    public async Task SchedulerRemoveThrows_Fails()
    {
        FakeOps ops = new()
        {
            PresentSchedulers = [LegacyFleetUpgradeObjectNames.SchedulerName],
            ThrowOnSchedulerRemove = new InvalidOperationException("scheduler remove boom"),
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("scheduler", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scheduler remove boom", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(ops.RemovalOrder);
    }

    /// <summary>
    /// If removing the script throws, cleanup does not succeed. The failure reason mentions 'script' and the
    /// underlying 'script remove boom' message, and the failed removal is not recorded in the removal order.
    /// </summary>
    [Fact]
    public async Task ScriptRemoveThrows_Fails()
    {
        FakeOps ops = new()
        {
            PresentScripts = [LegacyFleetUpgradeObjectNames.ScriptName],
            ThrowOnScriptRemove = new InvalidOperationException("script remove boom"),
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("script", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("script remove boom", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(ops.RemovalOrder);
    }

    /// <summary>
    /// If the scheduler is still listed after its removal call, cleanup fails with a reason containing 'remain'.
    /// The removal was attempted, and the legacy scheduler is still present.
    /// </summary>
    [Fact]
    public async Task PostVerifyStillFindsObject_Fails()
    {
        FakeOps ops = new()
        {
            PresentSchedulers = [LegacyFleetUpgradeObjectNames.SchedulerName],
            IgnoreRemovals = true,
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("remain", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["scheduler:" + LegacyFleetUpgradeObjectNames.SchedulerName], ops.RemovalOrder);
        Assert.Contains(LegacyFleetUpgradeObjectNames.SchedulerName, ops.PresentSchedulers);
    }

    /// <summary>
    /// A second cleanup run after a successful first run also succeeds, finds and removes nothing, and issues no
    /// further removals.
    /// </summary>
    [Fact]
    public async Task IdempotentSecondCleanup_SucceedsWithZeroRemovals()
    {
        FakeOps ops = new()
        {
            PresentSchedulers = [LegacyFleetUpgradeObjectNames.SchedulerName],
            PresentScripts = [LegacyFleetUpgradeObjectNames.ScriptName],
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);

        LegacyFleetUpgradeCleanupResult first = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);
        ops.RemovalOrder.Clear();

        LegacyFleetUpgradeCleanupResult second = await cleanup.CleanupAsync(
            FakeMikrotikConnections.CreateFullyPopulated(),
            CreateJob(),
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(0, second.SchedulerFound);
        Assert.Equal(0, second.ScriptFound);
        Assert.Equal(0, second.SchedulerRemoved);
        Assert.Equal(0, second.ScriptRemoved);
        Assert.Empty(ops.RemovalOrder);
    }

    /// <summary>
    /// A cancellation raised while counting schedulers must propagate as <c>OperationCanceledException</c> instead
    /// of being turned into a result, and no removals are issued.
    /// </summary>
    [Fact]
    public async Task CancelledToken_PropagatesWithoutSuccess()
    {
        FakeOps ops = new()
        {
            PresentSchedulers = [LegacyFleetUpgradeObjectNames.SchedulerName],
            ThrowCanceledOnSchedulerCount = true,
        };
        LegacyFleetUpgradeAutomationCleanup cleanup = CreateCleanup(ops);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cleanup.CleanupAsync(
                FakeMikrotikConnections.CreateFullyPopulated(),
                CreateJob(),
                cts.Token));

        Assert.Empty(ops.RemovalOrder);
    }

    /// <summary>
    /// Creates the production <see cref="LegacyFleetUpgradeAutomationCleanup"/> over the supplied router operations
    /// with a null logger.
    /// </summary>
    private static LegacyFleetUpgradeAutomationCleanup CreateCleanup(IMikrotikRouterOperations ops) =>
        new(ops, NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance);

    /// <summary>Builds a minimal scheduled job (router <c>R1</c>, 10.0.0.50) for cleanup calls.</summary>
    private static MikrotikScheduledJob CreateJob() =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "R1",
            RouterIp = IPAddress.Parse("10.0.0.50"),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
        };

    /// <summary>
    /// Router operations fake that stores legacy scripts and schedulers by name and supports failure, cancellation
    /// and "removal ignored" modes for cleanup scenarios.
    /// </summary>
    private sealed class FakeOps : IMikrotikRouterOperations
    {
        /// <summary>Names of legacy fleet-upgrade scripts currently present on the fake router.</summary>
        public List<string> PresentScripts { get; init; } = [];

        /// <summary>Names of legacy fleet-upgrade schedulers currently present on the fake router.</summary>
        public List<string> PresentSchedulers { get; init; } = [];

        /// <summary>
        /// Ordered log of removals (<c>script:name</c> / <c>scheduler:name</c>) used to assert cleanup order.
        /// </summary>
        public List<string> RemovalOrder { get; } = [];

        /// <summary>
        /// Number of scheduler inspections performed; zero proves cleanup never looked for schedulers.
        /// </summary>
        public int SchedulerCountCalls { get; private set; }

        /// <summary>Number of script inspections performed; zero proves cleanup never looked for scripts.</summary>
        public int ScriptCountCalls { get; private set; }

        /// <summary>
        /// When set, <see cref="RemoveSchedulersByNameAsync"/> throws this exception instead of removing the
        /// scheduler.
        /// </summary>
        public Exception? ThrowOnSchedulerRemove { get; init; }

        /// <summary>
        /// When set, <see cref="RemoveSystemScriptsByNameAsync"/> throws this exception instead of removing the
        /// script.
        /// </summary>
        public Exception? ThrowOnScriptRemove { get; init; }

        /// <summary>
        /// When true, removals are recorded but the items stay present, simulating a router that fails to delete
        /// them.
        /// </summary>
        public bool IgnoreRemovals { get; init; }

        /// <summary>
        /// When true, counting schedulers throws <see cref="OperationCanceledException"/> to simulate cancellation
        /// mid-cleanup.
        /// </summary>
        public bool ThrowCanceledOnSchedulerCount { get; init; }

        /// <summary>Returns RouterOS 6.49.23 device info for <c>AP01.POP001.001.access</c>.</summary>
        public Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MikrotikDeviceInfo
            {
                Identity = "AP01.POP001.001.access",
                Version = "6.49.23",
                ArchitectureName = "mipsbe",
                BoardName = "hEX",
            });

        /// <summary>Returns a single enabled <c>routeros-mipsbe</c> 6.49.23 package.</summary>
        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([new("routeros-mipsbe", "6.49.23", false)]);

        /// <summary>No-op: pretends the <c>.rsc</c> configuration export was written on the router.</summary>
        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>No-op: pretends the binary <c>.backup</c> system backup was saved on the router.</summary>
        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>Always reports the file as missing.</summary>
        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(null);

        /// <summary>
        /// No-op: files are reported ready immediately, so the executor never waits on backup-file polling.
        /// </summary>
        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>No-op: pretends the file was deleted from the router.</summary>
        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>No-op: firmware fetches are not part of cleanup.</summary>
        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>No-op: reboots are not part of cleanup.</summary>
        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        /// <summary>
        /// Counts the call and returns how many entries in <see cref="PresentScripts"/> match the name exactly.
        /// </summary>
        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default)
        {
            ScriptCountCalls++;
            return Task.FromResult(PresentScripts.Count(n => n.Equals(scriptName, StringComparison.Ordinal)));
        }

        /// <summary>
        /// Throws <see cref="ThrowOnScriptRemove"/> when configured; otherwise records <c>script:name</c> in <see
        /// cref="RemovalOrder"/> and, unless <see cref="IgnoreRemovals"/> is set, removes the entries.
        /// </summary>
        public Task RemoveSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnScriptRemove is not null)
            {
                throw ThrowOnScriptRemove;
            }

            RemovalOrder.Add("script:" + scriptName);
            if (!IgnoreRemovals)
            {
                PresentScripts.RemoveAll(n => n.Equals(scriptName, StringComparison.Ordinal));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Counts the call, optionally throws cancellation (<see cref="ThrowCanceledOnSchedulerCount"/>), and returns
        /// how many entries in <see cref="PresentSchedulers"/> match the name exactly.
        /// </summary>
        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default)
        {
            SchedulerCountCalls++;
            if (ThrowCanceledOnSchedulerCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.FromResult(PresentSchedulers.Count(n => n.Equals(schedulerName, StringComparison.Ordinal)));
        }

        /// <summary>
        /// Throws <see cref="ThrowOnSchedulerRemove"/> when configured; otherwise records <c>scheduler:name</c> in
        /// <see cref="RemovalOrder"/> and, unless <see cref="IgnoreRemovals"/> is set, removes the entries.
        /// </summary>
        public Task RemoveSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnSchedulerRemove is not null)
            {
                throw ThrowOnSchedulerRemove;
            }

            RemovalOrder.Add("scheduler:" + schedulerName);
            if (!IgnoreRemovals)
            {
                PresentSchedulers.RemoveAll(n => n.Equals(schedulerName, StringComparison.Ordinal));
            }

            return Task.CompletedTask;
        }
    }
}
