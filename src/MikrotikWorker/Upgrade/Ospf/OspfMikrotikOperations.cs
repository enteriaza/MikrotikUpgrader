using System.Globalization;
using MikrotikWorker.Assessment;
using tik4net;
using TikSpecial = tik4net.TikSpecialProperties;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Production tik4net OSPF capture/reconcile adapter.
/// </summary>
internal sealed class OspfMikrotikOperations(ILogger<OspfMikrotikOperations> logger) : IOspfMikrotikOperations
{
    public async Task<OspfCaptureSnapshot> CaptureAsync(
        ITikConnection connection,
        OspfCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<OspfIpAddressRow> addresses = (await ReadIpAddressInventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false)).ToList();

        // Version-specific filter inventory: never issue the v6 menu on RouterOS 7 (or vice versa).
        List<OspfRoutingFilterRow> filters = request.SourceKind == OspfCaptureSourceKind.RouterOsV7
            ? await ReadV7RoutingFilterInventoryAsync(connection, cancellationToken).ConfigureAwait(false)
            : await ReadV6RoutingFilterInventoryAsync(connection, cancellationToken).ConfigureAwait(false);

        if (request.SourceKind == OspfCaptureSourceKind.RouterOsV6)
        {
            return await CaptureV6Async(connection, request, addresses, filters, cancellationToken)
                .ConfigureAwait(false);
        }

