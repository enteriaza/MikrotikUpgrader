// -----------------------------------------------------------------------------
// File: PackageCompatibilityAuditReportWriter.cs
//
// Description:
// Serializes non-live package-compatibility audit reports to CSV/JSON
// artefacts.
//
// -----------------------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MikrotikWorker.Serialization;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Writes non-live package-compatibility audit reports to CSV/JSON.
/// </summary>
internal static class PackageCompatibilityAuditReportWriter
{
    /// <summary>
    /// Indented JSON that omits null values.
    /// </summary>
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Writes the catalogue coverage and device compatibility reports as both JSON and CSV files.
    /// </summary>
    /// <param name="outputDirectory">Directory to write into; created when missing.</param>
    /// <param name="coverage">Catalogue coverage rows.</param>
    /// <param name="deviceReports">Per-device compatibility reports.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task that completes when all files are written.</returns>
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
                JsonSerializer.Serialize(
                    coverage,
                    CatalogueCoverageJsonContext.Default.IReadOnlyListCatalogueCoverageArchitectureRow),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(coverageCsv, FormatCoverageCsv(coverage), cancellationToken)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
                devicesJson,
                SerializeDevicePayload(deviceReports),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(devicesCsv, FormatDevicesCsv(deviceReports), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Diagnostic audit JSON uses anonymous projections; not on the Native AOT upgrade hot path.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Package compatibility audit reports are diagnostic tooling; anonymous JSON projections are not trimmed for AOT.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Package compatibility audit reports are diagnostic tooling; anonymous JSON projections are not trimmed for AOT.")]
    private static string SerializeDevicePayload(IReadOnlyList<PackageCompatibilityDeviceReport> deviceReports)
    {
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

        return JsonSerializer.Serialize(devicePayload, DiagnosticJsonOptions);
    }

    /// <summary>
    /// Formats catalogue coverage rows as CSV text with a header row.
    /// </summary>
    /// <param name="rows">Coverage rows.</param>
    /// <returns>The CSV text.</returns>
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

    /// <summary>
    /// Formats one CSV row per device stage, with a header row.
    /// </summary>
    /// <param name="reports">Per-device compatibility reports.</param>
    /// <returns>The CSV text.</returns>
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

    /// <summary>
    /// Escapes a value for CSV, quoting it when it contains a quote, comma, or line break.
    /// </summary>
    /// <param name="value">Text to escape; null is treated as empty.</param>
    /// <returns>The CSV-safe text.</returns>
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
