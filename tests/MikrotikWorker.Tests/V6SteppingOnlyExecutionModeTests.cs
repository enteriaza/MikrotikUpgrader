using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests;

/// <summary>
/// Non-live coverage for per-job <see cref="UpgradeExecutionMode.V6SteppingOnly"/>.
/// Includes a synthetic lab fixture for 198.18.240.11 — not a live router result.
/// </summary>
public sealed class V6SteppingOnlyExecutionModeTests
{
    private static readonly string[] LabBundledComponents =
    [
        "system",
        "wireless",
        "hotspot",
        "dhcp",
        "routing",
        "mpls",
        "ppp",
        "security",
        "advanced-tools",
        "ipv6",
    ];

    [Theory]
    [InlineData("Mikrotik", DeviceType.Unknown)]
    [InlineData(" ", DeviceType.Unknown)]
    [InlineData("AP01.POP001.001.access", DeviceType.Ap)]
    [InlineData("DCL12.POP123.456.some.device", DeviceType.Dcl)]
    [InlineData("ACE01.POP001.001.core", DeviceType.Ace)]
    [InlineData("BR12.POP123.456.edge", DeviceType.Br)]
    public async Task V6SteppingOnly_EligibleOrExcludedIdentity_StopsAfterV6(
        string identity,
        DeviceType expectedType)
    {
        string effectiveIdentity = string.IsNullOrWhiteSpace(identity) ? " " : identity;
        StageTrackingOps ops = new(effectiveIdentity)
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        string root = packages.FirmwareRoot;
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: effectiveIdentity,
                version: "6.49.23",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(ops, backup, root, reconnect, packages, v7);

        CommandRecordingConnection recording = new(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: effectiveIdentity,
                version: "6.49.19",
                architecture: "mipsbe"));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            recording,
            CreateJob(UpgradeExecutionMode.V6SteppingOnly),
            CreateDevice(effectiveIdentity, "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
        Assert.Equal(UpgradeExecutionMode.V6SteppingOnly, result.ExecutionMode);
        Assert.Equal(UpgradeCompletedStages.V6Stepping, result.CompletedStage);
        Assert.Equal("6.49.23", result.PostUpgradeVersion);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.Equal(0, v7.CaptureCount);
        Assert.True(ops.SchedulerCountCalls > 0);
        Assert.All(packages.ResolvedTargets, t => Assert.True(t.IsV6));
        Assert.DoesNotContain(packages.ResolvedTargets, static t => t.IsV7OrLater);
        AssertNoV7Mutation(ops);
        AssertNoRouterboardUpgrade(recording);
        Assert.DoesNotContain(
            ops.FetchedFiles,
            f => f.Equals("FleetUpgradeInstall.rsc", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expectedType, DeviceIdentityClassifier.Classify(identity).DeviceType);
    }

    [Fact]
    public async Task SyntheticLabFixture_198_18_240_11_V6SteppingOnly_SucceedsWithoutV7()
    {
        // SyntheticFixture only — not a live router observation or live upgrade result.
        const string identity = "Mikrotik";
        const string routerIp = "198.18.240.11";
        StageTrackingOps ops = new(identity)
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23"],
            IncludeLabBundledInventory = true,
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: identity,
                version: "6.49.23",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            v7);

        CommandRecordingConnection recording = new(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: identity,
                version: "6.49.19",
                architecture: "mipsbe"));

