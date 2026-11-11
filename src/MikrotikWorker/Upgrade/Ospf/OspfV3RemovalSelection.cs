namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Computes instance-safe OSPFv3 removal targets from a v7 inventory.
/// Areas belong to instances via <c>instance=</c>; templates/ranges reference areas by name.
/// When a v3 area name also exists on an OSPFv2 instance, selection fails closed.
/// </summary>
internal static class OspfV3RemovalSelection
{
    internal sealed class Result
    {
        public required bool Succeeded { get; init; }
        public string? FailureDetail { get; init; }
        public IReadOnlyList<string> TemplateIds { get; init; } = [];
        public IReadOnlyList<string> RangeIds { get; init; } = [];
        public IReadOnlyList<string> AreaIds { get; init; } = [];
        public IReadOnlyList<string> InstanceIds { get; init; } = [];
    }

    public static Result Build(OspfV7Inventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        List<OspfInstanceRow> v3Instances = inventory.Instances
            .Where(static i => string.Equals(i.Version, "3", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (v3Instances.Count == 0)
        {
            return new Result { Succeeded = true };
        }

        HashSet<string> v3InstanceNames = v3Instances
            .Select(static i => i.Name ?? string.Empty)
            .Where(static n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<OspfAreaRow> v3Areas = inventory.Areas
            .Where(a => a.Instance is not null
                        && v3InstanceNames.Contains(a.Instance))
            .ToList();

        List<OspfAreaRow> v2Areas = inventory.Areas
            .Where(a => a.Instance is null
                        || !v3InstanceNames.Contains(a.Instance))
            .ToList();

        foreach (OspfAreaRow v3Area in v3Areas)
        {
            if (string.IsNullOrWhiteSpace(v3Area.Name))
            {
                return new Result
                {
                    Succeeded = false,
                    FailureDetail =
                        "OSPFv3 area is missing a name; refusing ambiguous dependency removal.",
                };
            }

            if (string.IsNullOrWhiteSpace(v3Area.Id))
            {
                return new Result
                {
                    Succeeded = false,
                    FailureDetail =
                        $"OSPFv3 area '{v3Area.Name}' is missing .id; refusing removal without instance-scoped id.",
                };
            }

            bool nameCollision = v2Areas.Any(v2 =>
                string.Equals(v2.Name, v3Area.Name, StringComparison.OrdinalIgnoreCase));
            if (nameCollision)
            {
                return new Result
                {
                    Succeeded = false,
                    FailureDetail =
                        $"OSPFv3 area name '{v3Area.Name}' also exists on an OSPFv2 instance; refusing unsafe name-based dependency removal.",
                };
            }
        }

        HashSet<string> v3AreaNames = v3Areas
            .Select(static a => a.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> templateIds = [];
        foreach (OspfInterfaceTemplateRow template in inventory.Templates)
        {
            if (template.Area is null || !v3AreaNames.Contains(template.Area))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(template.Id))
            {
                return new Result
                {
                    Succeeded = false,
                    FailureDetail =
                        $"OSPFv3-dependent interface-template for area '{template.Area}' is missing .id.",
                };
            }

            templateIds.Add(template.Id!);
        }

        List<string> rangeIds = [];
        foreach (OspfAreaRangeRow range in inventory.AreaRanges)
        {
            if (range.Area is null || !v3AreaNames.Contains(range.Area))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(range.Id))
            {
                return new Result
                {
                    Succeeded = false,
                    FailureDetail =
                        $"OSPFv3-dependent area-range for area '{range.Area}' is missing .id.",
                };
            }

            rangeIds.Add(range.Id!);
        }

        List<string> areaIds = v3Areas.Select(static a => a.Id!).ToList();
        List<string> instanceIds = [];
        foreach (OspfInstanceRow instance in v3Instances)
        {
            if (string.IsNullOrWhiteSpace(instance.Id))
            {
                return new Result
                {
                    Succeeded = false,
                    FailureDetail =
                        $"OSPFv3 instance '{instance.Name}' is missing .id; refusing removal.",
                };
            }

            instanceIds.Add(instance.Id!);
        }

        return new Result
        {
            Succeeded = true,
            TemplateIds = templateIds,
            RangeIds = rangeIds,
            AreaIds = areaIds,
            InstanceIds = instanceIds,
        };
    }
}
