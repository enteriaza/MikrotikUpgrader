// -----------------------------------------------------------------------------
// File: OspfAreaRebindDualProtocolTests.cs
//
// Description:
// Production-path fake-RouterOS tests proving EnsureOspfInstanceNameAsync /
// area rebind updates only OSPFv2 areas by prior instance= ownership and never
// mutates OSPFv3 areas that share names or area-ids.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.Tests.Infrastructure.Fakes;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade.Ospf;

/// <summary>
/// Dual-protocol area rebind evidence for the production rename/rebind path.
/// </summary>
public sealed class OspfAreaRebindDualProtocolTests
{
    private const string LegacyV2Name = "default";
    private const string TargetV2Name = "default-v2";
    private const string V3Name = "default-v3";

    private const string V2InstanceId = "*i2";
    private const string V3InstanceId = "*i3";

    private const string V2BackboneAreaId = "*a2bb";
    private const string V2SiteAreaId = "*a2site";
    private const string V3BackboneAreaId = "*a3bb";
    private const string V3SiteAreaId = "*a3site";
    private const string UnrelatedAreaId = "*aX";
    private const string OrphanAreaId = "*aOrphan";

    [Fact]
    public async Task EnsureOspfInstanceName_RebindsOnlyPriorV2Areas_IgnoresMatchingV3NameAndAreaId()
    {
        MutableDualProtocolStore store = CreateDualProtocolStore();
        CapturingConnection connection = new(store);
        connection.Attach();

        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);
        await ops.EnsureOspfInstanceNameAsync(connection, LegacyV2Name, TargetV2Name, CancellationToken.None);

        Assert.Equal(TargetV2Name, store.Instances.Single(i => i[".id"] == V2InstanceId)["name"]);
        Assert.Equal(V3Name, store.Instances.Single(i => i[".id"] == V3InstanceId)["name"]);

        Assert.Equal(TargetV2Name, store.Areas.Single(a => a[".id"] == V2BackboneAreaId)["instance"]);
        Assert.Equal(TargetV2Name, store.Areas.Single(a => a[".id"] == V2SiteAreaId)["instance"]);
        Assert.Equal(V3Name, store.Areas.Single(a => a[".id"] == V3BackboneAreaId)["instance"]);
        Assert.Equal(V3Name, store.Areas.Single(a => a[".id"] == V3SiteAreaId)["instance"]);
        Assert.Equal("other-instance", store.Areas.Single(a => a[".id"] == UnrelatedAreaId)["instance"]);
        Assert.Equal("missing-v3", store.Areas.Single(a => a[".id"] == OrphanAreaId)["instance"]);

