// -----------------------------------------------------------------------------
// File: TommyOspfSummaryLsaReadOnlyHarness.cs
//
// Description:
// Strictly read-only investigation of OSPF summary LSA origination /
// area-range behaviour on Tommy. No mutations, backups, resets, or reconciler
// apply.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Strictly read-only investigation of OSPF summary LSA origination / area-range behaviour on Tommy.
/// No mutations, backups, resets, or reconciler apply.
/// </summary>
public sealed class TommyOspfSummaryLsaReadOnlyHarness
{
    /// <summary>
    /// Name of the environment variable that overrides the directory where read-only investigation evidence is written; defaults to a timestamped folder under <c>Reports/OperationalEvidence</c>.
    /// </summary>
    public const string EvidenceRootEnv = "TOMMY_OSPF_SUMMARY_LSA_EVIDENCE_DIR";

    /// <summary>
    /// Serializer options used for evidence files so they are human-readable.
    /// </summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Area ranges (prefix and comment) expected to be configured on Tommy, whose summary LSA origination and component routes are investigated.
    /// </summary>
    private static readonly (string Prefix, string Comment)[] ExpectedRanges =
    [
        ("10.16.11.0/24", "MGMT"),
        ("100.99.76.16/28", "HEROTEL_ACTIVE_POOL"),
        ("172.26.197.4/30", "HEROTEL_INACTIVE_POOL"),
    ];

