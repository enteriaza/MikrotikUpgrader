using System.Text.Json.Serialization;
using MikrotikWorker.Backup;
using MikrotikWorker.Upgrade;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Serialization;

/// <summary>Source-generated STJ for Native AOT backup manifests (PascalCase, indented).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BackupManifest))]
internal partial class BackupManifestJsonContext : JsonSerializerContext;

/// <summary>Source-generated STJ for OSPF plan persistence (camelCase, indented).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OspfMigrationPlan))]
internal partial class OspfMigrationPlanJsonContext : JsonSerializerContext;

/// <summary>
/// Source-generated STJ for OSPF plan content hashing (camelCase, compact).
/// Shape must stay aligned with the historical anonymous payload used by
/// <see cref="OspfMigrationPlanner.ComputeContentHash"/>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OspfMigrationPlanHashPayload))]
internal partial class OspfPlanHashJsonContext : JsonSerializerContext;

/// <summary>Source-generated STJ for package-compatibility catalogue coverage reports.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IReadOnlyList<CatalogueCoverageArchitectureRow>))]
[JsonSerializable(typeof(CatalogueCoverageArchitectureRow))]
internal partial class CatalogueCoverageJsonContext : JsonSerializerContext;
