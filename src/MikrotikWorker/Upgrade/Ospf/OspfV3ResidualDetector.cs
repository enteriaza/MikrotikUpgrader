// -----------------------------------------------------------------------------
// File: OspfV3ResidualDetector.cs
//
// Description:
// Authoritative post-removal residual check for OSPFv3 configuration on a
// RouterOS 7 unified OSPF inventory. Detects version=3 instances, v3-owned
// areas, dependent templates/ranges, and orphaned or ambiguous leftovers.
// Incomplete inventory never proves removal.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Evaluates whether any OSPFv3 (or OSPFv3-orphan/ambiguous) configuration remains
/// after mandatory OSPFv3 removal.
/// </summary>
/// <remarks>
/// Ownership rules:
/// <list type="bullet">
/// <item>Incomplete inventory (missing/failed/null collections) is never clean.</item>
/// <item>An instance with <c>version=3</c> is always residual.</item>
/// <item>An area whose <c>instance</c> names a live <c>version=3</c> instance is residual.</item>
/// <item>An area whose <c>instance</c> is empty, missing, or names no live OSPFv2 instance is
/// treated as orphaned/ambiguous residual — never accepted as OSPFv2 merely because the parent
/// is gone.</item>
/// <item>Templates and ranges whose <c>area</c> matches a residual area name, or whose area name
/// does not resolve to a live OSPFv2-owned area, are residual.</item>
/// </list>
/// </remarks>
internal static class OspfV3ResidualDetector
{
    /// <summary>One residual OSPFv3 (or ambiguous) object found in live inventory.</summary>
    internal sealed class ResidualObject
    {
        /// <summary>Gets the object kind (instance, area, template, range, inventory).</summary>
        public required string Kind { get; init; }

        /// <summary>Gets the RouterOS <c>.id</c> when known.</summary>
        public string? Id { get; init; }

        /// <summary>Gets the object name when known.</summary>
        public string? Name { get; init; }

        /// <summary>Gets why the object is treated as residual.</summary>
        public required string Detail { get; init; }
    }

    /// <summary>Result of evaluating residual OSPFv3 state.</summary>
    internal sealed class Result
    {
        /// <summary>Gets whether the inventory is free of OSPFv3 and OSPFv3-orphan objects.</summary>
        public required bool IsClean { get; init; }

        /// <summary>
        /// True when residual detection could not run because required collections were missing,
        /// null, not queried, or failed to parse.
        /// </summary>
        public bool InventoryIncomplete { get; init; }

        /// <summary>Gets residual objects; empty when <see cref="IsClean"/> is true.</summary>
        public IReadOnlyList<ResidualObject> Residuals { get; init; } = [];

        /// <summary>Gets a semicolon-joined summary suitable for gate failures.</summary>
        public string? Detail =>
            IsClean
                ? null
                : string.Join("; ", Residuals.Select(static r =>
                    $"{r.Kind} id={r.Id ?? "<none>"} name={r.Name ?? "<none>"}: {r.Detail}"));
    }

