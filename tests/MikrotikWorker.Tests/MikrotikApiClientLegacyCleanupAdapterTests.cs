using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests;

/// <summary>
/// Production-adapter tests for <see cref="MikrotikApiClient"/> legacy object removal.
/// These inspect the actual tik4net commands/parameters issued by the production client —
/// not the FakeOps used by cleanup unit tests.
/// </summary>
public sealed class MikrotikApiClientLegacyCleanupAdapterTests
{
    [Fact]
    public async Task RemoveSchedulersByName_UsesExactNameMatch_AndRemovesByObjectId()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/scheduler/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*A1",
                        ["name"] = LegacyFleetUpgradeObjectNames.SchedulerName,
                    }),
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*KEEP",
                        ["name"] = "FleetUpgrade-startup-old",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/scheduler/remove",
                [new TikFakeDoneSentence()]);

        ParameterCapturingConnection capturing = new(fake);
        MikrotikApiClient client = CreateClient();

        await client.RemoveSchedulersByNameAsync(
            capturing,
            LegacyFleetUpgradeObjectNames.SchedulerName,
            CancellationToken.None);

        Assert.Contains("/system/scheduler/print", capturing.CommandTexts);
        CapturedRemove remove = Assert.Single(capturing.RemoveCalls);
        Assert.Equal("/system/scheduler/remove", remove.CommandText);
        Assert.Equal("*A1", remove.Parameters[".id"]);
        Assert.False(remove.Parameters.ContainsKey("numbers"));
        Assert.DoesNotContain(capturing.RemoveCalls, static c => c.Parameters.Values.Contains("*KEEP"));
    }

    [Fact]
    public async Task RemoveSystemScriptsByName_UsesExactNameMatch_AndRemovesByObjectId()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/script/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*S1",
                        ["name"] = LegacyFleetUpgradeObjectNames.ScriptName,
                    }),
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*S2",
                        ["name"] = "FleetUpgrade2",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/script/remove",
                [new TikFakeDoneSentence()]);

        ParameterCapturingConnection capturing = new(fake);
        MikrotikApiClient client = CreateClient();

        await client.RemoveSystemScriptsByNameAsync(
            capturing,
            LegacyFleetUpgradeObjectNames.ScriptName,
            CancellationToken.None);

        Assert.Single(capturing.RemoveCalls);
        Assert.Equal("/system/script/remove", capturing.RemoveCalls[0].CommandText);
        Assert.Equal("*S1", capturing.RemoveCalls[0].Parameters[".id"]);
        Assert.DoesNotContain(capturing.RemoveCalls, static c => c.Parameters.Values.Contains("*S2"));
    }

    [Fact]
    public async Task RemoveSchedulersByName_MultipleExactMatches_EachRemovedByOwnId()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/scheduler/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*1",
                        ["name"] = LegacyFleetUpgradeObjectNames.SchedulerName,
                    }),
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*2",
                        ["name"] = LegacyFleetUpgradeObjectNames.SchedulerName,
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/scheduler/remove",
                [new TikFakeDoneSentence()]);

        ParameterCapturingConnection capturing = new(fake);
        MikrotikApiClient client = CreateClient();

        await client.RemoveSchedulersByNameAsync(
            capturing,
            LegacyFleetUpgradeObjectNames.SchedulerName,
            CancellationToken.None);

        Assert.Equal(2, capturing.RemoveCalls.Count);
        Assert.Equal(["*1", "*2"], capturing.RemoveCalls.Select(static c => c.Parameters[".id"]).ToArray());
        Assert.All(capturing.RemoveCalls, static c => Assert.Equal("/system/scheduler/remove", c.CommandText));
        Assert.All(capturing.RemoveCalls, static c => Assert.False(c.Parameters.ContainsKey("numbers")));
    }

    [Fact]
    public async Task RemoveSystemScriptsByName_MissingObjectId_FailsWithoutRemove()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/script/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["name"] = LegacyFleetUpgradeObjectNames.ScriptName,
                    }),
                    new TikFakeDoneSentence(),
                ]);

        ParameterCapturingConnection capturing = new(fake);
        MikrotikApiClient client = CreateClient();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RemoveSystemScriptsByNameAsync(
                capturing,
                LegacyFleetUpgradeObjectNames.ScriptName,
                CancellationToken.None));

        Assert.Contains(".id", ex.Message, StringComparison.Ordinal);
        Assert.Empty(capturing.RemoveCalls);
    }

    [Fact]
    public async Task CountByName_IsExactAndCaseSensitive()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/script/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*1",
                        ["name"] = LegacyFleetUpgradeObjectNames.ScriptName,
                    }),
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*2",
                        ["name"] = "fleetupgrade",
                    }),
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*3",
                        ["name"] = "FleetUpgrade2",
                    }),
                    new TikFakeDoneSentence(),
                ]);

        MikrotikApiClient client = CreateClient();
        int count = await client.CountSystemScriptsByNameAsync(
            fake,
            LegacyFleetUpgradeObjectNames.ScriptName,
            CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Cleanup_ThroughProductionClient_RemovesSchedulerBeforeScript_UsingIds()
    {
        // Stateful: print keeps returning the object until its remove is observed.
        // (Count + RemoveNamedObjectsAsync each issue their own print.)
        bool schedulerRemoved = false;
        bool scriptRemoved = false;

        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                rows => rows.First() == "/system/scheduler/print",
                _ => schedulerRemoved
                    ? [new TikFakeDoneSentence()]
                    :
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*SCH",
                            ["name"] = LegacyFleetUpgradeObjectNames.SchedulerName,
                        }),
                        new TikFakeDoneSentence(),
                    ])
            .WithResponse(
                rows => rows.First() == "/system/scheduler/remove",
                _ =>
                {
                    schedulerRemoved = true;
                    return [new TikFakeDoneSentence()];
                })
            .WithResponse(
                rows => rows.First() == "/system/script/print",
                _ => scriptRemoved
                    ? [new TikFakeDoneSentence()]
                    :
                    [
                        new TikFakeReSentence(new Dictionary<string, string>
                        {
                            [".id"] = "*SCR",
                            ["name"] = LegacyFleetUpgradeObjectNames.ScriptName,
                        }),
                        new TikFakeDoneSentence(),
                    ])
            .WithResponse(
                rows => rows.First() == "/system/script/remove",
                _ =>
                {
                    scriptRemoved = true;
                    return [new TikFakeDoneSentence()];
                });
        ParameterCapturingConnection capturing = new(fake);
        MikrotikApiClient client = CreateClient();
        LegacyFleetUpgradeAutomationCleanup cleanup = new(
            client,
            NullLogger<LegacyFleetUpgradeAutomationCleanup>.Instance);

        LegacyFleetUpgradeCleanupResult result = await cleanup.CleanupAsync(
            capturing,
            new MikrotikWorker.Scheduling.MikrotikScheduledJob
            {
                ImportId = Guid.NewGuid(),
                SourceFileName = "a.csv",
                RowNumber = 2,
                RouterName = "R1",
                RouterIp = System.Net.IPAddress.Parse("10.0.0.1"),
                DateStartUtc = DateTime.UtcNow.AddMinutes(-1),
                DateStopUtc = DateTime.UtcNow.AddHours(1),
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(2, capturing.RemoveCalls.Count);
        Assert.Equal("/system/scheduler/remove", capturing.RemoveCalls[0].CommandText);
        Assert.Equal("*SCH", capturing.RemoveCalls[0].Parameters[".id"]);
        Assert.Equal("/system/script/remove", capturing.RemoveCalls[1].CommandText);
        Assert.Equal("*SCR", capturing.RemoveCalls[1].Parameters[".id"]);

        int firstSchedulerRemove = capturing.CommandTexts.IndexOf("/system/scheduler/remove");
        int firstScriptPrintAfterScheduler = capturing.CommandTexts
            .Select((c, i) => (c, i))
            .Where(t => t.c == "/system/script/print" && t.i > firstSchedulerRemove)
            .Select(t => t.i)
            .First();
        int schedulerVerifyPrint = capturing.CommandTexts
            .Select((c, i) => (c, i))
            .Where(t => t.c == "/system/scheduler/print" && t.i > firstSchedulerRemove)
            .Select(t => t.i)
            .First();
        Assert.True(schedulerVerifyPrint < firstScriptPrintAfterScheduler);
    }

    [Fact]
    public async Task RemoveSchedulersByName_CancelledToken_PropagatesWithoutRemove()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/scheduler/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        [".id"] = "*A1",
                        ["name"] = LegacyFleetUpgradeObjectNames.SchedulerName,
                    }),
                    new TikFakeDoneSentence(),
                ]);

        ParameterCapturingConnection capturing = new(fake);
        MikrotikApiClient client = CreateClient();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RemoveSchedulersByNameAsync(
                capturing,
                LegacyFleetUpgradeObjectNames.SchedulerName,
                cts.Token));

        Assert.Empty(capturing.RemoveCalls);
        Assert.DoesNotContain("/system/scheduler/remove", capturing.CommandTexts);
    }

    private static MikrotikApiClient CreateClient() =>
        new(
            new ThrowingMikrotikConnectionFactory(new InvalidOperationException("unused")),
            Options.Create(new MikroTikOptions { Port = 8728 }),
            NullLogger<MikrotikApiClient>.Instance);

    /// <summary>
    /// Records production command texts and retains the real tik4net remove commands so their
    /// <c>.id</c> parameters can be inspected after <see cref="MikrotikApiClient"/> mutates them.
    /// Does not replace tik4net execute behaviour (avoids breaking <c>AsAsync()</c> / ITikCommandAsync).
    /// </summary>
    private sealed class ParameterCapturingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
    {
        private readonly List<ITikCommand> _removeCommands = [];

        public List<string> CommandTexts { get; } = [];

        public IReadOnlyList<CapturedRemove> RemoveCalls =>
            _removeCommands
                .Select(static cmd => new CapturedRemove(
                    cmd.CommandText,
                    cmd.Parameters.ToDictionary(
                        static p => p.Name,
                        static p => p.Value ?? string.Empty,
                        StringComparer.Ordinal)))
                .ToArray();

        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            CommandTexts.Add(commandText);
            ITikCommand real = base.CreateCommand(commandText, parameters);
            if (commandText.EndsWith("/remove", StringComparison.Ordinal))
            {
                _removeCommands.Add(real);
            }

            return real;
        }

        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            CommandTexts.Add(commandText);
            ITikCommand real = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            if (commandText.EndsWith("/remove", StringComparison.Ordinal))
            {
                _removeCommands.Add(real);
            }

            return real;
        }
    }

    private sealed record CapturedRemove(string CommandText, IReadOnlyDictionary<string, string> Parameters);
}
