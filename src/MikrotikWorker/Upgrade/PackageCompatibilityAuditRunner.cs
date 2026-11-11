using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MikrotikWorker.Assessment;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Opt-in CLI entry for non-live package compatibility auditing against synthetic fixtures
/// and the on-disk firmware catalogue. Does not connect to routers.
/// </summary>
internal static class PackageCompatibilityAuditRunner
{
    public const string FixtureAuditArgument = "--audit-package-compatibility";

    public static bool IsFixtureAuditRequested(string[] args) =>
        args.Any(static a => string.Equals(a, FixtureAuditArgument, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunFixtureAuditAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        string outputDir = ResolveOutputDirectory(args);
        WebApplicationBuilder builder = MikrotikWorkerHost.CreateApplicationBuilder(args);
        // Audit path does not need the full host started; build the service provider only.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        await using WebApplication app = builder.Build();
        IServiceProvider services = app.Services;

        IFirmwarePackageCatalog catalog = services.GetRequiredService<IFirmwarePackageCatalog>();
        IUpgradePackageResolver resolver = services.GetRequiredService<IUpgradePackageResolver>();
        IRouterOsV7TargetProvider v7 = services.GetRequiredService<IRouterOsV7TargetProvider>();
        ILoggerFactory loggerFactory = services.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("PackageCompatibilityAudit");

        IHostedService? v7Hosted = v7 as IHostedService;
        if (v7Hosted is not null)
        {
            await v7Hosted.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            RouterOsV7TargetSnapshot? snapshot = v7.TryGetCurrent();
            logger.LogInformation(
                "Package compatibility fixture audit starting. V6Stepping={V6Stepping}, V7Target={V7Target}, Output={Output}",
                RouterOsUpgradeVersions.V6SteppingVersionText,
                snapshot?.VersionText ?? "(unavailable)",
                outputDir);

            CatalogueCoverageReporter coverageReporter = new(catalog);
            List<CatalogueCoverageArchitectureRow> coverage = [];
            coverage.AddRange(coverageReporter.BuildCoverage(RouterOsUpgradeVersions.V6Stepping));
            if (snapshot is not null)
            {
                coverage.AddRange(coverageReporter.BuildCoverage(snapshot.Version));
            }

            PackageCompatibilityAuditor auditor = new(resolver, catalog, v7);
            IReadOnlyList<PackageCompatibilityDeviceReport> reports =
                auditor.AssessDevices(SyntheticPackageInventoryFixtures.All());

            await PackageCompatibilityAuditReportWriter
                .WriteAsync(outputDir, coverage, reports, cancellationToken)
                .ConfigureAwait(false);

            int compatible = reports.SelectMany(static r => r.Stages)
                .Count(static s => s.Status == CatalogueCompatibilityStatus.Compatible);
            int blocked = reports.SelectMany(static r => r.Stages)
                .Count(static s => s.Status is not CatalogueCompatibilityStatus.Compatible
                    and not CatalogueCompatibilityStatus.NotApplicable);

            logger.LogInformation(
                "Package compatibility fixture audit complete. Devices={DeviceCount}, CompatibleStages={Compatible}, NonApplicableOrBlockedStages tracked (blocked={Blocked}). Reports written to {Output}",
                reports.Count,
                compatible,
                blocked,
                outputDir);

            return 0;
        }
        finally
        {
            if (v7Hosted is not null)
            {
                await v7Hosted.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (v7 is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static string ResolveOutputDirectory(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "Reports", "PackageCompatibility");
    }
}
