// -----------------------------------------------------------------------------
// File: LivePackageCompatibilityAuditReportWriter.cs
//
// Description:
// Writes live fleet package-compatibility audit results to CSV and JSON under
// the configured report directory.
//
// -----------------------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Writes live fleet package-compatibility audit CSV and JSON report files.
/// </summary>
internal static class LivePackageCompatibilityAuditReportWriter
{
    /// <summary>
    /// Indented JSON that omits null values.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Writes the fleet audit report as a CSV and a JSON file, choosing file names that do not overwrite earlier reports.
    /// </summary>
    /// <param name="outputDirectory">Directory to write into; created when missing.</param>
    /// <param name="report">Report to write.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The paths of the CSV and JSON files written.</returns>
    public static async Task<(string CsvPath, string JsonPath)> WriteAsync(
        string outputDirectory,
        LiveFleetAuditReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(report);

        Directory.CreateDirectory(outputDirectory);
        string stamp = report.FinishedAtUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string csvPath = UniquePath(outputDirectory, $"live-fleet-compatibility-{stamp}.csv");
        string jsonPath = UniquePath(outputDirectory, $"live-fleet-compatibility-{stamp}.json");

        await File.WriteAllTextAsync(csvPath, FormatCsv(report), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
                jsonPath,
                SerializeReport(report),
                cancellationToken)
            .ConfigureAwait(false);

        return (csvPath, jsonPath);
    }

    /// <summary>
    /// Live fleet audit JSON uses anonymous projections; not on the Native AOT upgrade hot path.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Live fleet audit reports are diagnostic tooling; anonymous JSON projections are not trimmed for AOT.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Live fleet audit reports are diagnostic tooling; anonymous JSON projections are not trimmed for AOT.")]
    private static string SerializeReport(LiveFleetAuditReport report) =>
        JsonSerializer.Serialize(ToJsonPayload(report), JsonOptions);

