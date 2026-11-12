using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade;
public sealed class LegacyFleetUpgradeAutomationCleanupTests
{
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

    private static LegacyFleetUpgradeAutomationCleanup CreateCleanup(IMikrotikRouterOperations ops) =>
        new(ops, NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance);

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

    private sealed class FakeOps : IMikrotikRouterOperations
    {
        public List<string> PresentScripts { get; init; } = [];

        public List<string> PresentSchedulers { get; init; } = [];

        public List<string> RemovalOrder { get; } = [];

        public int SchedulerCountCalls { get; private set; }

        public int ScriptCountCalls { get; private set; }

        public Exception? ThrowOnSchedulerRemove { get; init; }

        public Exception? ThrowOnScriptRemove { get; init; }

        public bool IgnoreRemovals { get; init; }

        public bool ThrowCanceledOnSchedulerCount { get; init; }

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

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledPackageInfo>>([new("routeros-mipsbe", "6.49.23", false)]);

        public Task ExportConfigurationToFileAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveSystemBackupAsync(
            ITikConnection connection,
            string fileNameWithoutExtension,
            string? backupPassword,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RouterFileInfo?> FindFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterFileInfo?>(null);

        public Task WaitForFileAsync(
            ITikConnection connection,
            string fileName,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveFileAsync(
            ITikConnection connection,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task FetchUrlToFileAsync(
            ITikConnection connection,
            string url,
            string destinationFileName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CountSystemScriptsByNameAsync(
            ITikConnection connection,
            string scriptName,
            CancellationToken cancellationToken = default)
        {
            ScriptCountCalls++;
            return Task.FromResult(PresentScripts.Count(n => n.Equals(scriptName, StringComparison.Ordinal)));
        }

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
