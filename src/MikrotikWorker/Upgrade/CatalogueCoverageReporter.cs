// -----------------------------------------------------------------------------
// File: CatalogueCoverageReporter.cs
//
// Description:
// Reports firmware-catalogue coverage per supported architecture for
// package-compatibility audits (main .npk present, optionals listed).
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Builds per-architecture catalogue coverage rows for a target RouterOS version.
/// </summary>
internal interface ICatalogueCoverageReporter
{
    /// <summary>
    /// Returns one coverage row per architecture for packages at <paramref name="targetVersion"/>.
    /// </summary>
    /// <param name="targetVersion">Catalogue version to inspect.</param>
    /// <param name="architectures">Architectures to report; defaults to <see cref="CatalogueCoverageReporter.SupportedArchitectures"/>.</param>
    IReadOnlyList<CatalogueCoverageArchitectureRow> BuildCoverage(
        RouterOsVersion targetVersion,
        IReadOnlyList<string>? architectures = null);
}

/// <summary>
/// Default catalogue coverage reporter over <see cref="IFirmwarePackageCatalog"/>.
/// </summary>
internal sealed class CatalogueCoverageReporter(IFirmwarePackageCatalog packageCatalog)
    : ICatalogueCoverageReporter
{
    /// <summary>
    /// Architectures reported when the caller does not supply an explicit list.
    /// </summary>
    public static readonly string[] SupportedArchitectures =
    [
        "mipsbe", "arm", "arm64", "mmips", "smips", "tile", "powerpc", "x86",
    ];

    /// <inheritdoc />
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
