// -----------------------------------------------------------------------------
// File: MikrotikApiClientLegacyCleanupAdapterTests.cs
//
// Description:
// Tests legacy FleetUpgrade script/scheduler cleanup adapters on MikrotikApiClient
// using exact-name match semantics (no wildcards).
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.MikroTik;
    /// <summary>
    /// Scenario: Production <c>MikrotikApiClient</c> cleanup against a stateful fake where scheduler/script print
    /// rows remain until their matching remove is observed.
    /// Expected: the scheduler object is removed before the script object (by <c>.id</c>), matching fleet cleanup order.
    /// Invariant: cleanup must not leave the named scheduler after the script has already been removed.
    /// </summary>
public sealed class MikrotikApiClientLegacyCleanupAdapterTests
{
    /// <summary>
    /// Scenario: The scheduler print returns the legacy scheduler (<c>*A1</c>) and a similarly named
    /// <c>FleetUpgrade-startup-old</c> (<c>*KEEP</c>); <c>RemoveSchedulersByNameAsync</c> is called with the legacy
    /// name.
    /// Expected: Exactly one <c>/system/scheduler/remove</c> is issued with <c>.id</c> = <c>*A1</c> and no
    /// <c>numbers</c> parameter, and <c>*KEEP</c> is never removed.
    /// Invariant: Removal matches the exact name and targets the object by <c>.id</c>, never by wildcard or number.
    /// </summary>
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

    /// <summary>
    /// Scenario: The script print returns the legacy script (<c>*S1</c>) and <c>FleetUpgrade2</c> (<c>*S2</c>);
    /// <c>RemoveSystemScriptsByNameAsync</c> is called with the legacy script name.
    /// Expected: Exactly one <c>/system/script/remove</c> is issued with <c>.id</c> = <c>*S1</c>, and <c>*S2</c>
    /// is never removed.
    /// Invariant: A script whose name merely starts with the legacy name is left alone.
    /// </summary>
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

    /// <summary>
    /// Scenario: The scheduler print returns two entries (<c>*1</c> and <c>*2</c>) that both have the exact legacy
    /// name.
    /// Expected: Two <c>/system/scheduler/remove</c> commands are issued, in order, each carrying its own <c>.id</c>
    /// and no <c>numbers</c> parameter.
    /// Invariant: Duplicate exact-name matches are all removed individually by ID.
    /// </summary>
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

    /// <summary>
    /// Scenario: The script print returns a row with the legacy name but no <c>.id</c> field.
    /// Expected: <c>InvalidOperationException</c> is thrown with a message mentioning <c>.id</c>, and no remove
    /// command is issued.
    /// Invariant: An object that cannot be identified by ID is never removed.
    /// </summary>
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

    /// <summary>
    /// Scenario: The script print returns the legacy name, a lower-case <c>fleetupgrade</c> and <c>FleetUpgrade2</c>;
    /// <c>CountSystemScriptsByNameAsync</c> counts the legacy name.
    /// Expected: The count is 1.
    /// Invariant: Counting uses ordinal exact matching, ignoring case variants and longer names.
    /// </summary>
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

    /// <summary>
    /// Scenario: Production MikrotikApiClient cleanup against a stateful fake where scheduler/script print
    /// rows remain until their matching remove is observed.
    /// Expected: the scheduler object is removed before the script object (by .id).
    /// Invariant: cleanup must not leave the named scheduler after the script has already been removed.
    /// </summary>
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

    /// <summary>
    /// Scenario: <c>RemoveSchedulersByNameAsync</c> is called with an already cancelled token while the print would
    /// return a matching scheduler.
    /// Expected: <c>OperationCanceledException</c> is thrown, no remove call is recorded, and
    /// <c>/system/scheduler/remove</c> is never issued.
    /// Invariant: A cancelled cleanup never mutates the router.
    /// </summary>
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

    /// <summary>
    /// Helper used by this suite.
    /// Key steps: Port = 8728.
    /// </summary>
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
        /// <summary>Commands whose text ends with <c>/remove</c>, kept so their parameters can be inspected.</summary>
        private readonly List<ITikCommand> _removeCommands = [];

        /// <summary>Every command text passed to <c>CreateCommand</c>, in call order.</summary>
        public List<string> CommandTexts { get; } = [];

        /// <summary>Snapshot of recorded <c>/remove</c> commands with their parameter name/value pairs.</summary>
        public IReadOnlyList<CapturedRemove> RemoveCalls =>
            _removeCommands
                .Select(static cmd => new CapturedRemove(
                    cmd.CommandText,
                    cmd.Parameters.ToDictionary(
                        static p => p.Name,
                        static p => p.Value ?? string.Empty,
                        StringComparer.Ordinal)))
                .ToArray();

        /// <summary>Records <paramref name="commandText"/> (and the command when it is a <c>/remove</c>) before returning the real command.</summary>
        /// <param name="commandText">Command path.</param>
        /// <param name="parameters">Command parameters.</param>
        /// <returns>The command created by the base fake connection.</returns>
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

        /// <summary>Records <paramref name="commandText"/> (and the command when it is a <c>/remove</c>) before returning the real command.</summary>
        /// <param name="commandText">Command path.</param>
        /// <param name="defaultParameterFormat">Default format applied to parameters.</param>
        /// <param name="parameters">Command parameters.</param>
        /// <returns>The command created by the base fake connection.</returns>
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

    /// <summary>
    /// Snapshot of one captured remove command: its command text and the parameters that were added to it.
    /// </summary>
    private sealed record CapturedRemove(string CommandText, IReadOnlyDictionary<string, string> Parameters);
}
