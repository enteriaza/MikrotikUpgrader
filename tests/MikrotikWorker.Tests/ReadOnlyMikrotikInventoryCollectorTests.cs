using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests;

public sealed class ReadOnlyMikrotikInventoryCollectorTests
{
    [Fact]
    public async Task Collects_SuccessfulInventory_IncludingBundle()
    {
        CommandRecordingConnection recording = new(
            FakeMikrotikConnections.CreateWithPackageInventory());
        ScriptedMikrotikConnectionFactory factory = new(_ => recording);
        ReadOnlyMikrotikInventoryCollector collector = Create(factory);

        ReadOnlyInventoryCollectionResult result = await collector.CollectAsync(
            "10.0.0.10",
            "admin",
            "secret",
            8728,
            TimeSpan.FromSeconds(5));

        Assert.Equal(LiveInventoryCollectionStatus.Succeeded, result.CollectionStatus);
        Assert.Equal(LiveIdentityCollectionStatus.Succeeded, result.IdentityStatus);
        Assert.Equal("AP01.POP001.001.access", result.Identity);
        Assert.Equal("6.49.10", result.RouterOsVersion);
        Assert.Equal("mipsbe", result.Architecture);
        Assert.Contains(result.Packages, static p => p.Name == "routeros-mipsbe" && !p.IsBundledComponent);
        Assert.Contains(result.Packages, static p => p.Name == "ipv6" && p.IsBundledComponent && p.Bundle == "routeros-mipsbe");
        Assert.Contains(result.Packages, static p => p.Name == "user-manager" && !p.IsBundledComponent);
        Assert.Equal(1, recording.DisposeCount);
        Assert.All(recording.Commands, static c => Assert.Contains(c, ReadOnlyTikCommandExecutor.AllowedCommands));
    }

    [Fact]
    public async Task IdentityUnavailable_StillCollectsRemainingInventory()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithTrap(static rows => rows.First() == "/system/identity/print", "cannot read identity")
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["version"] = "6.49.10",
                        ["architecture-name"] = "mipsbe",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/package/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["name"] = "routeros-mipsbe",
                        ["version"] = "6.49.10",
                        ["disabled"] = "false",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithTrap(static rows => rows.First() == "/system/routerboard/print", "no such command");

        ReadOnlyInventoryCollectionResult result = await Create(new ScriptedMikrotikConnectionFactory(_ => fake))
            .CollectAsync("10.0.0.11", "admin", "x", 8728, TimeSpan.FromSeconds(5));

