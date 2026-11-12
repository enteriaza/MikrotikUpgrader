using System.Net;
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
/// Strictly read-only Tommy OSPF/state audit. No mutations, no job replay, no backup create.
/// </summary>
public sealed class TommyReadOnlyOspfAuditHarness
{
    public const string EvidenceRootEnv = "TOMMY_RO_AUDIT_EVIDENCE_DIR";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    [Trait("Category", "Live")]
    public async Task Inspect_Tommy_ReadOnly_AfterFullUpgradeAttempt()
    {
        LiveMikrotikTestGate.RequireArmed(LiveMikrotikTestGate.AllowLiveMikrotikEnv, "Live");

        string evidenceDir = Environment.GetEnvironmentVariable(EvidenceRootEnv)
            ?? Path.Combine(
                Path.GetTempPath(),
                "TommyReadOnlyOspfAudit",
                DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(evidenceDir);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        string workerDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "MikrotikWorker"));
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
        Assert.False(upgrade.Value.Enabled, "FirmwareUpgrade.Enabled must remain false for read-only audit.");

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

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        using ITikConnection connection = await factory.CreateAsync(connOpts, cts.Token);
        MikrotikDeviceInfo info = await deviceInfo.ReadDeviceInfoAsync(connection, targetIp.ToString(), cts.Token);

        Assert.Equal(expectedIdentity, info.Identity);
        Assert.Equal("mipsbe", info.ArchitectureName, ignoreCase: true);

        OspfV7Inventory inventory = await ops.ReadV7InventoryAsync(connection, cts.Token);
        IReadOnlyList<OspfIpAddressRow> addresses = await ops.ReadIpAddressInventoryAsync(connection, cts.Token);
        IReadOnlyList<Dictionary<string, string>> neighbours =
            await ops.ReadOspfNeighboursAsync(connection, cts.Token);

        List<Dictionary<string, string>> routerboard = await PrintAsync(connection, "/system/routerboard/print", cts.Token);
        List<Dictionary<string, string>> resource = await PrintAsync(connection, "/system/resource/print", cts.Token);
        List<Dictionary<string, string>> packages = await PrintAsync(connection, "/system/package/print", cts.Token);
        List<Dictionary<string, string>> routes = await PrintAsync(connection, "/ip/route/print", cts.Token);
        OspfLoopbackRouterIdResult loopback = OspfLoopbackRouterId.Derive(addresses, "10.16.0.11");

        var report = new
        {
            inspectedAt = DateTimeOffset.Now,
            host = targetIp.ToString(),
            expectedIdentity,
            device = new
            {
                info.Identity,
                info.Version,
                info.ArchitectureName,
                info.BoardName,
                info.RouterboardModel,
                info.RouterboardCurrentFirmware,
                info.RouterboardUpgradeFirmware,
                info.RouterboardFactoryFirmware,
                info.Uptime,
            },
            resource = resource.FirstOrDefault(),
            packages,
            routerboard = routerboard.FirstOrDefault(),
            loopbacks = addresses
                .Where(a => OspfLoopbackRouterId.IsSupportedLoopbackInterfaceName(a.Interface))
                .Select(a => new { a.Interface, a.Address, a.Network })
                .ToList(),
            loopbackDerive = new
            {
                loopback.Succeeded,
                loopback.RouterId,
                loopback.LoopbackCidr,
                loopback.LoopbackInterfaceName,
                loopback.IgnoredOutsideRangeAddresses,
                loopback.ObservedInScopeAddresses,
                loopback.Errors,
            },
            instances = inventory.Instances.Select(i => new
            {
                i.Id,
                i.Name,
                i.RouterId,
                i.Version,
                i.InFilter,
                i.OutFilter,
                Redistribute = i.RedistributeProperties,
            }),
            areas = inventory.Areas.Select(a => new
            {
                a.Id,
                a.Name,
                a.AreaId,
                a.Instance,
                a.Type,
                a.DefaultCost,
                a.NoSummaries,
            }),
            ranges = inventory.AreaRanges.Select(r => new
            {
                r.Id,
                r.Area,
                r.Range,
                r.Comment,
                r.Advertise,
            }),
            templates = inventory.Templates.Select(t => new
            {
                t.Id,
                t.Area,
                t.Interfaces,
                t.Networks,
                t.Type,
                t.Cost,
                t.Passive,
                t.Disabled,
                t.Comment,
            }),
            filters = inventory.Filters.Select(f => new { f.Id, f.Name }),
            neighbours,
            managementRoutes = routes
                .Where(r =>
                    (r.TryGetValue("dst-address", out string? dst)
                     && (dst.Contains("198.18.", StringComparison.Ordinal)
                         || dst.StartsWith("0.0.0.0", StringComparison.Ordinal)))
                    || (r.TryGetValue("gateway", out string? gw)
                        && gw.Contains("198.18.", StringComparison.Ordinal)))
                .Take(40)
                .ToList(),
        };

        string jsonPath = Path.Combine(evidenceDir, "tommy-readonly-ospf-audit.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, Indented), cts.Token);
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDir, "verdict-hint.txt"),
            "READ-ONLY AUDIT COMPLETE — no mutations performed.",
            cts.Token);

        Assert.True(File.Exists(jsonPath));
        Assert.Contains(
            inventory.Instances,
            i => string.Equals(i.Name, "default-v2", StringComparison.OrdinalIgnoreCase));
    }

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