        MikrotikScheduledJob job = new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "lab-v6-only-synthetic.csv",
            RowNumber = 2,
            RouterName = identity,
            RouterIp = IPAddress.Parse(routerIp),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
            ExecutionMode = UpgradeExecutionMode.V6SteppingOnly,
        };

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            recording,
            job,
            new MikrotikDeviceInfo
            {
                Identity = identity,
                Version = "6.49.19",
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
            },
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
        Assert.Equal(UpgradeExecutionMode.V6SteppingOnly, result.ExecutionMode);
        Assert.Equal(UpgradeCompletedStages.V6Stepping, result.CompletedStage);
        Assert.Equal("6.49.23", result.PostUpgradeVersion);
        Assert.Equal("mipsbe", result.PostUpgradeArchitecture);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.Single(ops.FetchedFiles);
        Assert.Contains("routeros-mipsbe-6.49.23.npk", ops.FetchedFiles[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, ops.LastBundledComponentCount);
        Assert.Equal(0, v7.CaptureCount);
        Assert.Single(packages.ResolvedTargets);
        Assert.Equal(RouterOsUpgradeVersions.V6Stepping, packages.ResolvedTargets[0]);
        Assert.DoesNotContain(packages.ResolvedTargets, static t => t.IsV7OrLater);
        AssertNoV7Mutation(ops);
        AssertNoRouterboardUpgrade(recording);
    }

    [Theory]
    [InlineData("6.49.23")]
    [InlineData("6.49.24")]
    public async Task V6SteppingOnly_AlreadyAtOrAboveStepping_DoesNotClaimCompletedStage(string version)
    {
        StageTrackingOps ops = new("Mikrotik") { InitialVersion = version };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            packageResolver: packages,
            v7TargetProvider: v7);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: version,
                architecture: "mipsbe"),
            CreateJob(UpgradeExecutionMode.V6SteppingOnly),
            CreateDevice("Mikrotik", version),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
        Assert.Equal(UpgradeExecutionMode.V6SteppingOnly, result.ExecutionMode);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Equal(version, result.PostUpgradeVersion);
        Assert.Equal("mipsbe", result.PostUpgradeArchitecture);
        Assert.Equal(0, backup.CallCount);
        Assert.Equal(0, ops.RebootCount);
        Assert.Empty(ops.FetchedFiles);
        Assert.Empty(packages.ResolvedTargets);
        Assert.Equal(0, v7.CaptureCount);
        AssertNoV7Mutation(ops);
    }

    [Fact]
    public async Task V6SteppingOnly_RouterOs7Device_IsSkippedStageNotApplicable()
    {
        StageTrackingOps ops = new("AP01.POP001.001.access") { InitialVersion = "7.23.8" };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            packageResolver: packages,
            v7TargetProvider: v7);

        CommandRecordingConnection recording = new(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "AP01.POP001.001.access",
                version: "7.23.8",
                architecture: "mipsbe"));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            recording,
            CreateJob(UpgradeExecutionMode.V6SteppingOnly),
            CreateDevice("AP01.POP001.001.access", "7.23.8"),
            CreateAssessment(DeviceAssessmentStatus.ReadyForUpgrade),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.SkippedStageNotApplicable, result.Outcome);
        Assert.Equal(UpgradeExecutionMode.V6SteppingOnly, result.ExecutionMode);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal("mipsbe", result.PostUpgradeArchitecture);
        Assert.Contains("not applicable", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backup.CallCount);
        Assert.Equal(0, ops.RebootCount);
        Assert.Empty(ops.FetchedFiles);
        Assert.Empty(packages.ResolvedTargets);
        Assert.Equal(0, v7.CaptureCount);
        AssertNoV7Mutation(ops);
        AssertNoRouterboardUpgrade(recording);
    }

    [Fact]
    public async Task V6SteppingOnly_VerificationFailure_DoesNotClaimSuccessOrCompletedStage()
    {
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.48.1"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(identity: "Mikrotik", architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            v7,
            rebootTimeout: TimeSpan.FromSeconds(5),
            postUpgradeVerifyTimeout: TimeSpan.FromMilliseconds(80));

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(UpgradeExecutionMode.V6SteppingOnly),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeVersionMismatch, result.Outcome);
        Assert.NotEqual(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Equal(UpgradeExecutionMode.V6SteppingOnly, result.ExecutionMode);
        Assert.Equal(1, backup.CallCount);
        Assert.Equal(1, ops.RebootCount);
        Assert.Equal(0, v7.CaptureCount);
        Assert.DoesNotContain(packages.ResolvedTargets, static t => t.IsV7OrLater);
        AssertNoV7Mutation(ops);
    }

    [Fact]
    public async Task V6SteppingOnly_RebootTimeout_PreservesTimeoutOutcome()
    {
        FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingThrowFactory reconnect = new();
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            rebootTimeout: TimeSpan.FromMilliseconds(40),
            postUpgradeVerifyTimeout: TimeSpan.FromMilliseconds(40),
            timeProvider: time);

        Task<UpgradeExecutionResult> running = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(UpgradeExecutionMode.V6SteppingOnly),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        await WaitForAsync(() => ops.RebootCount >= 1, TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromMilliseconds(80));
        UpgradeExecutionResult result = await running;

        Assert.Equal(UpgradeOutcome.UpgradeTimedOut, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Equal(UpgradeExecutionMode.V6SteppingOnly, result.ExecutionMode);
        AssertNoV7Mutation(ops);
    }

    [Fact]
    public async Task V6SteppingOnly_Cancellation_PreservesInterruptedOutcome()
    {
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23"],
            HangPostRebootReadUntilCancelled = true,
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.23",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            rebootTimeout: TimeSpan.FromSeconds(30),
            postUpgradeVerifyTimeout: TimeSpan.FromSeconds(30));

        using CancellationTokenSource cts = new();
        Task<UpgradeExecutionResult> running = executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(UpgradeExecutionMode.V6SteppingOnly),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            cts.Token);

        await WaitForAsync(() => ops.PostRebootReadStarted.Task.IsCompleted, TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        UpgradeExecutionResult result = await running;

        Assert.Equal(UpgradeOutcome.UpgradeInterrupted, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        AssertNoV7Mutation(ops);
    }

    [Fact]
    public async Task FullUpgrade_UnknownIdentity_StillRunsTwoStage()
    {
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.23",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            v7);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(UpgradeExecutionMode.FullUpgrade),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeExecutionMode.FullUpgrade, result.ExecutionMode);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
        Assert.Equal(1, v7.CaptureCount);
        Assert.True(ops.SchedulerCountCalls > 0);
        Assert.Contains(packages.ResolvedTargets, static t => t.IsV6);
        Assert.Contains(packages.ResolvedTargets, static t => t.IsV7OrLater);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(
            ops.FetchedFiles,
            f => f.Equals("FleetUpgradeInstall.rsc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task V6SteppingOnly_LegacyCleanupFailure_SkipsFirmwareMutation()
    {
        StageTrackingOps ops = new("Mikrotik")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23"],
            PresentSchedulers = [LegacyFleetUpgradeObjectNames.SchedulerName],
            ThrowOnSchedulerRemove = new InvalidOperationException("cleanup boom"),
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            packageResolver: packages,
            v7TargetProvider: v7);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "Mikrotik",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(UpgradeExecutionMode.V6SteppingOnly),
            CreateDevice("Mikrotik", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.LegacyFirmwareAutomationCleanupFailed, result.Outcome);
        Assert.Equal(UpgradeCompletedStages.None, result.CompletedStage);
        Assert.Equal(0, ops.RebootCount);
        Assert.Equal(0, backup.CallCount);
        Assert.Empty(ops.FetchedFiles);
        Assert.Empty(packages.ResolvedTargets);
        Assert.True(ops.SchedulerCountCalls > 0);
    }

    [Fact]
    public async Task FullUpgrade_Ace_ProceedsToV7_WithoutStageCap()
    {
        StageTrackingOps ops = new("ACE01.POP001.001.core")
        {
            InitialVersion = "6.49.19",
            VersionsAfterReboot = ["6.49.23", "7.23.8"],
        };
        CountingBackup backup = new();
        CountingPackageResolver packages = CreateCountingResolver();
        CountingV7TargetProvider v7 = new("7.23.8");
        ScriptedMikrotikConnectionFactory reconnect = new(_ =>
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "ACE01.POP001.001.core",
                version: "6.49.23",
                architecture: "mipsbe"));
        RouterOsV6UpgradeExecutor executor = CreateExecutor(
            ops,
            backup,
            packages.FirmwareRoot,
            reconnect,
            packages,
            v7);

        UpgradeExecutionResult result = await executor.ExecuteAsync(
            FakeMikrotikConnections.CreateFullyPopulated(
                identity: "ACE01.POP001.001.core",
                version: "6.49.19",
                architecture: "mipsbe"),
            CreateJob(UpgradeExecutionMode.FullUpgrade),
            CreateDevice("ACE01.POP001.001.core", "6.49.19"),
            CreateAssessment(),
            "admin",
            "secret",
            static () => true,
            CancellationToken.None);

        Assert.Equal(UpgradeOutcome.UpgradeSucceeded, result.Outcome);
        Assert.Equal(UpgradeExecutionMode.FullUpgrade, result.ExecutionMode);
        Assert.Equal(UpgradeCompletedStages.RouterboardFirmware, result.CompletedStage);
        Assert.Equal("7.23.8", result.PostUpgradeVersion);
        Assert.Equal(3, backup.CallCount);
        Assert.Equal(2, ops.RebootCount);
        Assert.True(v7.CaptureCount >= 1);
        Assert.Contains(packages.ResolvedTargets, static t => t.IsV7OrLater);
        Assert.Contains(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecutionMode_SurvivesCsvImportIntoExecutor()
    {
        string path = Path.Combine(Path.GetTempPath(), $"v6only-import-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(
            path,
            """
            datestart,datestop,routername,routerip,executionmode
            01/12/2026 08:00:00,02/12/2026 18:00:00,Mikrotik,198.18.240.11,V6SteppingOnly
            """);

        try
        {
            ScheduledJobCsvParser parser = new(
                Options.Create(new ScheduledJobsOptions()),
                new CsvDateTimeParser(Options.Create(new CsvOptions
                {
                    Culture = "en-ZA",
                    TimeZoneId = "Africa/Johannesburg",
                    DateTimeFormats =
                    [
                        "dd/MM/yyyy HH:mm:ss",
                        "dd/MM/yyyy HH:mm",
                    ],
                })));

            Guid importId = Guid.NewGuid();
            MikrotikScheduledJob? accepted = null;
            await foreach (MikrotikScheduledJob job in parser.EnumerateJobsAsync(path, importId, CancellationToken.None))
            {
                accepted = job;
            }

            Assert.NotNull(accepted);
            Assert.Equal(UpgradeExecutionMode.V6SteppingOnly, accepted.ExecutionMode);

            CountingPackageResolver packages = CreateCountingResolver();
            CountingV7TargetProvider v7 = new("7.23.8");
            StageTrackingOps ops = new("Mikrotik")
            {
                InitialVersion = "6.49.19",
                VersionsAfterReboot = ["6.49.23"],
            };
            CountingBackup backup = new();
            ScriptedMikrotikConnectionFactory reconnect = new(_ =>
                FakeMikrotikConnections.CreateFullyPopulated(
                    identity: "Mikrotik",
                    version: "6.49.23",
                    architecture: "mipsbe"));
            RouterOsV6UpgradeExecutor executor = CreateExecutor(
                ops,
                backup,
                packages.FirmwareRoot,
                reconnect,
                packages,
                v7);

            MikrotikScheduledJob executable = new()
            {
                ImportId = accepted.ImportId,
                SourceFileName = accepted.SourceFileName,
                RowNumber = accepted.RowNumber,
                RouterName = accepted.RouterName,
                RouterIp = accepted.RouterIp,
                DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
                DateStopUtc = DateTime.UtcNow.AddHours(1),
                ExecutionMode = accepted.ExecutionMode,
            };

            UpgradeExecutionResult result = await executor.ExecuteAsync(
                FakeMikrotikConnections.CreateFullyPopulated(
                    identity: "Mikrotik",
                    version: "6.49.19",
                    architecture: "mipsbe"),
                executable,
                CreateDevice("Mikrotik", "6.49.19"),
                CreateAssessment(),
                "admin",
                "secret",
                static () => true,
                CancellationToken.None);

            Assert.Equal(UpgradeOutcome.UpgradeSucceededV6Only, result.Outcome);
            Assert.Equal(UpgradeCompletedStages.V6Stepping, result.CompletedStage);
            Assert.Equal(0, v7.CaptureCount);
            Assert.DoesNotContain(packages.ResolvedTargets, static t => t.IsV7OrLater);
            AssertNoV7Mutation(ops);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertNoV7Mutation(StageTrackingOps ops)
    {
        Assert.DoesNotContain(ops.FetchedFiles, f => f.Contains("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(ops.FetchedUrls, u => u.Contains("7.23.8", StringComparison.Ordinal));
        Assert.True(ops.RebootCount <= 1);
    }

    private static void AssertNoRouterboardUpgrade(CommandRecordingConnection recording) =>
        Assert.DoesNotContain(
            recording.Commands,
            static c => c.Contains("/system/routerboard/upgrade", StringComparison.OrdinalIgnoreCase));

    private static CountingPackageResolver CreateCountingResolver()
    {
        string root = Directory.CreateTempSubdirectory("v6only-fw-").FullName;
        File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(root, "routeros-7.23.8-mipsbe.npk"), [5, 6, 7, 8]);
        return new CountingPackageResolver(root);
    }

    private static RouterOsV6UpgradeExecutor CreateExecutor(
        StageTrackingOps ops,
        IDeviceBackupService backup,
        string firmwareRoot,
        IMikrotikConnectionFactory? connectionFactory = null,
        IUpgradePackageResolver? packageResolver = null,
        IRouterOsV7TargetProvider? v7TargetProvider = null,
        TimeSpan? rebootTimeout = null,
        TimeSpan? postUpgradeVerifyTimeout = null,
        TimeProvider? timeProvider = null)
    {
        TimeSpan reboot = rebootTimeout ?? TimeSpan.FromSeconds(5);
        TimeSpan verify = postUpgradeVerifyTimeout ?? reboot;
        if (verify > reboot)
        {
            verify = reboot;
        }

        IOptions<FirmwareFilesOptions> firmwareFilesOptions =
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot });
        IUpgradePackageResolver resolver = packageResolver
            ?? new UpgradePackageResolver(
                new StaticFirmwarePackageCatalog(firmwareRoot),
                firmwareFilesOptions);
        IMikrotikConnectionFactory factory =
            connectionFactory ?? new ThrowingMikrotikConnectionFactory(new IOException("no reconnect"));
        IOptions<FirmwareUpgradeOptions> upgradeOptions = Options.Create(new FirmwareUpgradeOptions
        {
            Enabled = true,
            AdvertisedBaseUrl = "http://198.18.0.66:8080/firmware_files",
            FetchTimeout = TimeSpan.FromSeconds(5),
            ReconnectInterval = TimeSpan.FromMilliseconds(10),
            RebootTimeout = reboot,
            PostUpgradeVerifyTimeout = verify,
            BackupFileReadyTimeout = TimeSpan.FromMilliseconds(100),
            BackupFileReadyPollInterval = TimeSpan.FromMilliseconds(20),
        });
        IOptions<MikroTikOptions> mikrotikOptions = Options.Create(new MikroTikOptions { Port = 8728 });
        TimeProvider clock = timeProvider ?? TimeProvider.System;

        return new RouterOsV6UpgradeExecutor(
            ops,
            factory,
            resolver,
            backup,
            v7TargetProvider ?? new FixedRouterOsV7TargetProvider("7.23.8"),
            new LegacyFleetUpgradeAutomationCleanup(
                ops,
                NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance),
            new RouterboardFirmwareUpgradeService(
                ops,
                factory,
                upgradeOptions,
                mikrotikOptions,
                clock,
                NullLogger<RouterboardFirmwareUpgradeService>.Instance),
            new PassingOspfMigrationService(),
            upgradeOptions,
            mikrotikOptions,
            clock,
            NullLogger<RouterOsV6UpgradeExecutor>.Instance);
    }

    private static MikrotikScheduledJob CreateJob(UpgradeExecutionMode mode) =>
        new()
        {
            ImportId = Guid.NewGuid(),
            SourceFileName = "a.csv",
            RowNumber = 2,
            RouterName = "R1",
            RouterIp = IPAddress.Parse("10.0.0.50"),
            DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
            DateStopUtc = DateTime.UtcNow.AddHours(1),
            ExecutionMode = mode,
        };

    private static MikrotikDeviceInfo CreateDevice(string identity, string version) =>
        new()
        {
            Identity = identity,
            Version = version,
            ArchitectureName = "mipsbe",
            BoardName = "hEX PoE",
        };

    private static DeviceCompatibilityAssessment CreateAssessment(
        DeviceAssessmentStatus status = DeviceAssessmentStatus.ReadyForUpgrade) =>
        new()
        {
            RouterIp = "10.0.0.50",
            OverallStatus = status,
            RouterOsVersionClassification = RouterOsVersionClassification.OlderThanTargetV6,
            TargetRouterOsVersion = "6.49.23",
            PackageCompatibility = PackageCompatibilityStatus.Compatible,
            UpgradePath = UpgradePathStatus.EligibleSubjectToManualPathVerification,
            RouterboardAssessment = RouterboardFirmwareStatus.Unavailable,
            AssessedAtUtc = DateTimeOffset.UtcNow,
        };

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private sealed class CountingBackup : IDeviceBackupService
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
            return Task.FromResult(new DeviceBackupResult
            {
                Succeeded = true,
                RemoteCleanupSucceeded = true,
            });
        }
    }

    private sealed class CountingPackageResolver(string firmwareRoot) : IUpgradePackageResolver
    {
        private readonly UpgradePackageResolver _inner = new(
            new StaticFirmwarePackageCatalog(firmwareRoot),
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot }));

        public string FirmwareRoot { get; } = firmwareRoot;

        public List<RouterOsVersion> ResolvedTargets { get; } = [];

        public UpgradePackageResolution Resolve(
            string? deviceArchitecture,
            IReadOnlyList<InstalledPackageInfo> installedPackages,
            RouterOsVersion targetVersion)
        {
            ResolvedTargets.Add(targetVersion);
            return _inner.Resolve(deviceArchitecture, installedPackages, targetVersion);
        }
    }

    private sealed class CountingV7TargetProvider(string? versionText) : IRouterOsV7TargetProvider
    {
        private readonly FixedRouterOsV7TargetProvider _inner = new(versionText);

        public int CaptureCount { get; private set; }

        public string VersionFilePath => _inner.VersionFilePath;

        public RouterOsV7TargetSnapshot? TryGetCurrent() => _inner.TryGetCurrent();

        public RouterOsV7TargetSnapshot? CaptureSnapshot()
        {
            CaptureCount++;
            return _inner.CaptureSnapshot();
        }
    }

    private sealed class CountingThrowFactory : IMikrotikConnectionFactory
    {
        public int Attempts { get; private set; }

        public Task<ITikConnection> CreateAsync(
            MikrotikConnectionOptions connection,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new IOException("still down");
        }
    }

    private sealed class StageTrackingOps(string identity) : IMikrotikRouterOperations
    {
        private int _rebootIndex;

        public string Identity { get; } = identity;

        public string InitialVersion { get; init; } = "6.49.19";

        public List<string> VersionsAfterReboot { get; init; } = [];

        public bool IncludeLabBundledInventory { get; init; }

        public bool HangPostRebootReadUntilCancelled { get; init; }

        public TaskCompletionSource PostRebootReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string RouterboardCurrentFirmware { get; init; } = "7.23.8";

        public string RouterboardUpgradeFirmware { get; init; } = "7.23.8";

        public List<string> PresentScripts { get; init; } = [];

        public List<string> PresentSchedulers { get; init; } = [];

        public List<string> RemovalOrder { get; } = [];

        public Exception? ThrowOnSchedulerRemove { get; init; }

        public Exception? ThrowOnScriptRemove { get; init; }

        public int RebootCount { get; private set; }

        public int SchedulerCountCalls { get; private set; }

        public int ScriptCountCalls { get; private set; }

        public int CleanupInspectionCount => SchedulerCountCalls + ScriptCountCalls;

        public int LastBundledComponentCount { get; private set; }

        public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> FetchedFiles { get; } = [];

        public List<string> FetchedUrls { get; } = [];

        private string CurrentVersion =>
            RebootCount == 0
                ? InitialVersion
                : VersionsAfterReboot[Math.Min(_rebootIndex - 1, VersionsAfterReboot.Count - 1)];

        public async Task<MikrotikDeviceInfo> ReadDeviceInfoAsync(
            ITikConnection connection,
            string host,
            CancellationToken cancellationToken = default)
        {
            if (RebootCount >= 1 && HangPostRebootReadUntilCancelled)
            {
                PostRebootReadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new MikrotikDeviceInfo
            {
                Identity = Identity,
                Version = CurrentVersion,
                ArchitectureName = "mipsbe",
                BoardName = "hEX PoE",
                RouterboardCurrentFirmware = RouterboardCurrentFirmware,
                RouterboardUpgradeFirmware = RouterboardUpgradeFirmware,
            };
        }

        public Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledPackagesAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default)
        {
            string version = CurrentVersion;
            string numeric = RouterOsVersion.TryParse(version, out RouterOsVersion parsed)
                ? parsed.ToString()
                : version;

            bool isV7 = RouterOsVersion.TryParse(version, out RouterOsVersion ver) && ver.IsV7OrLater;
            string mainName = isV7 ? "routeros" : "routeros-mipsbe";
            List<InstalledPackageInfo> packages =
            [
                new(mainName, numeric, false),
            ];

            if (IncludeLabBundledInventory && !isV7)
            {
                foreach (string component in LabBundledComponents)
                {
                    packages.Add(new InstalledPackageInfo(component, numeric, false, "routeros-mipsbe"));
                }

                LastBundledComponentCount = LabBundledComponents.Length;
            }
            else
            {
                LastBundledComponentCount = 0;
            }

            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>(packages);
        }

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
            CancellationToken cancellationToken = default)
        {
            if (FileSizes.TryGetValue(fileName, out long known))
            {
                return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, known));
            }

            return Task.FromResult<RouterFileInfo?>(new RouterFileInfo(fileName, 4));
        }

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
            CancellationToken cancellationToken = default)
        {
            FetchedUrls.Add(url);
            FetchedFiles.Add(destinationFileName);
            return Task.CompletedTask;
        }

        public Task RebootAsync(ITikConnection connection, CancellationToken cancellationToken = default)
        {
            RebootCount++;
            _rebootIndex = RebootCount;
            return Task.CompletedTask;
        }

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
            PresentScripts.RemoveAll(n => n.Equals(scriptName, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        public Task<int> CountSchedulersByNameAsync(
            ITikConnection connection,
            string schedulerName,
            CancellationToken cancellationToken = default)
        {
            SchedulerCountCalls++;
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
            PresentSchedulers.RemoveAll(n => n.Equals(schedulerName, StringComparison.Ordinal));
            return Task.CompletedTask;
        }

        public Task UpgradeRouterboardAsync(
            ITikConnection connection,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