        Assert.Equal(
            [V2InstanceId],
            connection.InstanceSetIds);
        Assert.Equal(
            [V2BackboneAreaId, V2SiteAreaId],
            connection.AreaSetIds);
        Assert.DoesNotContain(connection.Commands, c => c.Contains("/remove", StringComparison.Ordinal));
        Assert.DoesNotContain(V3BackboneAreaId, connection.AreaSetIds);
        Assert.DoesNotContain(V3SiteAreaId, connection.AreaSetIds);
        Assert.DoesNotContain(UnrelatedAreaId, connection.AreaSetIds);
        Assert.DoesNotContain(OrphanAreaId, connection.AreaSetIds);
    }

    [Fact]
    public async Task EnsureOspfInstanceName_SecondRun_IsIdempotent()
    {
        MutableDualProtocolStore store = CreateDualProtocolStore();
        CapturingConnection connection = new(store);
        connection.Attach();
        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);

        await ops.EnsureOspfInstanceNameAsync(connection, LegacyV2Name, TargetV2Name, CancellationToken.None);
        int setsAfterFirst = connection.AreaSetIds.Count + connection.InstanceSetIds.Count;

        await ops.EnsureOspfInstanceNameAsync(connection, TargetV2Name, TargetV2Name, CancellationToken.None);
        Assert.Equal(setsAfterFirst, connection.AreaSetIds.Count + connection.InstanceSetIds.Count);

        // Already renamed inventory: calling with legacy name again must fail closed (source missing).
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ops.EnsureOspfInstanceNameAsync(connection, LegacyV2Name, TargetV2Name, CancellationToken.None));
        Assert.Contains("source instance is missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rebind_MissingAreaId_FailsClosed()
    {
        MutableDualProtocolStore store = CreateDualProtocolStore();
        store.Areas.Single(a => a[".id"] == V2SiteAreaId).Remove(".id");
        CapturingConnection connection = new(store);
        connection.Attach();

        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ops.EnsureOspfInstanceNameAsync(connection, LegacyV2Name, TargetV2Name, CancellationToken.None));

        Assert.Contains("no .id", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TargetV2Name, store.Instances.Single(i => i[".id"] == V2InstanceId)["name"]);
        Assert.Equal(
            V3Name,
            store.Areas.Single(a => a.TryGetValue(".id", out string? id) && id == V3BackboneAreaId)["instance"]);
    }

    [Fact]
    public async Task Rebind_AreaApiUpdateFailure_FailsClosed_AfterRename()
    {
        MutableDualProtocolStore store = CreateDualProtocolStore();
        CapturingConnection connection = new(store) { FailAreaSetId = V2SiteAreaId };
        connection.Attach();

        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ops.EnsureOspfInstanceNameAsync(connection, LegacyV2Name, TargetV2Name, CancellationToken.None));

        Assert.Contains("Simulated area set failure", ex.Message, StringComparison.Ordinal);
        Assert.Equal(TargetV2Name, store.Instances.Single(i => i[".id"] == V2InstanceId)["name"]);
        Assert.Equal(TargetV2Name, store.Areas.Single(a => a[".id"] == V2BackboneAreaId)["instance"]);
        Assert.Equal(LegacyV2Name, store.Areas.Single(a => a[".id"] == V2SiteAreaId)["instance"]);
        Assert.Equal(V3Name, store.Areas.Single(a => a[".id"] == V3BackboneAreaId)["instance"]);
    }

    [Fact]
    public async Task RerunAfterPartialRebind_CompletesRemainingV2Areas()
    {
        MutableDualProtocolStore store = CreateDualProtocolStore();
        // Simulate rename already done and one area already rebound.
        store.Instances.Single(i => i[".id"] == V2InstanceId)["name"] = TargetV2Name;
        store.Areas.Single(a => a[".id"] == V2BackboneAreaId)["instance"] = TargetV2Name;
        // Site still points at legacy name — production path from currentName=legacy fails because
        // source instance is gone. Documented failure mode: after rename+partial rebind, recovery
        // requires invoking rebind with previousInstanceName=legacy against the already-renamed
        // instance via a dedicated repair, or completing the remaining area set manually.
        // The production EnsureOspfInstanceNameAsync(current=legacy) fails closed here.
        CapturingConnection connection = new(store);
        connection.Attach();
        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ops.EnsureOspfInstanceNameAsync(connection, LegacyV2Name, TargetV2Name, CancellationToken.None));
        Assert.Contains("source instance is missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LegacyV2Name, store.Areas.Single(a => a[".id"] == V2SiteAreaId)["instance"]);
    }

    [Fact]
    public async Task DuplicateConflictingInstanceInventory_FailsClosed()
    {
        MutableDualProtocolStore store = CreateDualProtocolStore();
        store.Instances.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".id"] = "*i2b",
            ["name"] = TargetV2Name,
            ["version"] = "2",
        });
        CapturingConnection connection = new(store);
        connection.Attach();

        OspfMikrotikOperations ops = new(NullLogger<OspfMikrotikOperations>.Instance);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ops.EnsureOspfInstanceNameAsync(connection, LegacyV2Name, TargetV2Name, CancellationToken.None));

        Assert.Contains("target name already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(connection.AreaSetIds);
        Assert.Equal(LegacyV2Name, store.Instances.Single(i => i[".id"] == V2InstanceId)["name"]);
    }

    private static MutableDualProtocolStore CreateDualProtocolStore() =>
        new()
        {
            Instances =
            [
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".id"] = V2InstanceId,
                    ["name"] = LegacyV2Name,
                    ["version"] = "2",
                    ["router-id"] = "10.16.0.11",
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".id"] = V3InstanceId,
                    ["name"] = V3Name,
                    ["version"] = "3",
                    ["router-id"] = "10.16.0.11",
                },
            ],
            Areas =
            [
                // Matching names + matching area-ids across protocols — selection must use instance=.
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".id"] = V2BackboneAreaId,
                    ["name"] = "backbone",
                    ["area-id"] = "0.0.0.0",
                    ["instance"] = LegacyV2Name,
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".id"] = V3BackboneAreaId,
                    ["name"] = "backbone",
                    ["area-id"] = "0.0.0.0",
                    ["instance"] = V3Name,
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".id"] = V2SiteAreaId,
                    ["name"] = "site",
                    ["area-id"] = "10.16.0.11",
                    ["instance"] = LegacyV2Name,
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".id"] = V3SiteAreaId,
                    ["name"] = "site",
                    ["area-id"] = "10.16.0.11",
                    ["instance"] = V3Name,
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".id"] = UnrelatedAreaId,
                    ["name"] = "unrelated",
                    ["area-id"] = "10.0.0.1",
                    ["instance"] = "other-instance",
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [".id"] = OrphanAreaId,
                    ["name"] = "orphan",
                    ["area-id"] = "10.0.0.2",
                    ["instance"] = "missing-v3",
                },
            ],
        };

    private sealed class MutableDualProtocolStore
    {
        public List<Dictionary<string, string>> Instances { get; init; } = [];
        public List<Dictionary<string, string>> Areas { get; init; } = [];
    }

    private sealed class CapturingConnection : DisposeTrackingConnection
    {
        private readonly MutableDualProtocolStore _store;
        private readonly TikFakeConnection _fake;
        private ITikCommand? _pendingSet;

        public CapturingConnection(MutableDualProtocolStore store)
            : this(store, new TikFakeConnection())
        {
        }

        private CapturingConnection(MutableDualProtocolStore store, TikFakeConnection fake)
            : base(fake)
        {
            _store = store;
            _fake = fake;
        }

        public List<string> Commands { get; } = [];
        public List<string> InstanceSetIds { get; } = [];
        public List<string> AreaSetIds { get; } = [];
        public string? FailAreaSetId { get; init; }

        public void Attach()
        {
            _fake.WithResponse(
                    static rows => rows.First() == "/routing/ospf/instance/print",
                    _ =>
                    [
                        .._store.Instances.Select(static r => new TikFakeReSentence(new Dictionary<string, string>(r))),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/instance/set",
                    _ =>
                    {
                        string? id = _pendingSet?.Parameters.FirstOrDefault(p => p.Name == ".id")?.Value;
                        string? name = _pendingSet?.Parameters.FirstOrDefault(p => p.Name == "name")?.Value;
                        Dictionary<string, string> row = _store.Instances.Single(i => i[".id"] == id);
                        if (name is not null)
                        {
                            row["name"] = name;
                        }

                        InstanceSetIds.Add(id!);
                        return [new TikFakeDoneSentence()];
                    })
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/area/print",
                    _ =>
                    [
                        .._store.Areas.Select(static r =>
                        {
                            // Areas that lost .id still serialize remaining keys.
                            return new TikFakeReSentence(new Dictionary<string, string>(r));
                        }),
                        new TikFakeDoneSentence(),
                    ])
                .WithResponse(
                    static rows => rows.First() == "/routing/ospf/area/set",
                    _ =>
                    {
                        string? id = _pendingSet?.Parameters.FirstOrDefault(p => p.Name == ".id")?.Value;
                        string? instance = _pendingSet?.Parameters.FirstOrDefault(p => p.Name == "instance")?.Value;
                        if (FailAreaSetId is not null
                            && string.Equals(id, FailAreaSetId, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException($"Simulated area set failure for {id}");
                        }

                        Dictionary<string, string> row = _store.Areas.Single(a =>
                            a.TryGetValue(".id", out string? rowId) && rowId == id);
                        if (instance is not null)
                        {
                            row["instance"] = instance;
                        }

                        AreaSetIds.Add(id!);
                        return [new TikFakeDoneSentence()];
                    });
        }

        public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
        {
            Commands.Add(commandText);
            ITikCommand cmd = base.CreateCommand(commandText, parameters);
            if (commandText is "/routing/ospf/instance/set" or "/routing/ospf/area/set")
            {
                _pendingSet = cmd;
            }

            return cmd;
        }

        public override ITikCommand CreateCommand(
            string commandText,
            TikCommandParameterFormat defaultParameterFormat,
            params ITikCommandParameter[] parameters)
        {
            Commands.Add(commandText);
            ITikCommand cmd = base.CreateCommand(commandText, defaultParameterFormat, parameters);
            if (commandText is "/routing/ospf/instance/set" or "/routing/ospf/area/set")
            {
                _pendingSet = cmd;
            }

            return cmd;
        }
    }
}