    /// <summary>
    /// Evaluates <paramref name="inventory"/> for any remaining OSPFv3 configuration.
    /// Incomplete capture never returns clean.
    /// </summary>
    /// <param name="inventory">Fresh RouterOS 7 OSPF inventory.</param>
    /// <returns>Clean when complete inventory has no residual objects; otherwise a failure.</returns>
    public static Result Evaluate(OspfV7Inventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        if (!TryResolveCompleteCollections(
                inventory,
                out IReadOnlyList<OspfInstanceRow> instances,
                out IReadOnlyList<OspfAreaRow> areas,
                out IReadOnlyList<OspfInterfaceTemplateRow> templates,
                out IReadOnlyList<OspfAreaRangeRow> ranges,
                out string incompletenessDetail))
        {
            return new Result
            {
                IsClean = false,
                InventoryIncomplete = true,
                Residuals =
                [
                    new ResidualObject
                    {
                        Kind = "inventory",
                        Detail = incompletenessDetail,
                    },
                ],
            };
        }

        List<ResidualObject> residuals = [];

        HashSet<string> v3InstanceNames = instances
            .Where(static i => string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase))
            .Select(static i => i.Name?.Trim() ?? string.Empty)
            .Where(static n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> v2InstanceNames = instances
            .Where(static i => !string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase))
            .Select(static i => i.Name?.Trim() ?? string.Empty)
            .Where(static n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (OspfInstanceRow instance in instances)
        {
            if (!string.Equals(instance.Version, "3", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            residuals.Add(new ResidualObject
            {
                Kind = "instance",
                Id = instance.Id,
                Name = instance.Name,
                Detail = "OSPFv3 instance remains after mandatory removal.",
            });
        }

        HashSet<string> residualAreaNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (OspfAreaRow area in areas)
        {
            string? areaName = string.IsNullOrWhiteSpace(area.Name) ? null : area.Name.Trim();
            string? owner = string.IsNullOrWhiteSpace(area.Instance) ? null : area.Instance.Trim();

            if (owner is not null && v3InstanceNames.Contains(owner))
            {
                residuals.Add(new ResidualObject
                {
                    Kind = "area",
                    Id = area.Id,
                    Name = areaName,
                    Detail = $"Area belongs to OSPFv3 instance '{owner}'.",
                });
                if (areaName is not null)
                {
                    residualAreaNames.Add(areaName);
                }

                continue;
            }

            if (owner is null)
            {
                residuals.Add(new ResidualObject
                {
                    Kind = "area",
                    Id = area.Id,
                    Name = areaName,
                    Detail =
                        "Area has empty instance ownership; refusing to treat as OSPFv2 after OSPFv3 removal.",
                });
                if (areaName is not null)
                {
                    residualAreaNames.Add(areaName);
                }

                continue;
            }

            if (!v2InstanceNames.Contains(owner))
            {
                // Parent instance missing or not a live OSPFv2 — orphaned/ambiguous (often former v3).
                residuals.Add(new ResidualObject
                {
                    Kind = "area",
                    Id = area.Id,
                    Name = areaName,
                    Detail =
                        $"Area instance '{owner}' is not a live OSPFv2 instance; treating as OSPFv3 orphan/ambiguous residual.",
                });
                if (areaName is not null)
                {
                    residualAreaNames.Add(areaName);
                }
            }
        }

        HashSet<string> v2OwnedAreaNames = areas
            .Where(a =>
                !string.IsNullOrWhiteSpace(a.Name)
                && !string.IsNullOrWhiteSpace(a.Instance)
                && v2InstanceNames.Contains(a.Instance.Trim())
                && (a.Name is null || !residualAreaNames.Contains(a.Name)))
            .Select(static a => a.Name!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (OspfInterfaceTemplateRow template in templates)
        {
            string? area = string.IsNullOrWhiteSpace(template.Area) ? null : template.Area.Trim();
            if (area is null)
            {
                residuals.Add(new ResidualObject
                {
                    Kind = "template",
                    Id = template.Id,
                    Name = template.Interfaces,
                    Detail = "Interface-template has empty area; ownership cannot be established after OSPFv3 removal.",
                });
                continue;
            }

            if (residualAreaNames.Contains(area) || !v2OwnedAreaNames.Contains(area))
            {
                residuals.Add(new ResidualObject
                {
                    Kind = "template",
                    Id = template.Id,
                    Name = template.Interfaces ?? area,
                    Detail =
                        residualAreaNames.Contains(area)
                            ? $"Interface-template area '{area}' is OSPFv3/orphan residual."
                            : $"Interface-template area '{area}' is not a live OSPFv2-owned area; treating as residual.",
                });
            }
        }

        foreach (OspfAreaRangeRow range in ranges)
        {
            string? area = string.IsNullOrWhiteSpace(range.Area) ? null : range.Area.Trim();
            if (area is null)
            {
                residuals.Add(new ResidualObject
                {
                    Kind = "range",
                    Id = range.Id,
                    Name = range.Range,
                    Detail = "Area-range has empty area; ownership cannot be established after OSPFv3 removal.",
                });
                continue;
            }

            if (residualAreaNames.Contains(area) || !v2OwnedAreaNames.Contains(area))
            {
                residuals.Add(new ResidualObject
                {
                    Kind = "range",
                    Id = range.Id,
                    Name = range.Range ?? area,
                    Detail =
                        residualAreaNames.Contains(area)
                            ? $"Area-range area '{area}' is OSPFv3/orphan residual."
                            : $"Area-range area '{area}' is not a live OSPFv2-owned area; treating as residual.",
                });
            }
        }

        return new Result
        {
            IsClean = residuals.Count == 0,
            Residuals = residuals,
        };
    }

    /// <summary>
    /// Resolves required collections only when capture status and non-null lists prove a complete read.
    /// </summary>
    private static bool TryResolveCompleteCollections(
        OspfV7Inventory inventory,
        out IReadOnlyList<OspfInstanceRow> instances,
        out IReadOnlyList<OspfAreaRow> areas,
        out IReadOnlyList<OspfInterfaceTemplateRow> templates,
        out IReadOnlyList<OspfAreaRangeRow> ranges,
        out string incompletenessDetail)
    {
        instances = inventory.Instances;
        areas = inventory.Areas;
        templates = inventory.Templates;
        ranges = inventory.AreaRanges;

        OspfInventoryCaptureStatus status = inventory.CaptureStatus ?? OspfInventoryCaptureStatus.Incomplete;
        List<string> problems = [];

        if (!status.InstancesCaptured)
        {
            problems.Add("instances collection was not successfully queried/parsed");
        }

        if (!status.AreasCaptured)
        {
            problems.Add("areas collection was not successfully queried/parsed");
        }

        if (!status.TemplatesCaptured)
        {
            problems.Add("interface-templates collection was not successfully queried/parsed");
        }

        if (!status.RangesCaptured)
        {
            problems.Add("area-ranges collection was not successfully queried/parsed");
        }

        // Defensive: a null collection reference is never treated as an empty successful read.
        if (inventory.Instances is null)
        {
            problems.Add("instances collection is null");
            instances = [];
        }

        if (inventory.Areas is null)
        {
            problems.Add("areas collection is null");
            areas = [];
        }

        if (inventory.Templates is null)
        {
            problems.Add("interface-templates collection is null");
            templates = [];
        }

        if (inventory.AreaRanges is null)
        {
            problems.Add("area-ranges collection is null");
            ranges = [];
        }

        if (problems.Count == 0)
        {
            incompletenessDetail = string.Empty;
            return true;
        }

        incompletenessDetail =
            "Incomplete OSPF inventory cannot prove OSPFv3 removal: " + string.Join("; ", problems) + ".";
        return false;
    }
}