        return await CaptureV7Async(connection, request, addresses, filters, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OspfCaptureSnapshot> CaptureV6Async(
        ITikConnection connection,
        OspfCaptureRequest request,
        List<OspfIpAddressRow> addresses,
        List<OspfRoutingFilterRow> filters,
        CancellationToken cancellationToken)
    {
        List<OspfInstanceRow> instances = await PrintMappedAsync(
                connection,
                "/routing/ospf/instance/print",
                MapInstance,
                cancellationToken)
            .ConfigureAwait(false);

        List<OspfAreaRow> areas = await PrintMappedAsync(
                connection,
                "/routing/ospf/area/print",
                MapAreaV6,
                cancellationToken)
            .ConfigureAwait(false);

        List<OspfAreaRangeRow> ranges = await PrintMappedAsync(
                connection,
                "/routing/ospf/area/range/print",
                MapRange,
                cancellationToken)
            .ConfigureAwait(false);

        List<OspfNetworkRow> networks = await PrintMappedAsync(
                connection,
                "/routing/ospf/network/print",
                static row => new OspfNetworkRow
                {
                    Id = Field(row, ".id"),
                    Area = Field(row, "area"),
                    Network = Field(row, "network"),
                    Comment = Field(row, "comment"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        List<OspfInterfaceRow> interfaces = await PrintMappedAsync(
                connection,
                "/routing/ospf/interface/print",
                static row => new OspfInterfaceRow
                {
                    Id = Field(row, ".id"),
                    Interface = Field(row, "interface"),
                    NetworkType = Field(row, "network-type"),
                    Cost = Field(row, "cost"),
                    Passive = IsTruthy(Field(row, "passive")),
                },
                cancellationToken)
            .ConfigureAwait(false);

        List<OspfV3ObjectRef> v3 = await CaptureV6OspfV3Async(connection, cancellationToken)
            .ConfigureAwait(false);

        return new OspfCaptureSnapshot
        {
            ImportId = request.ImportId,
            JobId = request.JobId,
            RouterIp = request.RouterIp,
            Identity = request.Identity,
            RouterOsVersion = request.RouterOsVersion,
            Architecture = request.Architecture,
            SourceKind = OspfCaptureSourceKind.RouterOsV6,
            Instances = instances,
            Areas = areas,
            AreaRanges = ranges,
            Networks = networks,
            Interfaces = interfaces,
            IpAddresses = addresses,
            RoutingFilters = filters,
            OspfV3Objects = v3,
        };
    }

    private async Task<OspfCaptureSnapshot> CaptureV7Async(
        ITikConnection connection,
        OspfCaptureRequest request,
        List<OspfIpAddressRow> addresses,
        List<OspfRoutingFilterRow> filters,
        CancellationToken cancellationToken)
    {
        OspfV7Inventory inventory = await ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        return new OspfCaptureSnapshot
        {
            ImportId = request.ImportId,
            JobId = request.JobId,
            RouterIp = request.RouterIp,
            Identity = request.Identity,
            RouterOsVersion = request.RouterOsVersion,
            Architecture = request.Architecture,
            SourceKind = OspfCaptureSourceKind.RouterOsV7,
            Instances = inventory.Instances.Where(static i =>
                !string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase)).ToList(),
            Areas = inventory.Areas,
            AreaRanges = inventory.AreaRanges,
            InterfaceTemplates = inventory.Templates,
            IpAddresses = addresses,
            RoutingFilters = filters,
            OspfV3Objects = inventory.OspfV3Objects,
        };
    }

    private async Task<List<OspfV3ObjectRef>> CaptureV6OspfV3Async(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        string[] paths =
        [
            "/routing/ospf-v3/instance/print",
            "/routing/ospf-v3/area/print",
            "/routing/ospf-v3/area/range/print",
            "/routing/ospf-v3/interface/print",
            "/routing/ospf-v3/network/print",
        ];

        List<OspfV3ObjectRef> result = [];
        foreach (string path in paths)
        {
            try
            {
                IEnumerable<ITikReSentence> rows = await connection
                    .CreateCommand(path)
                    .ExecuteListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (ITikReSentence row in rows)
                {
                    result.Add(new OspfV3ObjectRef
                    {
                        Path = path.Replace("/print", string.Empty, StringComparison.Ordinal),
                        Id = Field(row, ".id"),
                        Name = Field(row, "name"),
                        Kind = path,
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsAbsentMenuException(ex))
            {
                // Unsupported or absent submenu is empty for that path only; keep scanning siblings.
                logger.LogInformation("OSPFv3 menu {Path} absent or unsupported on RouterOS v6; treating path as empty",
                    path);
                continue;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"OSPFv3 capture failed for {path}: {ex.Message}",
                    ex);
            }
        }

        return result;
    }

    public async Task<OspfV7Inventory> ReadV7InventoryAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default)
    {
        List<OspfInstanceRow> instances = await PrintMappedAsync(
                connection,
                "/routing/ospf/instance/print",
                MapInstance,
                cancellationToken)
            .ConfigureAwait(false);

        List<OspfAreaRow> areas = await PrintMappedAsync(
                connection,
                "/routing/ospf/area/print",
                MapAreaV7,
                cancellationToken)
            .ConfigureAwait(false);

        List<OspfAreaRangeRow> ranges = await PrintMappedAsync(
                connection,
                "/routing/ospf/area/range/print",
                MapRange,
                cancellationToken)
            .ConfigureAwait(false);

        List<OspfInterfaceTemplateRow> templates = await PrintMappedAsync(
                connection,
                "/routing/ospf/interface-template/print",
                static row => new OspfInterfaceTemplateRow
                {
                    Id = Field(row, ".id"),
                    Area = Field(row, "area"),
                    Interfaces = Field(row, "interfaces"),
                    Networks = Field(row, "networks") ?? Field(row, "network"),
                    Type = Field(row, "type"),
                    Cost = Field(row, "cost"),
                    // Match cleanup inventory mapping: ROS flag properties may print as present/empty.
                    Passive = IsFlagPresent(Field(row, "passive")) || IsTruthy(Field(row, "passive")),
                    Disabled = IsTruthy(Field(row, "disabled")),
                    Comment = Field(row, "comment"),
                    Auth = Field(row, "auth"),
                    AuthId = Field(row, "auth-id"),
                    AuthKey = Field(row, "auth-key"),
                    AuthenticationKey = Field(row, "authentication-key"),
                    Priority = Field(row, "priority"),
                    UseBfd = Field(row, "use-bfd"),
                    HelloInterval = Field(row, "hello-interval"),
                    DeadInterval = Field(row, "dead-interval"),
                    RetransmitInterval = Field(row, "retransmit-interval"),
                    TransmitDelay = Field(row, "transmit-delay"),
                    InstanceId = Field(row, "instance-id"),
                    PrefixList = Field(row, "prefix-list"),
                    VlinkNeighborId = Field(row, "vlink-neighbor-id"),
                    VlinkTransitArea = Field(row, "vlink-transit-area"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Always the RouterOS v7 rule menus — never the legacy /routing/filter table.
        List<OspfRoutingFilterRow> filters = await ReadV7RoutingFilterInventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        List<OspfV3ObjectRef> v3 = instances
            .Where(static i => string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase))
            .Select(static i => new OspfV3ObjectRef
            {
                Path = "/routing/ospf/instance", Id = i.Id, Name = i.Name, Kind = "instance-v3",
            })
            .ToList();

        return new OspfV7Inventory
        {
            Instances = instances,
            Areas = areas,
            AreaRanges = ranges,
            Templates = templates,
            Filters = filters,
            OspfV3Objects = v3,
        };
    }

    public async Task RemoveAllRoutingFiltersAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        RouterOsVersion version = await ReadRouterOsVersionOrThrowAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (version.IsV7OrLater)
        {
            await RemoveAllRowsByIdAsync(
                    connection,
                    OspfRoutingFilterMenus.V7FilterRulePrint,
                    OspfRoutingFilterMenus.V7FilterRuleRemove,
                    requiredMenu: true,
                    cancellationToken)
                .ConfigureAwait(false);
            await RemoveAllRowsByIdAsync(
                    connection,
                    OspfRoutingFilterMenus.V7FilterSelectRulePrint,
                    OspfRoutingFilterMenus.V7FilterSelectRuleRemove,
                    requiredMenu: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await RemoveAllRowsByIdAsync(
                connection,
                OspfRoutingFilterMenus.V6FilterPrint,
                OspfRoutingFilterMenus.V6FilterRemove,
                requiredMenu: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountRoutingFiltersAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        RouterOsVersion version = await ReadRouterOsVersionOrThrowAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (version.IsV7OrLater)
        {
            return (await ReadV7RoutingFilterInventoryAsync(connection, cancellationToken).ConfigureAwait(false))
                .Count;
        }

        return (await ReadV6RoutingFilterInventoryAsync(connection, cancellationToken).ConfigureAwait(false))
            .Count;
    }

    public async Task ClearOspfRedistributionAsync(
        ITikConnection connection,
        string instanceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/instance/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (ITikReSentence row in rows)
        {
            if (string.Equals(Field(row, "version"), "3", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(Field(row, "name"), instanceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? id = Field(row, ".id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            ITikCommand set = connection.CreateCommand("/routing/ospf/instance/set");
            set.AddParameter(".id", id);
            set.AddParameter("redistribute", "");
            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ClearOspfInstanceFilterReferencesAsync(
        ITikConnection connection,
        string instanceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/instance/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (ITikReSentence row in rows)
        {
            // Never mutate OSPFv3 instances here — dedicated RemoveOspfV3Async owns that path.
            if (string.Equals(Field(row, "version"), "3", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(Field(row, "name"), instanceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? id = Field(row, ".id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            // RouterOS 7 uses in-filter + out-filter-chain/out-filter-select.
            // Do NOT unset bare "out-filter" — it is ambiguous with out-filter-chain/select.
            string[] filterProperties =
            [
                "in-filter",
                "in-filter-chain",
                "out-filter-chain",
                "out-filter-select",
            ];

            foreach (string propertyName in filterProperties)
            {
                string? current = Field(row, propertyName);
                if (string.IsNullOrWhiteSpace(current))
                {
                    continue;
                }

                await UnsetPropertyAsync(
                        connection,
                        "/routing/ospf/instance/unset",
                        id,
                        propertyName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                "Cleared OSPFv2 instance filter references via unset (when set). Id={Id}, Name={Name}",
                id,
                Field(row, "name") ?? "<unnamed>");
        }
    }

    public async Task SetInstanceRouterIdAsync(
        ITikConnection connection,
        string instanceName,
        string routerId,
        CancellationToken cancellationToken = default)
    {
        string? id = await FindInstanceIdAsync(connection, instanceName, cancellationToken)
            .ConfigureAwait(false);
        if (id is null)
        {
            throw new InvalidOperationException($"OSPF instance '{instanceName}' not found.");
        }

        ITikCommand set = connection.CreateCommand("/routing/ospf/instance/set");
        set.AddParameter(".id", id);
        set.AddParameter("router-id", routerId);
        await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OspfIpAddressRow>> ReadIpAddressInventoryAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // Unfiltered /ip/address/print exposes all assigned IPv4 addresses, including those whose
        // interface is disabled when RouterOS still lists the address row.
        return await PrintMappedAsync(
                connection,
                "/ip/address/print",
                static row => new OspfIpAddressRow
                {
                    Id = Field(row, ".id"),
                    Address = Field(row, "address"),
                    Network = Field(row, "network"),
                    Interface = Field(row, "interface"),
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task EnsureOspfAreaAsync(
        ITikConnection connection,
        string instanceName,
        OspfPlanArea area,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/area/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        ITikReSentence? existing = rows.FirstOrDefault(r =>
            string.Equals(Field(r, "instance"), instanceName, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(Field(r, "name"), area.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Field(r, "area-id"), area.AreaId, StringComparison.Ordinal)));
        existing ??= rows.FirstOrDefault(r =>
            string.Equals(Field(r, "name"), area.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Field(r, "area-id"), area.AreaId, StringComparison.Ordinal));

        if (existing is null)
        {
            ITikCommand add = connection.CreateCommand("/routing/ospf/area/add");
            add.AddParameter("name", area.Name);
            add.AddParameter("area-id", area.AreaId);
            add.AddParameter("instance", instanceName);
            add.AddParameter("type", area.TargetType);
            if (area.DefaultCost is not null)
            {
                add.AddParameter("default-cost", area.DefaultCost);
            }

            // Never set no-summaries on add — fleet stub areas must permit inter-area summaries.
            await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "OSPF area added name={Name} default-cost={DefaultCost} source-default-cost={SourceDefaultCost}",
                area.Name,
                area.DefaultCost ?? "<unset>",
                area.SourceDefaultCost ?? "<unset>");
            return;
        }

        string areaId = Field(existing, ".id")!;
        ITikCommand set = connection.CreateCommand("/routing/ospf/area/set");
        set.AddParameter(".id", areaId);
        set.AddParameter("type", area.TargetType);
        if (area.DefaultCost is not null)
        {
            set.AddParameter("default-cost", area.DefaultCost);
        }

        await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // no-summaries is a flag (()). Setting =no enables it. Clear via /unset only. Never enable.
        if (!area.NoSummaries && IsFlagPresent(Field(existing, "no-summaries")))
        {
            await UnsetPropertyAsync(
                    connection,
                    "/routing/ospf/area/unset",
                    areaId,
                    "no-summaries",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (area.NoSummaries)
        {
            throw new InvalidOperationException(
                $"Refusing to enable no-summaries on area '{area.Name}'; fleet policy requires it absent.");
        }

        if (area.DefaultCost is null)
        {
            // RouterOS API unset via /routing/ospf/area/unset =value-name=default-cost (tik4net UnsetValueName).
            await UnsetPropertyAsync(
                    connection,
                    "/routing/ospf/area/unset",
                    areaId,
                    "default-cost",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "OSPF area updated name={Name} default-cost={DefaultCost} source-default-cost={SourceDefaultCost}",
            area.Name,
            area.DefaultCost ?? "<unset>",
            area.SourceDefaultCost ?? "<unset>");
    }

    public async Task EnsureOspfAreaRangeAsync(
        ITikConnection connection,
        OspfPlanAreaRange range,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/area/range/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool exists = rows.Any(r =>
            string.Equals(Field(r, "area"), range.AreaName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadAreaRangePrefix(r)?.Trim(), range.RangeCidr, StringComparison.Ordinal));
        if (exists)
        {
            return;
        }

        ITikCommand add = connection.CreateCommand("/routing/ospf/area/range/add");
        add.AddParameter("area", range.AreaName);
        // RouterOS 7 uses prefix= (v6 export syntax used range=).
        add.AddParameter("prefix", range.RangeCidr);
        if (!string.IsNullOrWhiteSpace(range.Comment))
        {
            add.AddParameter("comment", range.Comment);
        }

        await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureBackhaulTemplateAsync(
        ITikConnection connection,
        OspfPlanBackhaulTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Passive)
        {
            throw new OspfBackboneMutationGuardException(
                $"Refusing backhaul ensure for '{template.Interface}': plan Passive must be false.");
        }

        if (template.Cost != OspfMigrationPlanner.FleetBackhaulCost)
        {
            throw new OspfBackboneMutationGuardException(
                $"Refusing backhaul ensure for '{template.Interface}': cost must be {OspfMigrationPlanner.FleetBackhaulCost}.");
        }

        List<ITikReSentence> rows = (await connection
                .CreateCommand("/routing/ospf/interface-template/print")
                .ExecuteListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToList();

        List<ITikReSentence> matches = rows
            .Where(r =>
                string.Equals(Field(r, "area"), template.AreaName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Field(r, "interfaces"), template.Interface, StringComparison.Ordinal)
                && string.Equals(NormalizeCidr(Field(r, "networks")), NormalizeCidr(template.NetworkCidr),
                    StringComparison.Ordinal))
            .ToList();

        if (matches.Count > 1)
        {
            throw new OspfBackboneMutationGuardException(
                $"Ambiguous backhaul template match for '{template.Interface}' {template.NetworkCidr} (count={matches.Count}); fail-closed.");
        }

        if (matches.Count == 1)
        {
            ITikReSentence existing = matches[0];
            string id = Field(existing, ".id")
                        ?? throw new OspfBackboneMutationGuardException("Backhaul template .id missing.");

            // CRITICAL: passive on interface-template is a flag (()). Presence enables it.
            // NEVER send =passive=… on an active backhaul set/add. Clear only via /unset.
            bool needsPassiveClear = IsFlagPresent(Field(existing, "passive"));
            bool disabled = IsTruthy(Field(existing, "disabled"));
            string? type = Field(existing, "type");
            bool typeOk = string.Equals(type, "ptp", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(type, "point-to-point", StringComparison.OrdinalIgnoreCase);
            bool costOk = string.Equals(
                Field(existing, "cost"),
                template.Cost.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

            if (needsPassiveClear)
            {
                logger.LogWarning(
                    "Backhaul template .id={Id} is currently passive; clearing flag via unset before asserting active state",
                    id);
                await UnsetPropertyAsync(
                        connection,
                        "/routing/ospf/interface-template/unset",
                        id,
                        "passive",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (needsPassiveClear || disabled || !typeOk || !costOk)
            {
                ITikCommand set = connection.CreateCommand("/routing/ospf/interface-template/set");
                set.AddParameter(".id", id);
                set.AddParameter("disabled", "no");
                set.AddParameter("type", "ptp");
                set.AddParameter("cost", template.Cost.ToString(CultureInfo.InvariantCulture));
                // Intentionally omit passive — sending passive=no enables the flag on ROS7.
                await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AssertBackhaulTemplateActiveAsync(connection, template, id, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ITikCommand add = connection.CreateCommand("/routing/ospf/interface-template/add");
        add.AddParameter("area", template.AreaName);
        add.AddParameter("interfaces", template.Interface);
        add.AddParameter("networks", template.NetworkCidr);
        add.AddParameter("type", "ptp");
        add.AddParameter("cost", template.Cost.ToString(CultureInfo.InvariantCulture));
        add.AddParameter("disabled", "no");
        // Do not add passive parameter — default is active.
        await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await AssertBackhaulTemplateActiveAsync(connection, template, expectedId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task EnsureLoopbackTemplateAsync(
        ITikConnection connection,
        OspfPlanLoopbackTemplate template,
        IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!template.Passive)
        {
            throw new InvalidOperationException($"Loopback '{template.NetworkCidr}' must be Passive=true.");
        }

        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/interface-template/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ITikReSentence> matches = rows
            .Where(r => MatchesLoopbackTemplateRow(r, template))
            .ToList();

        if (matches.Count > 1)
        {
            throw new OspfBackboneMutationGuardException(
                $"Ambiguous loopback template match for {template.NetworkCidr} (count={matches.Count}); fail-closed.");
        }

        if (matches.Count == 1)
        {
            ITikReSentence existing = matches[0];
            string id = Field(existing, ".id")
                        ?? throw new OspfBackboneMutationGuardException("Loopback template .id missing.");
            ForbidProtectedBackhaulMutation(id, existing, protectedBackhauls, "loopback ensure");

            string? currentType = Field(existing, "type");
            // RouterOS 7 rejects /unset value-name=type. Recreate only when an explicit PTP type was forced.
            // Default print values such as broadcast must not trigger remove/recreate every run.
            // Make-before-break is NOT used: overlapping interface-template matchers (interfaces=/networks=)
            // can apply concurrently under OR-style matching, and AssertLoopback requires exactly one row.
            bool forcedPtp = !string.IsNullOrWhiteSpace(currentType)
                             && (currentType.Equals("ptp", StringComparison.OrdinalIgnoreCase)
                                 || currentType.Equals("point-to-point", StringComparison.OrdinalIgnoreCase));
            if (forcedPtp)
            {
                await RecreateLoopbackWithoutForcedPtpAsync(
                        connection,
                        template,
                        id,
                        currentType!,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            bool disabled = IsTruthy(Field(existing, "disabled"));
            bool passiveOk = IsFlagPresent(Field(existing, "passive")) || IsTruthy(Field(existing, "passive"));
            if (disabled || !passiveOk)
            {
                ITikCommand set = connection.CreateCommand("/routing/ospf/interface-template/set");
                set.AddParameter(".id", id);
                set.AddParameter("disabled", "no");
                SetFlagEnabled(set, "passive");
                await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AssertLoopbackTemplateStateAsync(connection, template, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await AddPassiveLoopbackTemplateAsync(connection, template, cancellationToken)
            .ConfigureAwait(false);
        await AssertLoopbackTemplateStateAsync(connection, template, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Remove-then-recreate for forced PTP Loopbacks. Stages are logged explicitly so a mid-sequence
    /// failure can be diagnosed without replaying stale <c>.id</c> values.
    /// </summary>
    private async Task RecreateLoopbackWithoutForcedPtpAsync(
        ITikConnection connection,
        OspfPlanLoopbackTemplate template,
        string removedId,
        string priorType,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Loopback PTP clear stage=remove-begin .id={Id} type={Type} area={Area} interfaces={If} networks={Net}",
            removedId,
            priorType,
            template.AreaName,
            template.Interface,
            template.NetworkCidr);

        try
        {
            await RemoveByIdAsync(
                    connection,
                    "/routing/ospf/interface-template/remove",
                    removedId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback recreate stage=remove-failed .id={removedId} type={priorType}; "
                + "old template may still be present. Re-read inventory before any retry.",
                ex);
        }

        logger.LogWarning(
            "Loopback PTP clear stage=removed .id={Id}; Loopback OSPF advertisement may be absent until replacement is verified",
            removedId);

        try
        {
            await AddPassiveLoopbackTemplateAsync(connection, template, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback recreate stage=add-failed after-remove .id={removedId} type={priorType}; "
                + "Loopback advertisement is absent. Re-read complete inventory and derive next operation from actual state; "
                + "do not replay using the stale removed .id.",
                ex);
        }

        logger.LogInformation(
            "Loopback PTP clear stage=added after-remove prior.id={Id}; verifying replacement",
            removedId);

        try
        {
            await AssertLoopbackTemplateStateAsync(connection, template, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback recreate stage=verify-failed after-remove-and-add prior.id={removedId}; "
                + "replacement missing or incorrect. Re-read inventory before retry; do not mark migration successful.",
                ex);
        }

        logger.LogInformation(
            "Loopback PTP clear stage=verified prior.id={Id} networks={Net}",
            removedId,
            template.NetworkCidr);
    }

    private static async Task AddPassiveLoopbackTemplateAsync(
        ITikConnection connection,
        OspfPlanLoopbackTemplate template,
        CancellationToken cancellationToken)
    {
        ITikCommand add = connection.CreateCommand("/routing/ospf/interface-template/add");
        add.AddParameter("area", template.AreaName);
        add.AddParameter("networks", template.NetworkCidr);
        if (!string.IsNullOrWhiteSpace(template.Interface))
        {
            add.AddParameter("interfaces", template.Interface);
        }

        SetFlagEnabled(add, "passive");
        // Do not set type — never force PTP on loopbacks.
        await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureSiteNetworkTemplateAsync(
        ITikConnection connection,
        OspfPlanSiteNetworkTemplate template,
        IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!template.Passive)
        {
            throw new InvalidOperationException($"Site matcher '{template.NetworkCidr}' must be Passive=true.");
        }

        if (string.IsNullOrWhiteSpace(template.Interfaces))
        {
            throw new InvalidOperationException(
                $"Site matcher '{template.NetworkCidr}' must declare an interfaces matcher.");
        }

        await EnsurePlannedSiteTemplateAsync(
                connection,
                template,
                protectedBackhauls,
                cancellationToken)
            .ConfigureAwait(false);

        await AssertSiteNetworkTemplateStateAsync(connection, template, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task AssertSiteNetworkTemplateStateAsync(
        ITikConnection connection,
        OspfPlanSiteNetworkTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(template);
        cancellationToken.ThrowIfCancellationRequested();

        if (!template.Passive)
        {
            throw new InvalidOperationException(
                $"Site matcher '{template.NetworkCidr}' assertion requires Passive=true.");
        }

        if (!OspfSiteMatcherConsolidation.IsAllInterfaces(template.Interfaces)
            && string.IsNullOrWhiteSpace(template.Interfaces))
        {
            throw new InvalidOperationException(
                $"Site matcher '{template.NetworkCidr}' assertion requires a non-empty interfaces matcher.");
        }

        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/interface-template/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ITikReSentence> matches = rows
            .Where(r =>
                OspfSiteMatcherConsolidation.MatchesPlannedSite(
                    Field(r, "area"),
                    Field(r, "interfaces"),
                    Field(r, "networks"),
                    IsFlagPresent(Field(r, "passive")) || IsTruthy(Field(r, "passive")),
                    IsTruthy(Field(r, "disabled")),
                    template))
            .ToList();

        if (matches.Count == 0)
        {
            throw new OspfBackboneMutationGuardException(
                $"Site template assertion failed: planned matcher area='{template.AreaName}' "
                + $"interfaces='{template.Interfaces}' networks='{template.NetworkCidr}' is absent on readback. "
                + "Cleanup must not start.");
        }

        if (matches.Count > 1)
        {
            throw new OspfBackboneMutationGuardException(
                $"Site template assertion failed: planned matcher area='{template.AreaName}' "
                + $"interfaces='{template.Interfaces}' networks='{template.NetworkCidr}' matches {matches.Count} live rows. "
                + "Cleanup must not start.");
        }

        ITikReSentence live = matches[0];
        string? liveId = Field(live, ".id");
        if (string.IsNullOrWhiteSpace(liveId))
        {
            throw new OspfBackboneMutationGuardException(
                $"Site template assertion failed: matching row for '{template.NetworkCidr}' has no .id. "
                + "Cleanup must not start.");
        }

        if (!string.Equals(Field(live, "area"), template.AreaName, StringComparison.OrdinalIgnoreCase))
        {
            throw new OspfBackboneMutationGuardException(
                $"Site template assertion failed (.id={liveId}): area='{Field(live, "area")}' expected '{template.AreaName}'. "
                + "Cleanup must not start.");
        }

        if (!OspfSiteMatcherConsolidation.InterfacesEqual(Field(live, "interfaces"), template.Interfaces))
        {
            throw new OspfBackboneMutationGuardException(
                $"Site template assertion failed (.id={liveId}): interfaces='{Field(live, "interfaces")}' "
                + $"expected '{template.Interfaces}'. Cleanup must not start.");
        }

        if (!OspfNetworkList.SetsEqual(Field(live, "networks"), template.NetworkCidr))
        {
            throw new OspfBackboneMutationGuardException(
                $"Site template assertion failed (.id={liveId}): networks='{Field(live, "networks")}' "
                + $"expected exact set '{template.NetworkCidr}'. Cleanup must not start.");
        }

        if (!IsFlagPresent(Field(live, "passive")) && !IsTruthy(Field(live, "passive")))
        {
            throw new OspfBackboneMutationGuardException(
                $"Site template assertion failed (.id={liveId}): passive is absent. Cleanup must not start.");
        }

        if (IsTruthy(Field(live, "disabled")))
        {
            throw new OspfBackboneMutationGuardException(
                $"Site template assertion failed (.id={liveId}): template is disabled. Cleanup must not start.");
        }

        string? type = Field(live, "type");
        if (!string.IsNullOrWhiteSpace(type)
            && (type.Equals("ptp", StringComparison.OrdinalIgnoreCase)
                || type.Equals("point-to-point", StringComparison.OrdinalIgnoreCase)))
        {
            throw new OspfBackboneMutationGuardException(
                $"Site template assertion failed (.id={liveId}): unexpected explicit PTP type '{type}'. "
                + "Cleanup must not start.");
        }

        logger.LogInformation(
            "Site template asserted .id={Id} area={Area} interfaces={If} networks={Net}",
            liveId,
            template.AreaName,
            template.Interfaces,
            template.NetworkCidr);
    }

    public async Task RemoveObsoleteTemplatesAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        CancellationToken cancellationToken = default)
    {
        // 1–11: classify complete inventory and abort before ANY deletion if unknowns exist.
        List<OspfTemplateInventoryRow> initial = await PrintTemplateInventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        OspfTemplateClassificationReport initialReport = OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, initial);

        List<string> legacyFingerprints = initialReport.KnownLegacy
            .Select(static l => OspfTemplateCleanupClassifier.LegacyFingerprint(l.Row))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        HashSet<string> protectedIds = OspfTemplateCleanupClassifier.ProtectedIds(initialReport);
        int expectedPlannedCount = initialReport.PlannedProtectedCount;

        foreach (string fingerprint in legacyFingerprints)
        {
            List<OspfTemplateInventoryRow> live = await PrintTemplateInventoryAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            OspfTemplateClassificationReport liveReport = OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, live);

            HashSet<string> liveProtected = OspfTemplateCleanupClassifier.ProtectedIds(liveReport);
            if (!protectedIds.SetEquals(liveProtected))
            {
                throw new OspfBackboneMutationGuardException(
                    "Protected OSPF template identity set changed during cleanup; aborting further deletions.");
            }

            List<OspfClassifiedTemplate> candidates = liveReport.KnownLegacy
                .Where(l => string.Equals(
                    OspfTemplateCleanupClassifier.LegacyFingerprint(l.Row),
                    fingerprint,
                    StringComparison.Ordinal))
                .ToList();

            if (candidates.Count == 0)
            {
                logger.LogInformation(
                    "Known-legacy OSPF template fingerprint already absent before remove ({Fingerprint})",
                    fingerprint);
                continue;
            }

            if (candidates.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Known-legacy OSPF template fingerprint matches {candidates.Count} live rows during cleanup; aborting. "
                    + string.Join(" | ", candidates.Select(static c => c.Row.Describe())));
            }

            OspfClassifiedTemplate candidate = candidates[0];
            string currentId = candidate.Row.Id
                               ?? throw new InvalidOperationException(
                                   "Known-legacy template missing .id after refresh; aborting.");

            if (protectedIds.Contains(currentId)
                || candidate.Classification != OspfTemplateClassification.KnownLegacy)
            {
                throw new OspfBackboneMutationGuardException(
                    $"Refusing remove of .id={currentId}: refreshed classification is {candidate.Classification} ({candidate.Row.Describe()}).");
            }

            await RemoveByIdAsync(
                    connection,
                    "/routing/ospf/interface-template/remove",
                    currentId,
                    cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Removed known-legacy OSPF template .id={Id} interfaces={If} networks={Net}",
                currentId,
                candidate.Row.Interfaces,
                candidate.Row.Networks);

            List<OspfTemplateInventoryRow> after = await PrintTemplateInventoryAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (after.Any(r => string.Equals(r.Id, currentId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Known-legacy OSPF template .id={currentId} still present after remove.");
            }

            if (after.Any(r => string.Equals(
                    OspfTemplateCleanupClassifier.LegacyFingerprint(r),
                    fingerprint,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Known-legacy OSPF template fingerprint still present after remove of .id={currentId}.");
            }
        }

        List<OspfTemplateInventoryRow> finalRows = await PrintTemplateInventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        OspfTemplateClassificationReport finalReport = OspfTemplateCleanupClassifier.ClassifyOrThrow(plan, finalRows);
        if (finalRows.Count != expectedPlannedCount
            || finalReport.KnownLegacy.Count != 0
            || finalReport.Unknown.Count != 0
            || finalReport.PlannedProtectedCount != expectedPlannedCount)
        {
            throw new InvalidOperationException(
                $"OSPF template cleanup final inventory invalid: count={finalRows.Count}, "
                + $"planned-protected={finalReport.PlannedProtectedCount}, "
                + $"legacy={finalReport.KnownLegacy.Count}, unknown={finalReport.Unknown.Count}, "
                + $"expected planned count={expectedPlannedCount}.");
        }
    }

    private async Task<List<OspfTemplateInventoryRow>> PrintTemplateInventoryAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> areaInstances = await PrintAreaInstanceMapAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/interface-template/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => MapTemplateInventoryRow(r, areaInstances)).ToList();
    }

    private async Task<Dictionary<string, string>> PrintAreaInstanceMapAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> areas = await connection
            .CreateCommand("/routing/ospf/area/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (ITikReSentence area in areas)
        {
            string? name = Field(area, "name");
            string? instance = Field(area, "instance");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(instance))
            {
                continue;
            }

            map[name] = instance;
        }

        return map;
    }

    private static OspfTemplateInventoryRow MapTemplateInventoryRow(
        ITikReSentence row,
        IReadOnlyDictionary<string, string> areaInstances)
    {
        OspfTemplateSemanticIdentity.AuthKeyResolution authKey =
            OspfTemplateSemanticIdentity.ResolveAuthKey(
                Field(row, "auth-key"),
                Field(row, "authentication-key"));
        string? area = Field(row, "area");
        string? areaInstance = null;
        if (!string.IsNullOrWhiteSpace(area)
            && areaInstances.TryGetValue(area, out string? resolved))
        {
            areaInstance = resolved;
        }

        return new OspfTemplateInventoryRow
        {
            Id = Field(row, ".id"),
            Area = area,
            Interfaces = Field(row, "interfaces"),
            Networks = Field(row, "networks") ?? Field(row, "network"),
            Type = Field(row, "type"),
            Cost = Field(row, "cost"),
            Passive = IsFlagPresent(Field(row, "passive")) || IsTruthy(Field(row, "passive")),
            Disabled = IsTruthy(Field(row, "disabled")),
            Auth = Field(row, "auth"),
            AuthId = Field(row, "auth-id"),
            AuthKeyPresent = authKey.Present,
            AuthKeyFingerprint = authKey.Fingerprint,
            AuthKeyAmbiguous = authKey.Ambiguous,
            Priority = Field(row, "priority"),
            UseBfd = Field(row, "use-bfd"),
            HelloInterval = Field(row, "hello-interval"),
            DeadInterval = Field(row, "dead-interval"),
            RetransmitInterval = Field(row, "retransmit-interval"),
            TransmitDelay = Field(row, "transmit-delay"),
            InstanceId = Field(row, "instance-id"),
            AreaInstanceName = areaInstance,
            PrefixList = Field(row, "prefix-list"),
            VlinkNeighborId = Field(row, "vlink-neighbor-id"),
            VlinkTransitArea = Field(row, "vlink-transit-area"),
            Comment = OspfTemplateSemanticIdentity.NormalizeComment(Field(row, "comment")),
        };
    }

    private static bool? ParseOptionalBool(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0)
        {
            return null;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value == "1")
        {
            return true;
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value == "0")
        {
            return false;
        }

        return null;
    }

    public async Task RemoveOspfV3Async(
        ITikConnection connection,
        OspfMigrationPlan plan,
        CancellationToken cancellationToken = default)
    {
        _ = plan;
        OspfV7Inventory inventory = await ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        OspfV3RemovalSelection.Result selection = OspfV3RemovalSelection.Build(inventory);
        if (!selection.Succeeded)
        {
            throw new InvalidOperationException(
                selection.FailureDetail ?? "OSPFv3 removal selection failed closed.");
        }

        foreach (string id in selection.TemplateIds)
        {
            await RemoveByIdAsync(connection, "/routing/ospf/interface-template/remove", id, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (string id in selection.RangeIds)
        {
            await RemoveByIdAsync(connection, "/routing/ospf/area/range/remove", id, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (string id in selection.AreaIds)
        {
            await RemoveByIdAsync(connection, "/routing/ospf/area/remove", id, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (string id in selection.InstanceIds)
        {
            await RemoveByIdAsync(connection, "/routing/ospf/instance/remove", id, cancellationToken)
                .ConfigureAwait(false);
        }

        if (selection.InstanceIds.Count > 0)
        {
            logger.LogInformation(
                "Removed OSPFv3 objects by .id (templates={Templates}, ranges={Ranges}, areas={Areas}, instances={Instances})",
                selection.TemplateIds.Count,
                selection.RangeIds.Count,
                selection.AreaIds.Count,
                selection.InstanceIds.Count);
        }
    }

    /// <summary>
    /// Clears a RouterOS property using the API <c>/…/unset</c> command with
    /// <see cref="TikSpecialProperties.UnsetValueName"/> (=value-name=…).
    /// </summary>
    private static async Task UnsetPropertyAsync(
        ITikConnection connection,
        string unsetCommand,
        string id,
        string propertyName,
        CancellationToken cancellationToken)
    {
        ITikCommand unset = connection.CreateCommand(unsetCommand);
        unset.AddParameter(".id", id);
        unset.AddParameter(TikSpecial.UnsetValueName, propertyName);
        await unset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsurePlannedSiteTemplateAsync(
        ITikConnection connection,
        OspfPlanSiteNetworkTemplate template,
        IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/interface-template/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ITikReSentence> exact = rows
            .Where(r =>
                OspfSiteMatcherConsolidation.MatchesPlannedSite(
                    Field(r, "area"),
                    Field(r, "interfaces"),
                    Field(r, "networks"),
                    IsFlagPresent(Field(r, "passive")) || IsTruthy(Field(r, "passive")),
                    IsTruthy(Field(r, "disabled")),
                    template))
            .ToList();

        // For interfaces=all plans, allow a single area-scoped all-interfaces row to be corrected.
        List<ITikReSentence> areaAll = OspfSiteMatcherConsolidation.IsAllInterfaces(template.Interfaces)
            ? rows
                .Where(r =>
                    string.Equals(Field(r, "area"), template.AreaName, StringComparison.OrdinalIgnoreCase)
                    && OspfSiteMatcherConsolidation.IsAllInterfaces(Field(r, "interfaces")))
                .ToList()
            : [];

        ITikReSentence? existing = exact.Count == 1
            ? exact[0]
            : exact.Count == 0 && areaAll.Count == 1
                ? areaAll[0]
                : null;

        if (exact.Count > 1 || (exact.Count == 0 && areaAll.Count > 1))
        {
            throw new InvalidOperationException(
                $"Ambiguous site template match for {template.AreaName} interfaces={template.Interfaces} networks={template.NetworkCidr}; fail-closed.");
        }

        if (existing is not null)
        {
            string id = Field(existing, ".id")!;
            ForbidProtectedBackhaulMutation(id, existing, protectedBackhauls, "site ensure");

            bool disabled = IsTruthy(Field(existing, "disabled"));
            bool passiveOk = IsFlagPresent(Field(existing, "passive")) || IsTruthy(Field(existing, "passive"));
            bool interfacesOk = OspfSiteMatcherConsolidation.InterfacesEqual(
                Field(existing, "interfaces"),
                template.Interfaces);
            bool networksOk = OspfNetworkList.SetsEqual(Field(existing, "networks"), template.NetworkCidr);
            bool commentOk = string.IsNullOrWhiteSpace(template.Comment)
                             || string.Equals(Field(existing, "comment"), template.Comment, StringComparison.Ordinal);

            if (disabled || !passiveOk || !interfacesOk || !networksOk || !commentOk)
            {
                ITikCommand set = connection.CreateCommand("/routing/ospf/interface-template/set");
                set.AddParameter(".id", id);
                set.AddParameter("disabled", "no");
                set.AddParameter("interfaces", template.Interfaces);
                set.AddParameter("networks", template.NetworkCidr);
                SetFlagEnabled(set, "passive");
                if (!string.IsNullOrWhiteSpace(template.Comment))
                {
                    set.AddParameter("comment", template.Comment);
                }

                await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        ITikCommand add = connection.CreateCommand("/routing/ospf/interface-template/add");
        add.AddParameter("area", template.AreaName);
        add.AddParameter("interfaces", template.Interfaces);
        add.AddParameter("networks", template.NetworkCidr);
        SetFlagEnabled(add, "passive");
        if (!string.IsNullOrWhiteSpace(template.Comment))
        {
            add.AddParameter("comment", template.Comment);
        }

        await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OspfManagementPlaneBaseline> CaptureManagementPlaneBaselineAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        string managementHost,
        CancellationToken cancellationToken = default)
    {
        OspfPlanBackhaulTemplate? bh = plan.ActiveBackhauls.FirstOrDefault();
        OspfV7Inventory inventory = await ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        OspfInterfaceTemplateRow? liveBh = bh is null
            ? null
            : inventory.Templates.FirstOrDefault(t => OspfBackboneSafety.IsDesignatedBackhaulTemplate(t, bh));

        IReadOnlyList<Dictionary<string, string>> neighbours =
            await ReadOspfNeighboursAsync(connection, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string>? full = neighbours.FirstOrDefault(n =>
            string.Equals(GetDict(n, "state"), "Full", StringComparison.OrdinalIgnoreCase));

        string? neighbourAddress = full is null
            ? GetDict(neighbours.FirstOrDefault(), "address")
            : GetDict(full, "address");
        string? neighbourRouterId = full is null
            ? GetDict(neighbours.FirstOrDefault(), "router-id")
            : GetDict(full, "router-id");

        ManagementRouteObservation route = await ObserveManagementRouteAsync(
                connection,
                managementHost,
                cancellationToken)
            .ConfigureAwait(false);

        bool dependsOnBackbone = RouteDependsOnBackbone(
            route,
            bh?.Interface,
            neighbourAddress);

        return new OspfManagementPlaneBaseline
        {
            ManagementHost = managementHost,
            BackboneInterface = bh?.Interface ?? liveBh?.Interfaces,
            BackboneTemplateId = liveBh?.Id,
            BackboneArea = liveBh?.Area ?? bh?.AreaName,
            BackboneNetwork = liveBh?.Networks ?? bh?.NetworkCidr,
            BackboneWasActive = liveBh is { Disabled: false, Passive: false },
            BackboneTemplatePassive = liveBh?.Passive ?? false,
            BackboneTemplateDisabled = liveBh?.Disabled ?? false,
            NeighbourAddress = neighbourAddress,
            NeighbourRouterId = neighbourRouterId,
            NeighbourState = full is null ? GetDict(neighbours.FirstOrDefault(), "state") : GetDict(full, "state"),
            NeighbourWasFull = full is not null,
            ManagementRouteDst = route.DstAddress,
            ManagementRouteGateway = route.Gateway,
            ManagementRouteImmediateGw = route.ImmediateGw,
            ManagementOutgoingInterface = route.OutgoingInterface,
            ManagementRouteActive = route.Active,
            ManagementRouteObserved = route.Observed,
            ManagementRouteDependsOnBackbone = dependsOnBackbone,
        };
    }

    public async Task AssertBackboneStillActiveAsync(
        ITikConnection connection,
        OspfMigrationPlan plan,
        OspfManagementPlaneBaseline baseline,
        CancellationToken cancellationToken = default)
    {
        foreach (OspfPlanBackhaulTemplate bh in plan.ActiveBackhauls)
        {
            // Apply/Verify remap logical ROS6 area names (e.g. backbone) onto live ROS7
            // names (backbone-v2). Post-verify callers may still pass the unmapped plan;
            // match the intended live backbone area rather than requiring the capture name.
            OspfPlanBackhaulTemplate assertBh = RemapBackhaulAreaToLiveTarget(plan, bh);
            await AssertBackhaulTemplateActiveAsync(connection, assertBh, expectedId: null, cancellationToken)
                .ConfigureAwait(false);
        }

        if (baseline.NeighbourWasFull)
        {
            IReadOnlyList<Dictionary<string, string>> neighbours =
                await ReadOspfNeighboursAsync(connection, cancellationToken).ConfigureAwait(false);
            bool stillFull = neighbours.Any(n =>
                string.Equals(GetDict(n, "state"), "Full", StringComparison.OrdinalIgnoreCase)
                && (baseline.NeighbourAddress is null
                    || string.Equals(GetDict(n, "address"), baseline.NeighbourAddress, StringComparison.Ordinal))
                && (baseline.NeighbourRouterId is null
                    || string.Equals(
                        GetDict(n, "router-id"),
                        baseline.NeighbourRouterId,
                        StringComparison.Ordinal)));

            if (!stillFull)
            {
                throw new OspfBackboneMutationGuardException(
                    $"OSPF neighbour dropped from Full after backbone mutation (prior address={baseline.NeighbourAddress}, router-id={baseline.NeighbourRouterId}). Aborting further OSPF mutations.");
            }
        }

        if (!baseline.ManagementRouteObserved)
        {
            // Route was not observable at baseline — neighbour Full alone is never treated as
            // management reachability proof.
            return;
        }

        ManagementRouteObservation liveRoute = await ObserveManagementRouteAsync(
                connection,
                baseline.ManagementHost,
                cancellationToken)
            .ConfigureAwait(false);

        if (!liveRoute.Observed || !liveRoute.Active)
        {
            throw new OspfBackboneMutationGuardException(
                $"Management route to '{baseline.ManagementHost}' missing or inactive after mutation (baseline dst={baseline.ManagementRouteDst}). Aborting further OSPF mutations.");
        }

        if (!string.Equals(liveRoute.DstAddress, baseline.ManagementRouteDst, StringComparison.Ordinal)
            || !string.Equals(
                liveRoute.Gateway ?? liveRoute.ImmediateGw,
                baseline.ManagementRouteGateway ?? baseline.ManagementRouteImmediateGw,
                StringComparison.Ordinal)
            || !string.Equals(
                liveRoute.OutgoingInterface,
                baseline.ManagementOutgoingInterface,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new OspfBackboneMutationGuardException(
                $"Management route to '{baseline.ManagementHost}' changed after mutation "
                + $"(dst {baseline.ManagementRouteDst}->{liveRoute.DstAddress}, "
                + $"gw {baseline.ManagementRouteGateway}->{liveRoute.Gateway}, "
                + $"outIf {baseline.ManagementOutgoingInterface}->{liveRoute.OutgoingInterface}). "
                + "Aborting further OSPF mutations. Note: this is route verification, not end-to-end connectivity proof.");
        }
    }

    public async Task<IReadOnlyList<Dictionary<string, string>>> ReadOspfNeighboursAsync(
        ITikConnection connection,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/neighbor/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows
            .Select(static r => r.Words.ToDictionary(static kv => kv.Key, static kv => kv.Value))
            .ToList();
    }

    private async Task AssertBackhaulTemplateActiveAsync(
        ITikConnection connection,
        OspfPlanBackhaulTemplate template,
        string? expectedId,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/interface-template/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        ITikReSentence? live = rows.FirstOrDefault(r =>
            (expectedId is null || string.Equals(Field(r, ".id"), expectedId, StringComparison.Ordinal))
            && string.Equals(Field(r, "area"), template.AreaName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Field(r, "interfaces"), template.Interface, StringComparison.Ordinal)
            && string.Equals(
                NormalizeCidr(ReadNetworksField(r)),
                NormalizeCidr(template.NetworkCidr),
                StringComparison.Ordinal));

        if (live is null)
        {
            throw new OspfBackboneMutationGuardException(
                $"Backhaul template '{template.Interface}' {template.NetworkCidr} missing after mutation.");
        }

        if (IsFlagPresent(Field(live, "passive")))
        {
            throw new OspfBackboneMutationGuardException(
                $"Backbone safety failure: backhaul .id={Field(live, ".id")} is passive after ensure (RouterOS flag).");
        }

        string? disabled = Field(live, "disabled");
        bool isDisabled = disabled is not null
                          && !string.Equals(disabled, "no", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(disabled, "false", StringComparison.OrdinalIgnoreCase);
        if (isDisabled)
        {
            throw new OspfBackboneMutationGuardException(
                $"Backbone safety failure: backhaul .id={Field(live, ".id")} is disabled after ensure.");
        }

        if (!string.Equals(Field(live, "cost"), template.Cost.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new OspfBackboneMutationGuardException(
                $"Backbone safety failure: backhaul cost is '{Field(live, "cost")}', expected {template.Cost}.");
        }
    }

    private static void ForbidProtectedBackhaulMutation(
        string id,
        ITikReSentence row,
        IReadOnlyList<OspfPlanBackhaulTemplate> protectedBackhauls,
        string operation)
    {
        foreach (OspfPlanBackhaulTemplate bh in protectedBackhauls)
        {
            if (string.Equals(Field(row, "interfaces"), bh.Interface, StringComparison.Ordinal)
                && string.Equals(NormalizeCidr(Field(row, "networks")), NormalizeCidr(bh.NetworkCidr),
                    StringComparison.Ordinal))
            {
                throw new OspfBackboneMutationGuardException(
                    $"Refusing {operation} on .id={id}: matches protected active backhaul '{bh.Interface}' {bh.NetworkCidr}.");
            }
        }
    }

    private async Task AssertLoopbackTemplateStateAsync(
        ITikConnection connection,
        OspfPlanLoopbackTemplate template,
        CancellationToken cancellationToken)
    {
        // Independent print — never treat the preceding add/set response as proof.
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/interface-template/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ITikReSentence> matches = rows
            .Where(r => MatchesLoopbackTemplateRow(r, template))
            .ToList();

        if (matches.Count == 0)
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback template assertion failed: no template for area='{template.AreaName}' "
                + $"interfaces='{template.Interface}' networks='{template.NetworkCidr}'.");
        }

        if (matches.Count > 1)
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback template assertion failed: {matches.Count} templates match "
                + $"area='{template.AreaName}' interfaces='{template.Interface}' networks='{template.NetworkCidr}'.");
        }

        ITikReSentence live = matches[0];
        string? liveId = Field(live, ".id");
        if (string.IsNullOrWhiteSpace(liveId))
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback template '{template.NetworkCidr}' matching row has no .id after ensure.");
        }

        if (!string.Equals(Field(live, "area"), template.AreaName, StringComparison.OrdinalIgnoreCase))
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback template .id={liveId} area='{Field(live, "area")}' expected '{template.AreaName}'.");
        }

        if (!string.Equals(
                Field(live, "interfaces") ?? string.Empty,
                template.Interface ?? string.Empty,
                StringComparison.Ordinal))
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback template .id={liveId} interfaces='{Field(live, "interfaces")}' "
                + $"expected '{template.Interface}'.");
        }

        if (!string.Equals(
                NormalizeCidr(Field(live, "networks")),
                NormalizeCidr(template.NetworkCidr),
                StringComparison.Ordinal))
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback template .id={liveId} networks='{Field(live, "networks")}' "
                + $"expected '{template.NetworkCidr}'.");
        }

        if (!IsFlagPresent(Field(live, "passive")) && !IsTruthy(Field(live, "passive")))
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback template .id={liveId} networks='{template.NetworkCidr}' is not passive after ensure.");
        }

        string? type = Field(live, "type");
        if (!string.IsNullOrWhiteSpace(type)
            && (type.Equals("ptp", StringComparison.OrdinalIgnoreCase)
                || type.Equals("point-to-point", StringComparison.OrdinalIgnoreCase)))
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback template .id={liveId} networks='{template.NetworkCidr}' still has explicit PTP type '{type}' after ensure.");
        }

        if (IsTruthy(Field(live, "disabled")))
        {
            throw new OspfBackboneMutationGuardException(
                $"Loopback template .id={liveId} networks='{template.NetworkCidr}' is disabled after ensure.");
        }
    }

    private static bool MatchesLoopbackTemplateRow(ITikReSentence row, OspfPlanLoopbackTemplate template) =>
        string.Equals(Field(row, "area"), template.AreaName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            NormalizeCidr(Field(row, "networks")),
            NormalizeCidr(template.NetworkCidr),
            StringComparison.Ordinal)
        && string.Equals(
            Field(row, "interfaces") ?? string.Empty,
            template.Interface ?? string.Empty,
            StringComparison.Ordinal);

    private async Task<ManagementRouteObservation> ObserveManagementRouteAsync(
        ITikConnection connection,
        string managementHost,
        CancellationToken cancellationToken)
    {
        if (!System.Net.IPAddress.TryParse(managementHost, out System.Net.IPAddress? hostIp)
            || hostIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            logger.LogWarning(
                "Management host '{Host}' is not a parseable IPv4 address; skipping route observation",
                managementHost);
            return ManagementRouteObservation.None;
        }

        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/ip/route/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        ManagementRouteObservation? best = null;
        int bestPrefix = -1;
        foreach (ITikReSentence row in rows)
        {
            string? dst = Field(row, "dst-address");
            if (string.IsNullOrWhiteSpace(dst) || !TryParseCidr(dst, out uint network, out int prefix))
            {
                continue;
            }

            if (!Ipv4HostInPrefix(hostIp, network, prefix))
            {
                continue;
            }

            // RouterOS route print: active=true/yes means selected. Empty/absent is not active.
            string? activeRaw = Field(row, "active");
            bool active = string.Equals(activeRaw, "true", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(activeRaw, "yes", StringComparison.OrdinalIgnoreCase);

            // Prefer active routes; among equals, prefer longest prefix.
            if (best is { } current)
            {
                if (current.Active && !active)
                {
                    continue;
                }

                if (current.Active == active && prefix < bestPrefix)
                {
                    continue;
                }
            }

            string? gateway = Field(row, "gateway");
            string? immediate = Field(row, "immediate-gw") ?? gateway;
            best = new ManagementRouteObservation(
                Observed: true,
                Active: active,
                DstAddress: dst,
                Gateway: gateway,
                ImmediateGw: immediate,
                OutgoingInterface: ExtractOutgoingInterface(immediate) ?? ExtractOutgoingInterface(gateway));
            bestPrefix = prefix;
        }

        return best ?? ManagementRouteObservation.None;
    }

    private static bool RouteDependsOnBackbone(
        ManagementRouteObservation route,
        string? backboneInterface,
        string? neighbourAddress)
    {
        if (!route.Observed)
        {
            return false;
        }

        string haystack = $"{route.Gateway}|{route.ImmediateGw}|{route.OutgoingInterface}";
        if (!string.IsNullOrWhiteSpace(backboneInterface)
            && haystack.Contains(backboneInterface, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(neighbourAddress)
            && haystack.Contains(neighbourAddress, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string? ExtractOutgoingInterface(string? gateway)
    {
        if (string.IsNullOrWhiteSpace(gateway))
        {
            return null;
        }

        // RouterOS forms: "10.16.248.77%eth-01_BH01.Tommy-Shylock" or bare interface name.
        int pct = gateway.IndexOf('%');
        if (pct >= 0 && pct < gateway.Length - 1)
        {
            return gateway[(pct + 1)..];
        }

        if (gateway.Contains('.', StringComparison.Ordinal) && System.Net.IPAddress.TryParse(gateway, out _))
        {
            return null;
        }

        return gateway;
    }

    private static bool TryParseCidr(string cidr, out uint network, out int prefix)
    {
        network = 0;
        prefix = -1;
        string[] parts = cidr.Split('/');
        if (parts.Length != 2
            || !System.Net.IPAddress.TryParse(parts[0], out System.Net.IPAddress? net)
            || net.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out prefix)
            || prefix is < 0 or > 32)
        {
            return false;
        }

        byte[] b = net.GetAddressBytes();
        network = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return true;
    }

    private static bool Ipv4HostInPrefix(System.Net.IPAddress host, uint network, int prefix)
    {
        byte[] b = host.GetAddressBytes();
        if (b.Length != 4)
        {
            return false;
        }

        uint h = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (h & mask) == (network & mask);
    }

    private readonly record struct ManagementRouteObservation(
        bool Observed,
        bool Active,
        string? DstAddress,
        string? Gateway,
        string? ImmediateGw,
        string? OutgoingInterface)
    {
        public static ManagementRouteObservation None { get; } = new(false, false, null, null, null, null);
    }

    /// <summary>
    /// Enables a RouterOS flag property (<c>()</c> datatype) via empty value. Never use yes/no for flags
    /// such as <c>passive</c> on OSPF interface-templates — <c>passive=no</c> still enables the flag.
    /// </summary>
    private static void SetFlagEnabled(ITikCommand command, string flagName) =>
        command.AddParameter(flagName, string.Empty);

    private static bool IsFlagPresent(string? value) =>
        value is not null; // presence including empty string means the flag is set/true on ROS print

    private static async Task RemoveByIdAsync(
        ITikConnection connection,
        string removeCommand,
        string? id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        ITikCommand remove = connection.CreateCommand(removeCommand);
        remove.AddParameter(".id", id);
        await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> FindInstanceIdAsync(
        ITikConnection connection,
        string instanceName,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/instance/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Field(
            rows.FirstOrDefault(r =>
                string.Equals(Field(r, "name"), instanceName, StringComparison.OrdinalIgnoreCase)),
            ".id");
    }

    private async Task<RouterOsVersion> ReadRouterOsVersionOrThrowAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows;
        try
        {
            rows = await connection
                .CreateCommand("/system/resource/print")
                .ExecuteListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Unable to read RouterOS version from /system/resource/print; refusing routing-filter operations.",
                ex);
        }

        string? versionText = Field(rows.FirstOrDefault(), "version");
        if (!RouterOsVersion.TryParse(versionText, out RouterOsVersion version))
        {
            throw new InvalidOperationException(
                $"Unable to parse RouterOS version '{versionText}' from /system/resource/print; refusing routing-filter operations.");
        }

        return version;
    }

    private static async Task<List<OspfRoutingFilterRow>> ReadV6RoutingFilterInventoryAsync(
        ITikConnection connection,
        CancellationToken cancellationToken) =>
        await PrintMappedAsync(
                connection,
                OspfRoutingFilterMenus.V6FilterPrint,
                MapRoutingFilterRow,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<List<OspfRoutingFilterRow>> ReadV7RoutingFilterInventoryAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        // Required menu — any failure fails closed (including "no such command").
        List<OspfRoutingFilterRow> rules = await PrintMappedAsync(
                connection,
                OspfRoutingFilterMenus.V7FilterRulePrint,
                MapRoutingFilterRow,
                cancellationToken)
            .ConfigureAwait(false);

        List<OspfRoutingFilterRow> selectRules;
        try
        {
            selectRules = await PrintMappedAsync(
                    connection,
                    OspfRoutingFilterMenus.V7FilterSelectRulePrint,
                    MapRoutingFilterRow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAbsentMenuException(ex))
        {
            logger.LogInformation(
                "RouterOS v7 menu {Path} absent or unsupported; treating select-rule inventory as empty",
                OspfRoutingFilterMenus.V7FilterSelectRulePrint);
            selectRules = [];
        }

        List<OspfRoutingFilterRow> combined = new(rules.Count + selectRules.Count);
        combined.AddRange(rules);
        combined.AddRange(selectRules);
        return combined;
    }

    private async Task RemoveAllRowsByIdAsync(
        ITikConnection connection,
        string printCommand,
        string removeCommand,
        bool requiredMenu,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows;
        try
        {
            rows = await connection
                .CreateCommand(printCommand)
                .ExecuteListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!requiredMenu && IsAbsentMenuException(ex))
        {
            logger.LogInformation(
                "Optional routing-filter menu {Path} absent or unsupported; skipping removal",
                printCommand);
            return;
        }
        catch (Exception ex) when (requiredMenu)
        {
            throw new InvalidOperationException(
                $"Required routing-filter inventory '{printCommand}' failed; refusing cleanup.",
                ex);
        }

        foreach (ITikReSentence row in rows)
        {
            string? id = Field(row, ".id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            ITikCommand remove = connection.CreateCommand(removeCommand);
            remove.AddParameter(".id", id);
            await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static OspfRoutingFilterRow MapRoutingFilterRow(ITikReSentence row) => new()
    {
        Id = Field(row, ".id"),
        Name = Field(row, "chain")
               ?? Field(row, "name")
               ?? Field(row, "rule")
               ?? Field(row, ".id"),
    };

    private static async Task<List<T>> PrintMappedAsync<T>(
        ITikConnection connection,
        string printCommand,
        Func<ITikReSentence, T> map,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand(printCommand)
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(map).ToList();
    }

    private static OspfInstanceRow MapInstance(ITikReSentence row)
    {
        var redistribute = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in new[]
                 {
                     "redistribute", "redistribute-static", "redistribute-connected", "redistribute-other",
                 })
        {
            string? value = Field(row, key);
            if (value is not null)
            {
                redistribute[key] = value;
            }
        }

        return new OspfInstanceRow
        {
            Id = Field(row, ".id"),
            Name = Field(row, "name"),
            RouterId = Field(row, "router-id"),
            Version = Field(row, "version"),
            IsDefault = IsTruthy(Field(row, "default")),
            RedistributeProperties = redistribute,
            InFilter = NullIfWhiteSpace(Field(row, "in-filter"))
                       ?? NullIfWhiteSpace(Field(row, "in-filter-chain")),
            // ROS7 outbound filters are out-filter-chain / out-filter-select (not ambiguous "out-filter").
            OutFilter = NullIfWhiteSpace(Field(row, "out-filter-chain"))
                        ?? NullIfWhiteSpace(Field(row, "out-filter-select"))
                        ?? NullIfWhiteSpace(Field(row, "out-filter")),
        };
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OspfAreaRow MapAreaV6(ITikReSentence row) => new()
    {
        Id = Field(row, ".id"),
        Name = Field(row, "name"),
        AreaId = Field(row, "area-id"),
        Instance = Field(row, "instance"),
        Type = Field(row, "type"),
        DefaultCost = Field(row, "default-cost"),
        InjectSummaryLsas = Field(row, "inject-summary-lsas"),
    };

    private static OspfAreaRow MapAreaV7(ITikReSentence row) => new()
    {
        Id = Field(row, ".id"),
        Name = Field(row, "name"),
        AreaId = Field(row, "area-id"),
        Instance = Field(row, "instance"),
        Type = Field(row, "type"),
        DefaultCost = Field(row, "default-cost"),
        NoSummaries = IsTruthy(Field(row, "no-summaries")),
    };

    private static OspfAreaRangeRow MapRange(ITikReSentence row) => new()
    {
        Id = Field(row, ".id"),
        Area = Field(row, "area"),
        Range = ReadAreaRangePrefix(row),
        Comment = Field(row, "comment"),
        Advertise = Field(row, "advertise") is null ? null : IsTruthy(Field(row, "advertise")),
    };

    /// <summary>ROS7 prints <c>prefix</c>; legacy/v6 export used <c>range</c>.</summary>
    private static string? ReadAreaRangePrefix(ITikReSentence row) =>
        Field(row, "prefix") ?? Field(row, "range");

    /// <summary>
    /// Inventory mapping accepts either <c>networks</c> or legacy <c>network</c>; assert must too.
    /// </summary>
    private static string? ReadNetworksField(ITikReSentence row) =>
        Field(row, "networks") ?? Field(row, "network");

    /// <summary>
    /// Maps a planned backhaul onto the live ROS7 backbone area name when the plan still
    /// carries the logical capture name (e.g. <c>backbone</c> → <c>backbone-v2</c>).
    /// Does not alter interface/network/cost — only the area identity used for lookup.
    /// </summary>
    private static OspfPlanBackhaulTemplate RemapBackhaulAreaToLiveTarget(
        OspfMigrationPlan plan,
        OspfPlanBackhaulTemplate backhaul)
    {
        string target = string.IsNullOrWhiteSpace(plan.TargetBackboneAreaName)
            ? OspfFleetNaming.Ros7BackboneAreaName
            : plan.TargetBackboneAreaName;
        if (string.Equals(backhaul.AreaName, target, StringComparison.OrdinalIgnoreCase))
        {
            return backhaul;
        }

        bool plannedAsBackbone = plan.Areas.Any(a =>
            a.IsBackbone
            && string.Equals(a.Name, backhaul.AreaName, StringComparison.OrdinalIgnoreCase));
        if (!plannedAsBackbone)
        {
            return backhaul;
        }

        return new OspfPlanBackhaulTemplate
        {
            AreaName = target,
            Interface = backhaul.Interface,
            NetworkCidr = backhaul.NetworkCidr,
            Cost = backhaul.Cost,
            Type = backhaul.Type,
            Passive = backhaul.Passive,
            SourceCost = backhaul.SourceCost,
        };
    }

    private static string? Field(ITikReSentence? row, string name) =>
        row?.GetResponseFieldOrDefault(name, null);

    /// <summary>
    /// RouterOS yes/no and flag-style truthiness. Empty string means flag present (true) for properties
    /// such as <c>passive</c> on interface-templates.
    /// </summary>
    private static bool IsTruthy(string? value) =>
        value is not null
        && (value.Length == 0
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value == "1");

    private static string? GetDict(Dictionary<string, string>? row, string key) =>
        row is not null && row.TryGetValue(key, out string? value) ? value : null;

    private static string NormalizeCidr(string? value)
    {
        if (OspfIpPrefix.TryParse(value, out OspfIpPrefix prefix))
        {
            return prefix.ToCidrString();
        }

        return value?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Positive identification of an absent/unsupported RouterOS menu or command path only.
    /// Permission, timeout, connection, and generic API failures must not match.
    /// </summary>
    private static bool IsAbsentMenuException(Exception ex)
    {
        string message = ex.Message ?? string.Empty;
        return message.Contains("no such command", StringComparison.OrdinalIgnoreCase)
               || message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
               || message.Contains("bad command name", StringComparison.OrdinalIgnoreCase)
               || message.Contains("no such command prefix", StringComparison.OrdinalIgnoreCase);
    }
}