    /// <summary>
    /// Formats the fleet audit as CSV text.
    /// </summary>
    public static string FormatCsv(LiveFleetAuditReport report)
    {
        StringBuilder sb = new();
        sb.AppendLine(
            "RouterName,RouterIp,Identity,IdentityStatus,RouterOsVersion,Architecture,InventoryStatus," +
            "MainPackage,IndependentOptionalPackages,BundledComponents,BundledCount," +
            "V6Status,V7Status,MissingPackages,AmbiguousPackages,ResolverDetail," +
            "CollectionDurationMs,AuditedAtUtc");

        foreach (LiveFleetRouterAuditRecord row in report.Routers)
        {
            PackageInventorySummary inventory = PackageInventoryReporting.Summarize(row.Collection.Packages);
            string? main = inventory.IndependentPackages
                .FirstOrDefault(static p => UpgradePackageResolver.IsMainRouterOsPackage(p.Name))
                ?.Name;
            IEnumerable<string> optionals = inventory.IndependentPackages
                .Where(static p => !UpgradePackageResolver.IsMainRouterOsPackage(p.Name))
                .Select(static p => p.Name);
            string bundledNames = string.Join(';', inventory.BundledComponents.Select(static p => p.Name));

            PackageCompatibilityStageResult? v6 = row.Compatibility?.Stages
                .FirstOrDefault(static s => s.Stage == PackageCompatibilityUpgradeStage.V6Stepping);
            PackageCompatibilityStageResult? v7 = row.Compatibility?.Stages
                .FirstOrDefault(static s => s.Stage == PackageCompatibilityUpgradeStage.RouterOs7);

            IEnumerable<string> missing = (v6?.MissingPackageNames ?? [])
                .Concat(v7?.MissingPackageNames ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> ambiguous = (v6?.AmbiguousPackageCandidates ?? [])
                .Concat(v7?.AmbiguousPackageCandidates ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase);

            string resolverDetail = string.Join(
                " | ",
                new[] { row.Collection.FailureDetail, v6?.Explanation, v7?.Explanation }
                    .Where(static s => !string.IsNullOrWhiteSpace(s)));

            sb.Append(Escape(row.Input.RouterName)).Append(',')
                .Append(Escape(row.Input.RouterIp?.ToString() ?? row.Input.RouterIpText)).Append(',')
                .Append(Escape(row.Collection.Identity)).Append(',')
                .Append(Escape(row.Collection.IdentityStatus.ToString())).Append(',')
                .Append(Escape(row.Collection.RouterOsVersion)).Append(',')
                .Append(Escape(row.Collection.Architecture)).Append(',')
                .Append(Escape(row.Collection.CollectionStatus.ToString())).Append(',')
                .Append(Escape(main)).Append(',')
                .Append(Escape(string.Join(';', optionals))).Append(',')
                .Append(Escape(bundledNames)).Append(',')
                .Append(inventory.BundledCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(v6?.Status.ToString())).Append(',')
                .Append(Escape(v7?.Status.ToString())).Append(',')
                .Append(Escape(string.Join(';', missing))).Append(',')
                .Append(Escape(string.Join(';', ambiguous))).Append(',')
                .Append(Escape(resolverDetail)).Append(',')
                .Append(((long)row.Collection.Duration.TotalMilliseconds)
                    .ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(row.AuditedAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)))
                .AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Projects the report into the camelCase shape serialized to JSON.
    /// </summary>
    /// <param name="report">Report to project.</param>
    /// <returns>An anonymous object ready for serialization.</returns>
    private static object ToJsonPayload(LiveFleetAuditReport report) =>
        new
        {
            summary = new
            {
                totalInputRows = report.Summary.TotalInputRows,
                validUniqueAddresses = report.Summary.ValidUniqueAddresses,
                duplicateAddresses = report.Summary.DuplicateAddresses,
                invalidInputRows = report.Summary.InvalidInputRows,
                successfullyCollected = report.Summary.SuccessfullyCollected,
                connectionFailures = report.Summary.ConnectionFailures,
                partialInventories = report.Summary.PartialInventories,
                fullyCompatibleDevices = report.Summary.FullyCompatibleDevices,
                blockedByMissingPackages = report.Summary.BlockedByMissingPackages,
                ambiguousPackages = report.Summary.AmbiguousPackages,
                aceOrBrExcludedFromV7 = report.Summary.AceOrBrExcludedFromV7,
                unknownOrUnavailableIdentity = report.Summary.UnknownOrUnavailableIdentity,
                completed = report.Summary.Completed,
                cancelled = report.Summary.Cancelled,
                notes = report.Summary.Notes,
            },
            v6SteppingTarget = report.V6SteppingTarget,
            v7Target = report.V7Target,
            startedAtUtc = report.StartedAtUtc,
            finishedAtUtc = report.FinishedAtUtc,
            routers = report.Routers.Select(static r =>
            {
                PackageInventorySummary inventory = PackageInventoryReporting.Summarize(r.Collection.Packages);
                PackageCompatibilityStageResult? v6 = r.Compatibility?.Stages
                    .FirstOrDefault(static s => s.Stage == PackageCompatibilityUpgradeStage.V6Stepping);
                PackageCompatibilityStageResult? v7 = r.Compatibility?.Stages
                    .FirstOrDefault(static s => s.Stage == PackageCompatibilityUpgradeStage.RouterOs7);
                return new
                {
                    routerName = r.Input.RouterName,
                    routerIp = r.Input.RouterIp?.ToString() ?? r.Input.RouterIpText,
                    rowNumber = r.Input.RowNumber,
                    inputValid = r.Input.IsValid,
                    inputDuplicate = r.Input.IsDuplicate,
                    inputError = r.Input.ValidationError,
                    identity = r.Collection.Identity,
                    identityStatus = r.Collection.IdentityStatus.ToString(),
                    routerOsVersion = r.Collection.RouterOsVersion,
                    architecture = r.Collection.Architecture,
                    inventoryStatus = r.Collection.CollectionStatus.ToString(),
                    collectionFailureDetail = r.Collection.FailureDetail,
                    mainPackage = inventory.IndependentPackages
                        .FirstOrDefault(static p => UpgradePackageResolver.IsMainRouterOsPackage(p.Name))
                        ?.Name,
                    independentOptionalPackages = inventory.IndependentPackages
                        .Where(static p => !UpgradePackageResolver.IsMainRouterOsPackage(p.Name))
                        .Select(static p => p.Name)
                        .ToList(),
                    bundledComponents = inventory.BundledComponents.Select(static p => p.Name).ToList(),
                    bundledCount = inventory.BundledCount,
                    v6Status = v6?.Status.ToString(),
                    v7Status = v7?.Status.ToString(),
                    missingPackages = (v6?.MissingPackageNames ?? [])
                        .Concat(v7?.MissingPackageNames ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    ambiguousPackages = (v6?.AmbiguousPackageCandidates ?? [])
                        .Concat(v7?.AmbiguousPackageCandidates ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    resolverDetail = string.Join(
                        " | ",
                        new[] { r.Collection.FailureDetail, v6?.Explanation, v7?.Explanation }
                            .Where(static s => !string.IsNullOrWhiteSpace(s))),
                    collectionDurationMs = (long)r.Collection.Duration.TotalMilliseconds,
                    auditedAtUtc = r.AuditedAtUtc,
                    commandsIssued = r.Collection.CommandsIssued,
                    stages = r.Compatibility?.Stages.Select(static s => new
                    {
                        stage = s.Stage.ToString(),
                        target = s.TargetRouterOsVersion,
                        status = s.Status.ToString(),
                        explanation = s.Explanation,
                        missing = s.MissingPackageNames,
                        ambiguous = s.AmbiguousPackageCandidates,
                        resolvedFiles = s.ResolvedPackageFileNames,
                    }),
                };
            }),
        };

    /// <summary>
    /// Returns a path in the directory that does not yet exist, adding a numeric suffix when the file name is taken.
    /// </summary>
    /// <param name="directory">Target directory.</param>
    /// <param name="fileName">Preferred file name.</param>
    /// <returns>An unused file path.</returns>
    private static string UniquePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        string name = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(directory, $"{name}-{i}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{ext}");
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
