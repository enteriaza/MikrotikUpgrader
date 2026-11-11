using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Upgrade;
using tik4net;

namespace MikrotikWorker.Tests;

/// <summary>
/// One-shot read-only audit: collect live inventory from 198.18.240.11 and pass it through
/// production <see cref="PostUpgradePackageVerifier"/> (not a synthetic fixture).
/// </summary>
[Trait("Category", "Live")]
public sealed class LivePostUpgradeVerifierAuditTests
{
    private const string LiveHost = "198.18.240.11";
    private static readonly RouterOsVersion TargetV7 = RouterOsVersion.Parse("7.23.8");

    [Fact]
    public async Task LiveInventory_PassesProductionPostUpgradePackageVerifier()
    {
        string repoRoot = FindRepoRoot();
        string localConfig = Path.Combine(
            repoRoot,
            "src",
            "MikrotikWorker",
            "appsettings.Development.local.json");
        Assert.True(File.Exists(localConfig), $"Missing {localConfig}");

        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(localConfig, optional: false)
            .Build();

        // Safeguard: do not run if upgrades were left enabled in local config accidentally
        // (this test still only performs read-only API commands).
        Assert.False(
            string.Equals(config["FirmwareUpgrade:Enabled"], "true", StringComparison.OrdinalIgnoreCase),
            "FirmwareUpgrade:Enabled must remain false for this audit.");

        string username = config["Mikrotik:Username"] ?? string.Empty;
        string password = config["Mikrotik:Password"] ?? string.Empty;
        int port = int.TryParse(config["Mikrotik:Port"], out int p) ? p : 8728;
        Assert.False(string.IsNullOrWhiteSpace(username));
        Assert.False(string.IsNullOrWhiteSpace(password));

        MikrotikApiClient client = new(
            new ThrowingMikrotikConnectionFactory(new IOException("unused")),
            Options.Create(new MikroTikOptions
            {
                Enabled = true,
                Username = username,
                Password = password,
                Port = port,
            }),
            NullLogger<MikrotikApiClient>.Instance);

        using ITikConnection connection = ConnectionFactory.OpenConnection(
            TikConnectionType.Api,
            LiveHost,
            port,
            username,
            password);

        // Exact authorised read-only commands via production client helpers.
        MikrotikDeviceInfo device = await client.ReadDeviceInfoAsync(connection, LiveHost);
        IReadOnlyList<InstalledPackageInfo> packages =
            await client.ListInstalledPackagesAsync(connection);

        // Also re-parse raw /system/package/print through the production collector parser.
        IEnumerable<ITikReSentence> rawPackageRows = connection
            .CreateCommand("/system/package/print")
            .ExecuteList()
            .OfType<ITikReSentence>();
        (List<InstalledPackageInfo> collectorPackages, int unparsable) =
            ReadOnlyMikrotikInventoryCollector.ParsePackages(rawPackageRows);

        Assert.Equal("Mikrotik", device.Identity);
        Assert.StartsWith("7.23.8", device.Version, StringComparison.Ordinal);
        Assert.Equal("mipsbe", device.ArchitectureName, ignoreCase: true);
        Assert.Equal(packages.Count, collectorPackages.Count);
        Assert.Equal(0, unparsable);

        string firmwareRoot = Path.Combine(repoRoot, "FirmwareFiles");
        UpgradePackageResolver resolver = new(
            new StaticFirmwarePackageCatalog(firmwareRoot),
            Options.Create(new FirmwareFilesOptions { Path = firmwareRoot }));

        // Reconstruct the pre-upgrade independent inventory that drove the live FullUpgrade
        // (routeros-mipsbe at 6.49.23). Required packages must come from the resolver, not hand-waved.
        UpgradePackageResolution resolution = resolver.Resolve(
            device.ArchitectureName,
            [new InstalledPackageInfo("routeros-mipsbe", "6.49.23", false)],
            TargetV7);

        Assert.True(resolution.Succeeded, resolution.FailureReason);
        Assert.Single(resolution.Packages);
        Assert.Equal("routeros-mipsbe", resolution.Packages[0].InstalledPackageName);
        Assert.Equal("routeros", resolution.Packages[0].ExpectedInstalledPackageName);
        Assert.Equal(
            "routeros-7.23.8-mipsbe.npk",
            resolution.Packages[0].CatalogPackage.FileName,
            ignoreCase: true);

        string? expectedArch = FirmwarePackageFileNameParser.NormalizeArchitecture(
            device.ArchitectureName!);

        // Exact production verifier used by RouterOsV6UpgradeExecutor — live collector packages.
        PostUpgradePackageVerification verification = PostUpgradePackageVerifier.Verify(
            TargetV7,
            expectedArch,
            collectorPackages,
            resolution.Packages,
            unparsablePackageRows: unparsable);

        var evidence = new
        {
            Host = LiveHost,
            device.Identity,
            device.Version,
            device.ArchitectureName,
            Routerboard = new
            {
                device.RouterboardModel,
                device.RouterboardFirmwareType,
                device.RouterboardCurrentFirmware,
                device.RouterboardUpgradeFirmware,
                device.RouterboardFactoryFirmware,
            },
            CollectorPackages = collectorPackages.Select(p => new
            {
                p.Name,
                p.Version,
                p.Bundle,
                p.IsBundledComponent,
            }),
            UnparsableRows = unparsable,
            TargetVersion = TargetV7.ToString(),
            ExpectedArchitecture = expectedArch,
            Required = resolution.Packages.Select(p => new
            {
                p.InstalledPackageName,
                p.ExpectedInstalledPackageName,
                p.CatalogPackage.FileName,
            }),
            VerifierSucceeded = verification.Succeeded,
            verification.FailureOutcome,
            verification.Detail,
        };

        string evidencePath = Path.Combine(
            Path.GetTempPath(),
            "live-postupgrade-verifier-audit.json");
        await File.WriteAllTextAsync(
            evidencePath,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(
            verification.Succeeded,
            $"Production verifier failed on live inventory. Detail={verification.Detail}. Evidence={evidencePath}");
        Assert.Contains(
            collectorPackages,
            static p => !p.IsBundledComponent
                && p.Name.Equals("routeros", StringComparison.OrdinalIgnoreCase)
                && p.Version.StartsWith("7.23.8", StringComparison.Ordinal));
        Assert.DoesNotContain(collectorPackages, static p => p.IsBundledComponent);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MikrotikUpgrader.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate MikrotikUpgrader.sln from test base directory.");
    }
}