        Assert.Equal(LiveIdentityCollectionStatus.Failed, result.IdentityStatus);
        Assert.Equal("6.49.10", result.RouterOsVersion);
        Assert.Equal("mipsbe", result.Architecture);
        Assert.NotEmpty(result.Packages);
        // Identity failure must not be confused with incomplete package inventory.
        Assert.Equal(LiveInventoryCollectionStatus.Succeeded, result.CollectionStatus);
        Assert.True(result.HasUsableInventory);
    }

    [Fact]
    public async Task VersionUnavailable_RecordsPartial()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/identity/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["name"] = "AP01.POP001.001.access" }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["architecture-name"] = "mipsbe",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/package/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["name"] = "routeros-mipsbe",
                        ["version"] = "6.49.10",
                        ["disabled"] = "false",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithTrap(static rows => rows.First() == "/system/routerboard/print", "no such command");

        ReadOnlyInventoryCollectionResult result = await Create(new ScriptedMikrotikConnectionFactory(_ => fake))
            .CollectAsync("10.0.0.12", "admin", "x", 8728, TimeSpan.FromSeconds(5));

        Assert.Null(result.RouterOsVersion);
        Assert.Equal(LiveInventoryCollectionStatus.Partial, result.CollectionStatus);
    }

    [Fact]
    public async Task ArchitectureUnavailable_RecordsPartial()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/identity/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["name"] = "AP01.POP001.001.access" }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["version"] = "6.49.10",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/package/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["name"] = "routeros-mipsbe",
                        ["version"] = "6.49.10",
                        ["disabled"] = "false",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithTrap(static rows => rows.First() == "/system/routerboard/print", "no such command");

        ReadOnlyInventoryCollectionResult result = await Create(new ScriptedMikrotikConnectionFactory(_ => fake))
            .CollectAsync("10.0.0.13", "admin", "x", 8728, TimeSpan.FromSeconds(5));

        Assert.Null(result.Architecture);
        Assert.Equal(LiveInventoryCollectionStatus.Partial, result.CollectionStatus);
    }

    [Fact]
    public async Task PackageInventoryUnavailable_RecordsPartial()
    {
        TikFakeConnection fake = FakeMikrotikConnections.CreateFullyPopulated()
            .WithTrap(static rows => rows.First() == "/system/package/print", "cannot read packages");

        ReadOnlyInventoryCollectionResult result = await Create(new ScriptedMikrotikConnectionFactory(_ => fake))
            .CollectAsync("10.0.0.14", "admin", "x", 8728, TimeSpan.FromSeconds(5));

        Assert.Equal(LiveInventoryCollectionStatus.Partial, result.CollectionStatus);
        Assert.Empty(result.Packages);
        Assert.Contains("package", result.FailureDetail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticationFailure_IsIsolated()
    {
        ScriptedMikrotikConnectionFactory factory = new(_ =>
            throw new InvalidOperationException("invalid user name or password"));

        ReadOnlyInventoryCollectionResult result = await Create(factory)
            .CollectAsync("10.0.0.15", "admin", "bad", 8728, TimeSpan.FromSeconds(5));

        Assert.Equal(LiveInventoryCollectionStatus.AuthenticationFailed, result.CollectionStatus);
        Assert.DoesNotContain("bad", result.FailureDetail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionTimeout_IsIsolated()
    {
        ScriptedMikrotikConnectionFactory factory = new(_ => throw new OperationCanceledException());

        ReadOnlyInventoryCollectionResult result = await Create(factory)
            .CollectAsync("10.0.0.16", "admin", "x", 8728, TimeSpan.FromMilliseconds(1));

        Assert.Equal(LiveInventoryCollectionStatus.TimedOut, result.CollectionStatus);
    }

    [Fact]
    public async Task Cancellation_StopsAndDisposes()
    {
        CommandRecordingConnection recording = new(FakeMikrotikConnections.CreateWithPackageInventory());
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        ReadOnlyInventoryCollectionResult result = await Create(new ScriptedMikrotikConnectionFactory(_ => recording))
            .CollectAsync("10.0.0.17", "admin", "x", 8728, TimeSpan.FromSeconds(5), cancellationToken: cts.Token);

        Assert.Equal(LiveInventoryCollectionStatus.Cancelled, result.CollectionStatus);
    }

    [Fact]
    public async Task ConnectionFailure_IsIsolated()
    {
        ScriptedMikrotikConnectionFactory factory = new(_ =>
            throw new IOException("connection refused"));

        ReadOnlyInventoryCollectionResult result = await Create(factory)
            .CollectAsync("10.0.0.18", "admin", "x", 8728, TimeSpan.FromSeconds(5));

        Assert.Equal(LiveInventoryCollectionStatus.ConnectionFailed, result.CollectionStatus);
    }

    [Fact]
    public void DisallowedCommand_IsRejectedByExecutor()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReadOnlyTikCommandExecutor.EnsureAllowed("/tool/fetch"));
        Assert.Throws<InvalidOperationException>(() =>
            ReadOnlyTikCommandExecutor.EnsureAllowed("/system/reboot"));
        Assert.Throws<InvalidOperationException>(() =>
            ReadOnlyTikCommandExecutor.EnsureAllowed("/system/backup/save"));
        Assert.Throws<InvalidOperationException>(() =>
            ReadOnlyTikCommandExecutor.EnsureAllowed("/export"));
    }

    private static ReadOnlyMikrotikInventoryCollector Create(IMikrotikConnectionFactory factory) =>
        new(factory, NullLogger<ReadOnlyMikrotikInventoryCollector>.Instance);
}