    /// <summary>
    /// Scenario: Read-only live investigation of why the area ranges on BR01.POP011.016.Tommy (198.18.240.11,
    /// RouterOS 7.25.1, mipsbe) may not originate summary LSAs. It runs only when the live gate is armed, issues
    /// print commands only, and writes numbered JSON evidence. For each expected range it analyses the component
    /// routes (connected, or any route whose destination falls in the range, including blackhole routes) and the
    /// self-originated inter-area summary LSAs.
    /// Expected: FirmwareUpgrade.Enabled remains false and the router identity, version and architecture match. The
    /// router is still reachable with its OSPF neighbour Full after the investigation, and
    /// 99-investigation-report.json is written with blackhole and inter-area findings.
    /// Invariant: The investigation makes no configuration change on the router.
    /// </summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Investigate_Tommy_Summary_Lsa_ReadOnly()
    {
        LiveMikrotikTestGate.RequireArmed(LiveMikrotikTestGate.AllowLiveMikrotikEnv, "Live");

        DateTimeOffset start = DateTimeOffset.Now;
        string repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        string evidenceDir = Environment.GetEnvironmentVariable(EvidenceRootEnv)
            ?? Path.Combine(
                repoRoot,
                "Reports",
                "OperationalEvidence",
                $"Tommy-Ospf-Summary-Lsa-ReadOnly-{start:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(evidenceDir);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        string workerDir = Path.Combine(repoRoot, "src", "MikrotikWorker");
        builder.Configuration
            .AddJsonFile(Path.Combine(workerDir, "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(workerDir, "appsettings.Development.json"), optional: true)
            .AddJsonFile(Path.Combine(workerDir, "appsettings.Development.local.json"), optional: true);

        ConfigureServices(builder.Services, builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        using IHost host = builder.Build();
        IOptions<FirmwareUpgradeOptions> upgrade = host.Services.GetRequiredService<IOptions<FirmwareUpgradeOptions>>();
        IOptions<MikroTikOptions> mikrotik = host.Services.GetRequiredService<IOptions<MikroTikOptions>>();
        Assert.False(upgrade.Value.Enabled, "FirmwareUpgrade.Enabled must remain false.");

        const string expectedIdentity = "BR01.POP011.016.Tommy";
        IPAddress targetIp = IPAddress.Parse("198.18.240.11");

        IMikrotikConnectionFactory factory = host.Services.GetRequiredService<IMikrotikConnectionFactory>();
        IMikrotikDeviceInfoReader deviceInfo = host.Services.GetRequiredService<IMikrotikDeviceInfoReader>();
        IOspfMikrotikOperations ops = host.Services.GetRequiredService<IOspfMikrotikOperations>();

        MikrotikConnectionOptions connOpts = new()
        {
            Host = targetIp.ToString(),
            Username = mikrotik.Value.Username,
            Password = mikrotik.Value.Password,
            Port = mikrotik.Value.Port,
        };

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        using ITikConnection connection = await factory.CreateAsync(connOpts, cts.Token);
        MikrotikDeviceInfo info = await deviceInfo.ReadDeviceInfoAsync(connection, targetIp.ToString(), cts.Token);

        Assert.Equal(expectedIdentity, info.Identity);
        Assert.StartsWith("7.25.1", info.Version, StringComparison.Ordinal);
        Assert.Equal("mipsbe", info.ArchitectureName, ignoreCase: true);

        await WriteJson(evidenceDir, "00-session.json", new
        {
            start,
            target = targetIp.ToString(),
            expectedIdentity,
            mode = "read-only",
            info.Identity,
            info.Version,
            info.ArchitectureName,
            info.BoardName,
            info.RouterboardModel,
            info.RouterboardCurrentFirmware,
            info.RouterboardUpgradeFirmware,
            Reachable = await PingOkAsync(targetIp.ToString()),
        });

        IReadOnlyList<Dictionary<string, string>> neighboursBefore =
            await ops.ReadOspfNeighboursAsync(connection, cts.Token);
        await WriteJson(evidenceDir, "01-neighbours-before.json", neighboursBefore);
        AssertNeighbourFull(neighboursBefore);

        OspfV7Inventory inventory = await ops.ReadV7InventoryAsync(connection, cts.Token);
        IReadOnlyList<OspfIpAddressRow> addresses =
            await ops.ReadIpAddressInventoryAsync(connection, cts.Token);
        int filterCount = await ops.CountRoutingFiltersAsync(connection, cts.Token);

        List<Dictionary<string, string>> rawInstances =
            await PrintAsync(connection, "/routing/ospf/instance/print", cts.Token);
        List<Dictionary<string, string>> rawAreas =
            await PrintAsync(connection, "/routing/ospf/area/print", cts.Token);
        List<Dictionary<string, string>> rawRanges =
            await PrintAsync(connection, "/routing/ospf/area/range/print", cts.Token);
        List<Dictionary<string, string>> rawTemplates =
            await PrintAsync(connection, "/routing/ospf/interface-template/print", cts.Token);
        List<Dictionary<string, string>> rawInterfaces =
            await PrintAsync(connection, "/routing/ospf/interface/print", cts.Token);
        List<Dictionary<string, string>> lsas =
            await PrintAsync(connection, "/routing/ospf/lsa/print", cts.Token);
        List<Dictionary<string, string>> routes =
            await PrintAsync(connection, "/ip/route/print", cts.Token);
        List<Dictionary<string, string>> addressesRaw =
            await PrintAsync(connection, "/ip/address/print", cts.Token);

        await WriteJson(evidenceDir, "02-instances-raw.json", rawInstances);
        await WriteJson(evidenceDir, "03-areas-raw.json", rawAreas);
        await WriteJson(evidenceDir, "04-area-ranges-raw.json", rawRanges);
        await WriteJson(evidenceDir, "05-interface-templates-raw.json", rawTemplates);
        await WriteJson(evidenceDir, "06-ospf-interfaces-raw.json", rawInterfaces);
        await WriteJson(evidenceDir, "07-lsa-database-raw.json", lsas);
        await WriteJson(evidenceDir, "08-ip-route-raw.json", routes);
        await WriteJson(evidenceDir, "09-ip-address-raw.json", addressesRaw);
        await WriteJson(evidenceDir, "10-inventory-mapped.json", new
        {
            filterCount,
            inventory.Instances,
            inventory.Areas,
            inventory.AreaRanges,
            inventory.Templates,
            inventory.Filters,
            inventory.OspfV3Objects,
            addresses,
        });

        // Component-route analysis for each expected range (connected + any route whose dst falls in range).
        var componentAnalysis = ExpectedRanges.Select(er =>
        {
            List<Dictionary<string, string>> matchingRoutes = routes
                .Where(r => RoutePrefixCoveredByRange(Get(r, "dst-address"), er.Prefix))
                .ToList();
            List<Dictionary<string, string>> matchingAddresses = addressesRaw
                .Where(a => AddressInRange(Get(a, "address"), er.Prefix))
                .ToList();
            List<Dictionary<string, string>> matchingLsas = lsas
                .Where(l => LsaMatchesPrefix(l, er.Prefix))
                .ToList();
            Dictionary<string, string>? configuredRange = rawRanges.FirstOrDefault(r =>
                PrefixEqual(Get(r, "prefix") ?? Get(r, "range"), er.Prefix));

            return new
            {
                er.Prefix,
                er.Comment,
                RangeConfigured = configuredRange is not null,
                RangeRow = configuredRange,
                MatchingIpAddresses = matchingAddresses,
                MatchingRoutes = matchingRoutes,
                MatchingLsas = matchingLsas,
                BlackholeForRange = matchingRoutes.Any(r =>
                    string.Equals(Get(r, "dst-address"), er.Prefix, StringComparison.Ordinal)
                    && (Get(r, "type")?.Contains("blackhole", StringComparison.OrdinalIgnoreCase) == true
                        || Get(r, "blackhole") is not null
                        || string.Equals(Get(r, "gateway"), "blackhole", StringComparison.OrdinalIgnoreCase))),
                SelfOriginatedSummaryCandidates = matchingLsas
                    .Where(l =>
                        IsTruthy(Get(l, "self"))
                        || string.Equals(Get(l, "originator"), "10.16.0.11", StringComparison.Ordinal)
                        || string.Equals(Get(l, "originator"), "10.16.0.11", StringComparison.OrdinalIgnoreCase))
                    .ToList(),
            };
        }).ToList();

        await WriteJson(evidenceDir, "11-component-and-summary-analysis.json", componentAnalysis);

        // LSA type histogram and self-originated inter-area summaries.
        var lsaSummary = new
        {
            Total = lsas.Count,
            ByType = lsas
                .GroupBy(l => Get(l, "type") ?? "<missing>")
                .OrderBy(g => g.Key)
                .Select(g => new { Type = g.Key, Count = g.Count() }),
            SelfOriginated = lsas
                .Where(l =>
                    IsTruthy(Get(l, "self"))
                    || string.Equals(Get(l, "originator"), "10.16.0.11", StringComparison.Ordinal))
                .Select(LsaBrief)
                .ToList(),
            InterAreaPrefix = lsas
                .Where(l =>
                    string.Equals(Get(l, "type"), "inter-area-prefix", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Get(l, "type"), "summary", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Get(l, "type"), "summary-network", StringComparison.OrdinalIgnoreCase)
                    || (Get(l, "type")?.Contains("inter-area", StringComparison.OrdinalIgnoreCase) == true))
                .Select(LsaBrief)
                .ToList(),
            RelatedToExpectedPrefixes = lsas
                .Where(l => ExpectedRanges.Any(er => LsaMatchesPrefix(l, er.Prefix)))
                .Select(LsaBrief)
                .ToList(),
        };
        await WriteJson(evidenceDir, "12-lsa-summary.json", lsaSummary);

        // Routes that look OSPF-related or cover expected prefixes / blackholes.
        var routeFocus = new
        {
            OspfTagged = routes
                .Where(r =>
                    Get(r, "ospf") is not null
                    || (Get(r, "connect") is null
                        && Get(r, "gateway") is not null
                        && (Get(r, "dst-address")?.StartsWith("10.16.", StringComparison.Ordinal) == true
                            || Get(r, "dst-address")?.StartsWith("100.", StringComparison.Ordinal) == true
                            || Get(r, "dst-address")?.StartsWith("172.26.", StringComparison.Ordinal) == true)))
                .Take(200)
                .ToList(),
            ExactExpectedPrefixRoutes = routes
                .Where(r => ExpectedRanges.Any(er =>
                    string.Equals(Get(r, "dst-address"), er.Prefix, StringComparison.Ordinal)))
                .ToList(),
            BlackholeRoutes = routes
                .Where(r =>
                    Get(r, "type")?.Contains("blackhole", StringComparison.OrdinalIgnoreCase) == true
                    || Get(r, "blackhole") is not null
                    || string.Equals(Get(r, "gateway"), "blackhole", StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };
        await WriteJson(evidenceDir, "13-route-focus.json", routeFocus);

        IReadOnlyList<Dictionary<string, string>> neighboursAfter =
            await ops.ReadOspfNeighboursAsync(connection, cts.Token);
        await WriteJson(evidenceDir, "14-neighbours-after.json", neighboursAfter);
        AssertNeighbourFull(neighboursAfter);

        DateTimeOffset end = DateTimeOffset.Now;
        bool stillReachable = await PingOkAsync(targetIp.ToString());

        var report = new
        {
            Investigation = new
            {
                Start = start,
                End = end,
                EvidenceDir = evidenceDir,
                ReadOnly = true,
                MutatingCommandsIssued = false,
                BackupCreated = false,
                FirmwareOrRouterboardTouched = false,
                StillReachable = stillReachable,
                NeighbourFullBefore = NeighbourIsFull(neighboursBefore),
                NeighbourFullAfter = NeighbourIsFull(neighboursAfter),
            },
            Target = new
            {
                info.Identity,
                Address = targetIp.ToString(),
                info.Version,
                info.ArchitectureName,
            },
            Configuration = new
            {
                Instances = inventory.Instances,
                Areas = inventory.Areas,
                AreaRanges = inventory.AreaRanges,
                Templates = inventory.Templates,
                FilterCount = filterCount,
                NeighboursBefore = neighboursBefore,
                NeighboursAfter = neighboursAfter,
            },
            ComponentAnalysis = componentAnalysis,
            LsaSummary = lsaSummary,
            RouteFocus = routeFocus,
            DocumentationNotes = new
            {
                Source = "https://help.mikrotik.com/docs/spaces/ROS/pages/9863229/OSPF",
                AreaRangeAdvertiseRequiresCoveredRoutes =
                    "If a range is configured with the 'advertise' parameter, a single summary LSA is advertised "
                    + "for each range if there are any routes under the range in the specific area.",
                ActiveRangeInstallsBlackhole =
                    "For an active range (i.e. one that has at least one OSPF route from the specified area falling under it), "
                    + "a route with the type 'blackhole' is created and installed in the routing table.",
                StubNoSummaries =
                    "Totally stubby area = stub + no-summaries; blocks external and summarized inter-area routes into the area. "
                    + "Stub without no-summaries still receives Type-3 summaries into the stub; ABR still originates Type-3 into backbone from the stub's networks.",
            },
        };

        await WriteJson(evidenceDir, "99-investigation-report.json", report);
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDir, "verdict.txt"),
            "READ-ONLY SUMMARY LSA INVESTIGATION COMPLETE — no mutations performed.",
            cts.Token);

        Assert.True(stillReachable);
        Assert.True(NeighbourIsFull(neighboursAfter));
        Assert.True(File.Exists(Path.Combine(evidenceDir, "99-investigation-report.json")));
    }

    /// <summary>
    /// Asserts that a neighbour is in the Full state with router-id 198.18.255.253 at address 10.16.248.77.
    /// </summary>
    private static void AssertNeighbourFull(IReadOnlyList<Dictionary<string, string>> neighbours)
    {
        Assert.Contains(
            neighbours,
            n => string.Equals(Get(n, "state"), "Full", StringComparison.OrdinalIgnoreCase)
                 && string.Equals(Get(n, "router-id"), "198.18.255.253", StringComparison.Ordinal)
                 && string.Equals(Get(n, "address"), "10.16.248.77", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns true when any neighbour is in the Full state with router-id 198.18.255.253.
    /// </summary>
    private static bool NeighbourIsFull(IReadOnlyList<Dictionary<string, string>> neighbours) =>
        neighbours.Any(n =>
            string.Equals(Get(n, "state"), "Full", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Get(n, "router-id"), "198.18.255.253", StringComparison.Ordinal));

    /// <summary>
    /// Projects a raw LSA row into a brief object with type, id, originator, area, instance, sequence, checksum,
    /// age, options, netmask, metric and the raw row.
    /// </summary>
    private static object LsaBrief(Dictionary<string, string> l) => new
    {
        Type = Get(l, "type"),
        Id = Get(l, "id") ?? Get(l, "link-state-id"),
        Originator = Get(l, "originator") ?? Get(l, "advertising-router"),
        Area = Get(l, "area"),
        Instance = Get(l, "instance"),
        Sequence = Get(l, "sequence") ?? Get(l, "sequence-number"),
        Checksum = Get(l, "checksum"),
        Age = Get(l, "age"),
        Options = Get(l, "options"),
        Self = Get(l, "self"),
        Body = Get(l, "body"),
        Netmask = Get(l, "netmask") ?? ExtractBodyField(Get(l, "body"), "netmask"),
        Metric = Get(l, "metric") ?? ExtractBodyField(Get(l, "body"), "metric"),
        Raw = l,
    };

    /// <summary>
    /// Extracts the value of <paramref name="key"/> from an LSA <c>body</c> of whitespace/newline-separated <c>key=value</c> pairs.
    /// <param name="body">Raw LSA body text; may be <see langword="null"/>.</param>
    /// <param name="key">Field name to look for, compared case-insensitively.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when the body is empty or the key is absent.</returns>
    /// </summary>
    private static string? ExtractBodyField(string? body, string key)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // body often looks like: "netmask=255.255.255.0\nmetric=1"
        foreach (string line in body.Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            if (line[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(eq + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true when the LSA's id, netmask or body corresponds to the given CIDR prefix.
    /// </summary>
    private static bool LsaMatchesPrefix(Dictionary<string, string> lsa, string cidr)
    {
        if (!OspfIpPrefix.TryParse(cidr, out OspfIpPrefix expected))
        {
            return false;
        }

        string expectedNetwork = expected.ToCidrString().Split('/')[0];
        string? id = Get(lsa, "id") ?? Get(lsa, "link-state-id");
        if (id is not null
            && OspfIpPrefix.TryParseHost(id, out uint idHost, out _)
            && expected.ContainsHost(idHost))
        {
            string? mask = Get(lsa, "netmask") ?? ExtractBodyField(Get(lsa, "body"), "netmask");
            if (mask is not null && IPAddress.TryParse(mask, out IPAddress? maskIp))
            {
                int prefixLen = MaskToPrefixLength(maskIp);
                if (prefixLen == expected.PrefixLength
                    && string.Equals(id, expectedNetwork, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (string.Equals(id, expectedNetwork, StringComparison.Ordinal))
            {
                return true;
            }
        }

        string? body = Get(lsa, "body");
        return body is not null
               && body.Contains(expectedNetwork, StringComparison.Ordinal)
               && (body.Contains(cidr, StringComparison.Ordinal)
                   || body.Contains(PrefixToDottedMask(expected.PrefixLength), StringComparison.Ordinal));
    }

    /// <summary>
    /// Determines whether a route destination equals, lies within, or has a host address inside the given range.
    /// <param name="dst">Route destination (CIDR or host address).</param>
    /// <param name="rangeCidr">Area range prefix in CIDR notation.</param>
    /// <returns><see langword="true"/> when the destination is covered by the range.</returns>
    /// </summary>
    private static bool RoutePrefixCoveredByRange(string? dst, string rangeCidr)
    {
        if (string.IsNullOrWhiteSpace(dst) || !OspfIpPrefix.TryParse(rangeCidr, out OspfIpPrefix range))
        {
            return false;
        }

        if (string.Equals(dst, rangeCidr, StringComparison.Ordinal)
            || (OspfIpPrefix.TryParse(dst, out OspfIpPrefix exact) && exact.Equals(range)))
        {
            return true;
        }

        if (OspfIpPrefix.TryParse(dst, out OspfIpPrefix route))
        {
            return range.Contains(route);
        }

        return OspfIpPrefix.TryParseHost(dst, out uint host, out _) && range.ContainsHost(host);
    }

    /// <summary>
    /// Determines whether the host portion of an interface address (CIDR) lies inside the given range.
    /// <param name="addressCidr">Interface address, for example <c>10.16.11.30/27</c>.</param>
    /// <param name="rangeCidr">Area range prefix in CIDR notation.</param>
    /// <returns><see langword="true"/> when the address is inside the range.</returns>
    /// </summary>
    private static bool AddressInRange(string? addressCidr, string rangeCidr)
    {
        if (string.IsNullOrWhiteSpace(addressCidr)
            || !OspfIpPrefix.TryParse(rangeCidr, out OspfIpPrefix range))
        {
            return false;
        }

        return OspfIpPrefix.TryParseHost(addressCidr, out uint host, out _) && range.ContainsHost(host);
    }

    /// <summary>
    /// Compares two prefixes by value so that differently formatted representations of the same prefix are treated as equal.
    /// <param name="left">First prefix; blank values never match.</param>
    /// <param name="right">Second prefix.</param>
    /// <returns><see langword="true"/> when both parse to the same prefix or are identical strings.</returns>
    /// </summary>
    private static bool PrefixEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return OspfIpPrefix.TryParse(left, out OspfIpPrefix a)
               && OspfIpPrefix.TryParse(right, out OspfIpPrefix b)
               && a.Equals(b);
    }

    /// <summary>
    /// Converts a dotted-decimal subnet mask into its prefix length by counting leading one bits.
    /// <param name="mask">Subnet mask address.</param>
    /// <returns>The number of contiguous leading one bits.</returns>
    /// </summary>
    private static int MaskToPrefixLength(IPAddress mask)
    {
        byte[] bytes = mask.GetAddressBytes();
        int len = 0;
        foreach (byte b in bytes)
        {
            for (int i = 7; i >= 0; i--)
            {
                if (((b >> i) & 1) == 1)
                {
                    len++;
                }
                else
                {
                    return len;
                }
            }
        }

        return len;
    }

    /// <summary>
    /// Converts a prefix length into a dotted-decimal subnet mask string.
    /// <param name="prefixLength">Prefix length from 0 to 32.</param>
    /// <returns>The mask, for example <c>255.255.255.0</c> for 24.</returns>
    /// </summary>
    private static string PrefixToDottedMask(int prefixLength)
    {
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(mask >> 24) & 0xff}.{(mask >> 16) & 0xff}.{(mask >> 8) & 0xff}.{mask & 0xff}");
    }

    /// <summary>
    /// Interprets a RouterOS flag value as set when it is <c>true</c>, <c>yes</c>, <c>1</c> or empty (a bare flag).
    /// <param name="value">Raw property value; may be <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the flag is considered set.</returns>
    /// </summary>
    private static bool IsTruthy(string? value) =>
        value is not null
        && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value == ""
            || value == "1");

    /// <summary>
    /// Reads a property from a RouterOS row without throwing when the row or key is missing.
    /// <param name="d">Row to read; may be <see langword="null"/>.</param>
    /// <param name="key">Property name.</param>
    /// <returns>The value, or <see langword="null"/> when unavailable.</returns>
    /// </summary>
    private static string? Get(Dictionary<string, string>? d, string key) =>
        d is not null && d.TryGetValue(key, out string? v) ? v : null;

    /// <summary>
    /// Sends a single ICMP echo with a two-second timeout and reports whether it succeeded; any exception counts as unreachable.
    /// <param name="host">Host name or IP address to ping.</param>
    /// <returns><see langword="true"/> when the host replied successfully.</returns>
    /// </summary>
    private static async Task<bool> PingOkAsync(string host)
    {
        try
        {
            using Ping ping = new();
            PingReply reply = await ping.SendPingAsync(host, 2000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Registers the minimal services needed for read-only RouterOS access (options, device logging, connection factory, API client and OSPF operations); no backup, RouterBOARD or migration services are added.
    /// <param name="services">Service collection to populate.</param>
    /// <param name="configuration">Configuration providing the option sections.</param>
    /// </summary>
    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MikroTikOptions>()
            .Bind(configuration.GetSection(MikroTikOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MikroTikOptions>, MikroTikOptionsValidator>();

        services.AddOptions<FirmwareUpgradeOptions>()
            .Bind(configuration.GetSection(FirmwareUpgradeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FirmwareUpgradeOptions>, FirmwareUpgradeOptionsValidator>();

        services.AddOptions<SerilogOptions>()
            .Bind(configuration.GetSection(SerilogOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DeviceLogIdentityRegistry>();
        services.AddSingleton<DeviceLogWriter>();
        services.AddHostedService(static sp => sp.GetRequiredService<DeviceLogWriter>());

        services.AddSingleton<IMikrotikConnectionFactory, Tik4netConnectionFactory>();
        services.AddSingleton<MikrotikApiClient>();
        services.AddSingleton<IMikrotikApiClient>(static sp => sp.GetRequiredService<MikrotikApiClient>());
        services.AddSingleton<IMikrotikDeviceInfoReader>(static sp => sp.GetRequiredService<MikrotikApiClient>());
        services.AddSingleton<IMikrotikRouterOperations>(static sp => sp.GetRequiredService<MikrotikApiClient>());
        services.AddSingleton<IOspfMikrotikOperations, OspfMikrotikOperations>();
    }

    /// <summary>
    /// Serializes <paramref name="value"/> as indented JSON into <paramref name="name"/> inside the evidence directory.
    /// <param name="dir">Evidence directory.</param>
    /// <param name="name">Target file name.</param>
    /// <param name="value">Object to serialize.</param>
    /// </summary>
    private static async Task WriteJson(string dir, string name, object value) =>
        await File.WriteAllTextAsync(Path.Combine(dir, name), JsonSerializer.Serialize(value, Indented));

    /// <summary>
    /// Executes a read-only print command on the connection and returns each reply row as a dictionary.
    /// </summary>
    private static async Task<List<Dictionary<string, string>>> PrintAsync(
        ITikConnection connection,
        string command,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand(command)
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(static r => r.Words.ToDictionary(static kv => kv.Key, static kv => kv.Value)).ToList();
    }
}
