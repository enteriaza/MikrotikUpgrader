using MikrotikWorker.Assessment;

namespace MikrotikWorker.Upgrade;

internal interface ICatalogueCoverageReporter
{
    IReadOnlyList<CatalogueCoverageArchitectureRow> BuildCoverage(
        RouterOsVersion targetVersion,
        IReadOnlyList<string>? architectures = null);
}

internal sealed class CatalogueCoverageReporter(IFirmwarePackageCatalog packageCatalog)
    : ICatalogueCoverageReporter
{
    public static readonly string[] SupportedArchitectures =
    [
        "mipsbe", "arm", "arm64", "mmips", "smips", "tile", "powerpc", "x86",
    ];

    public IReadOnlyList<CatalogueCoverageArchitectureRow> BuildCoverage(
        RouterOsVersion targetVersion,
        IReadOnlyList<string>? architectures = null)
    {
        IReadOnlyList<string> arches = architectures ?? SupportedArchitectures;
        IReadOnlyList<FirmwarePackageInfo> atTarget = packageCatalog.GetPackages()
            .Where(p => p.Version == targetVersion)
            .ToList();

        List<CatalogueCoverageArchitectureRow> rows = [];
        foreach (string arch in arches)
        {
            string normalized = FirmwarePackageFileNameParser.NormalizeArchitecture(arch);
            List<FirmwarePackageInfo> forArch = atTarget
                .Where(p => UpgradePackageResolver.ArchitectureAcceptable(normalized, p.Architecture))
                .OrderBy(static p => p.PackageName, StringComparer.Ordinal)
                .ThenBy(static p => p.FileName, StringComparer.Ordinal)
                .ToList();

            FirmwarePackageInfo? main = forArch
                .FirstOrDefault(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal));
            List<string> optionals = forArch
                .Where(static p => !p.PackageName.Equals("routeros", StringComparison.Ordinal))
                .Select(static p => p.FileName)
                .ToList();

            string status = main is null
                ? "MissingMain"
                : optionals.Count == 0
                    ? "MainOnly"
                    : "MainAndOptionals";

            rows.Add(new CatalogueCoverageArchitectureRow
            {
                Architecture = normalized,
                TargetVersion = targetVersion.ToString(),
                MainPackageFileName = main?.FileName,
                IndependentOptionalPackageFileNames = optionals,
                CatalogueStatus = status,
            });
        }

        return rows;
    }
}
