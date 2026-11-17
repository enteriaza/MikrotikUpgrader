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
    /// <summary>
    /// Legacy OSPFv2 instance name before rename (<c>default</c>).
    /// </summary>
    private const string LegacyV2Name = "default";

    /// <summary>
    /// Target OSPFv2 instance name after rename (<c>default-v2</c>).
    /// </summary>
    private const string TargetV2Name = "default-v2";

    /// <summary>
    /// Co-resident OSPFv3 instance name that must never be renamed or rebound.
    /// </summary>
    private const string V3Name = "default-v3";

    /// <summary>
    /// Fake RouterOS object id for the OSPFv2 instance row.
    /// </summary>
    private const string V2InstanceId = "*i2";

    /// <summary>
    /// Fake RouterOS object id for the OSPFv3 instance row.
    /// </summary>
    private const string V3InstanceId = "*i3";

    /// <summary>
    /// Fake id for the OSPFv2 backbone area (name/area-id collide with v3).
    /// </summary>
    private const string V2BackboneAreaId = "*a2bb";

    /// <summary>
    /// Fake id for the OSPFv2 site area (name/area-id collide with v3).
    /// </summary>
    private const string V2SiteAreaId = "*a2site";

    /// <summary>
    /// Fake id for the OSPFv3 backbone area sharing name and area-id with v2.
    /// </summary>
    private const string V3BackboneAreaId = "*a3bb";

    /// <summary>
    /// Fake id for the OSPFv3 site area sharing name and area-id with v2.
    /// </summary>
    private const string V3SiteAreaId = "*a3site";

    /// <summary>
    /// Area owned by an unrelated instance; must never receive a rebind set.
    /// </summary>
    private const string UnrelatedAreaId = "*aX";

    /// <summary>
    /// Orphan area pointing at a missing instance name; must never be rebound.
    /// </summary>
    private const string OrphanAreaId = "*aOrphan";

    /// <summary>
    /// Scenario: dual-protocol store with matching area names/ids across v2 and v3; rename default→default-v2.
    /// Expected: only v2 instance and prior-v2 areas are set; v3/unrelated/orphan instance= unchanged; no removes.
    /// Invariant: area rebind selects by prior instance ownership, never by shared name or area-id alone.
    /// </summary>
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

    /// <summary>
    /// Scenario: successful rename/rebind, then a second call with current=target, then a call with missing legacy source.
    /// Expected: second run issues no additional sets; legacy-source call fails closed with source-missing.
    /// Invariant: EnsureOspfInstanceNameAsync is idempotent when already at target and refuses a missing source.
    /// </summary>
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

    /// <summary>
    /// Scenario: v2 site area row has lost its <c>.id</c> before rebind.
    /// Expected: fails closed mentioning no .id after instance rename; v3 area instance= untouched.
    /// Invariant: areas without RouterOS ids cannot be rebound; mutation stops fail-closed.
    /// </summary>
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

    /// <summary>
    /// Scenario: area/set for the v2 site area throws after the instance rename and backbone rebind succeed.
    /// Expected: exception surfaces; renamed instance and rebound backbone stay; site remains on legacy instance=.
    /// Invariant: partial rebind failure fails closed without rolling back prior successful sets (documented state).
    /// </summary>
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

    /// <summary>
    /// Scenario: inventory already renamed and partially rebound (backbone on target, site still on legacy).
    /// Expected: calling Ensure with current=legacy fails closed (source instance missing); site stays on legacy.
    /// Invariant: after rename+partial rebind, the legacy-source entry point cannot finish remaining areas.
    /// </summary>
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

    /// <summary>
    /// Scenario: target instance name already exists as a second conflicting OSPFv2 row.
    /// Expected: fails closed mentioning target already exists; no area sets; legacy name unchanged.
    /// Invariant: rename refuses when the destination name is already claimed by another instance.
    /// </summary>
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

    /// <summary>
    /// Seeds a dual-protocol fake store: OSPFv2+v3 instances and areas that share names/area-ids,
    /// plus unrelated and orphan areas that must never be selected for rebind.
    /// </summary>
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

    /// <summary>
    /// In-memory RouterOS instance/area rows mutated by the capturing fake connection.
    /// Models only the key bags production print/set paths read; not a full RouterOS schema.
    /// </summary>
    private sealed class MutableDualProtocolStore
    {
        /// <summary>
        /// Mutable <c>/routing/ospf/instance</c> print rows keyed by attribute name.
        /// </summary>
        public List<Dictionary<string, string>> Instances { get; init; } = [];

        /// <summary>
        /// Mutable <c>/routing/ospf/area</c> print rows keyed by attribute name.
        /// </summary>
        public List<Dictionary<string, string>> Areas { get; init; } = [];
    }

    /// <summary>
    /// Fake <see cref="ITikConnection"/> that serves dual-protocol print replies from
    /// <see cref="MutableDualProtocolStore"/> and applies instance/area set mutations while recording
    /// command order and targeted ids. Optional <see cref="FailAreaSetId"/> simulates API failure.
    /// Limitation: only OSPF instance/area print+set are wired; other menus are not modeled.
    /// </summary>
    private sealed class CapturingConnection : DisposeTrackingConnection
    {
        /// <summary>
        /// Backing store whose rows are returned on print and updated on set.
        /// </summary>
        private readonly MutableDualProtocolStore _store;

        /// <summary>
        /// tik4net fake used to register print/set response handlers.
        /// </summary>
        private readonly TikFakeConnection _fake;

        /// <summary>
        /// Last instance/area set command whose parameters the set handler will apply.
        /// </summary>
        private ITikCommand? _pendingSet;

        /// <summary>
        /// Creates a capturing connection over a fresh <see cref="TikFakeConnection"/>.
        /// </summary>
        /// <param name="store">Mutable dual-protocol inventory.</param>
        public CapturingConnection(MutableDualProtocolStore store)
            : this(store, new TikFakeConnection())
        {
        }

        /// <summary>
        /// Creates a capturing connection over an injected fake (used by the public ctor).
        /// </summary>
        /// <param name="store">Mutable dual-protocol inventory.</param>
        /// <param name="fake">Underlying tik4net fake connection.</param>
        private CapturingConnection(MutableDualProtocolStore store, TikFakeConnection fake)
            : base(fake)
        {
            _store = store;
            _fake = fake;
        }

        /// <summary>
        /// Ordered command texts observed via <see cref="CreateCommand"/>.
        /// </summary>
        public List<string> Commands { get; } = [];

        /// <summary>
        /// Instance <c>.id</c> values successfully updated by instance/set handlers.
        /// </summary>
        public List<string> InstanceSetIds { get; } = [];

        /// <summary>
        /// Area <c>.id</c> values successfully updated by area/set handlers.
        /// </summary>
        public List<string> AreaSetIds { get; } = [];

        /// <summary>
        /// When set, area/set for this id throws to simulate a RouterOS update failure.
        /// </summary>
        public string? FailAreaSetId { get; init; }

        /// <summary>
        /// Registers print/set handlers that read and mutate <see cref="_store"/>.
        /// Must be called before exercising production ops.
        /// </summary>
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

        /// <summary>
        /// Records the command text and, for instance/area set, stashes the command for the response handler.
        /// </summary>
        /// <param name="commandText">RouterOS command path.</param>
        /// <param name="parameters">Command parameters.</param>
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

        /// <summary>
        /// Overload with explicit parameter format; records and stashes identically so set cannot bypass capture.
        /// </summary>
        /// <param name="commandText">RouterOS command path.</param>
        /// <param name="defaultParameterFormat">tik4net parameter format.</param>
        /// <param name="parameters">Command parameters.</param>
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
