using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MikrotikWorker.Upgrade;

internal static class PackageCompatibilityAuditReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(
        string outputDirectory,
        IReadOnlyList<CatalogueCoverageArchitectureRow> coverage,
        IReadOnlyList<PackageCompatibilityDeviceReport> deviceReports,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        string coverageJson = Path.Combine(outputDirectory, "catalogue-coverage.json");
        string coverageCsv = Path.Combine(outputDirectory, "catalogue-coverage.csv");
        string devicesJson = Path.Combine(outputDirectory, "device-compatibility.json");
        string devicesCsv = Path.Combine(outputDirectory, "device-compatibility.csv");

        await File.WriteAllTextAsync(
                coverageJson,
                JsonSerializer.Serialize(coverage, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(coverageCsv, FormatCoverageCsv(coverage), cancellationToken)
            .ConfigureAwait(false);

        var devicePayload = deviceReports.Select(static r => new
        {
            inventorySource = r.Device.InventorySource,
            fixtureName = r.Device.FixtureName,
            routerIp = r.Device.RouterIp,
            routerIdentity = r.Device.RouterIdentity,
            installedVersion = r.Device.RouterOsVersionRaw,
            architecture = r.Device.Architecture,
            deviceType = r.Classification.DeviceType.ToString(),
            shouldUpgradeToV7 = r.Classification.ShouldUpgradeToV7,
            stages = r.Stages.Select(static s => new
            {
                stage = s.Stage.ToString(),
                target = s.TargetRouterOsVersion,
                status = s.Status.ToString(),
                independentPackages = s.IndependentPackageNames,
                bundledComponentCount = s.BundledComponentCount,
                resolvedFiles = s.ResolvedPackageFileNames,
                missing = s.MissingPackageNames,
                ambiguous = s.AmbiguousPackageCandidates,
                explanation = s.Explanation,
            }),
        });

        await File.WriteAllTextAsync(
                devicesJson,
                JsonSerializer.Serialize(devicePayload, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(devicesCsv, FormatDevicesCsv(deviceReports), cancellationToken)
            .ConfigureAwait(false);
    }

    public static string FormatCoverageCsv(IReadOnlyList<CatalogueCoverageArchitectureRow> rows)
    {
        StringBuilder sb = new();
        sb.AppendLine("Architecture,TargetVersion,MainPackage,IndependentOptionalPackages,OptionalCount,CatalogueStatus");
        foreach (CatalogueCoverageArchitectureRow row in rows)
        {
            sb.Append(Escape(row.Architecture)).Append(',')
                .Append(Escape(row.TargetVersion)).Append(',')
                .Append(Escape(row.MainPackageFileName)).Append(',')
                .Append(Escape(string.Join(';', row.IndependentOptionalPackageFileNames))).Append(',')
                .Append(row.IndependentOptionalPackageFileNames.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(row.CatalogueStatus))
                .AppendLine();
        }

        return sb.ToString();
    }

    public static string FormatDevicesCsv(IReadOnlyList<PackageCompatibilityDeviceReport> reports)
    {
        StringBuilder sb = new();
        sb.AppendLine(
            "InventorySource,FixtureName,Router,Identity,Architecture,InstalledVersion,Stage,TargetVersion,RequiredPackages,ResolvedFiles,Missing,Ambiguous,Result,Explanation");
        foreach (PackageCompatibilityDeviceReport report in reports)
        {
            foreach (PackageCompatibilityStageResult stage in report.Stages)
            {
                sb.Append(Escape(stage.InventorySource)).Append(',')
                    .Append(Escape(stage.FixtureName)).Append(',')
                    .Append(Escape(stage.RouterIp)).Append(',')
                    .Append(Escape(stage.RouterIdentity)).Append(',')
                    .Append(Escape(stage.Architecture)).Append(',')
                    .Append(Escape(stage.InstalledRouterOsVersion)).Append(',')
                    .Append(Escape(stage.Stage.ToString())).Append(',')
                    .Append(Escape(stage.TargetRouterOsVersion)).Append(',')
                    .Append(Escape(string.Join(';', stage.IndependentPackageNames))).Append(',')
                    .Append(Escape(string.Join(';', stage.ResolvedPackageFileNames))).Append(',')
                    .Append(Escape(string.Join(';', stage.MissingPackageNames))).Append(',')
                    .Append(Escape(string.Join(';', stage.AmbiguousPackageCandidates))).Append(',')
                    .Append(Escape(stage.Status.ToString())).Append(',')
                    .Append(Escape(stage.Explanation))
                    .AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Escape(string? value)
    {
        string text = value ?? string.Empty;
        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return text;
    }
}
