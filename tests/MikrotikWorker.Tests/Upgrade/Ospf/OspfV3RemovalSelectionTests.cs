// -----------------------------------------------------------------------------
// File: OspfV3RemovalSelectionTests.cs
//
// Description:
// Unit tests for OspfV3RemovalSelection instance-safe OSPFv3 cleanup
// targeting.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Unit tests for instance-safe OSPFv3 removal selection.
/// </summary>
public sealed class OspfV3RemovalSelectionTests
{
    /// <summary>
    /// Scenario: an inventory where the OSPFv2 and OSPFv3 instances each own an area named 'backbone'.
    /// Expected: Build fails with a detail saying the name 'also exists on an OSPFv2' object, and selects no template, area or instance ids.
    /// Invariant: an ambiguous area name must never lead to v2 objects being scheduled for removal.
    /// </summary>
    [Fact]
    public void CollidingAreaNames_FailClosed_WithoutSelectingV2Objects()
    {
        OspfV7Inventory inventory = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*v2", Name = "default-v2", Version = "2" },
                new OspfInstanceRow { Id = "*v3", Name = "default-v3", Version = "3" },
            ],
            Areas =
            [
                new OspfAreaRow { Id = "*a2", Name = "backbone", AreaId = "0.0.0.0", Instance = "default-v2" },
                new OspfAreaRow { Id = "*a3", Name = "backbone", AreaId = "0.0.0.0", Instance = "default-v3" },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow { Id = "*t2", Area = "backbone", Interfaces = "ether1", Networks = "10.0.0.0/30", Type = "ptp" },
                new OspfInterfaceTemplateRow { Id = "*t3", Area = "backbone", Networks = "2001:db8::/64" },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow { Id = "*r2", Area = "backbone", Range = "10.0.0.0/8" },
            ],
        };

        OspfV3RemovalSelection.Result result = OspfV3RemovalSelection.Build(inventory);
        Assert.False(result.Succeeded);
        Assert.Contains("also exists on an OSPFv2", result.FailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.TemplateIds);
        Assert.Empty(result.AreaIds);
        Assert.Empty(result.InstanceIds);
    }

    /// <summary>
    /// Scenario: v2 and v3 instances with distinct area names, each with its own templates and ranges.
    /// Expected: Build succeeds and returns only the v3 ids (*t3, *r3, *a3, *v3); *t2, *a2 and *v2 are not selected.
    /// Invariant: removal targets are chosen by .id and only for objects that depend on the v3 instance.
    /// </summary>
    [Fact]
    public void DistinctAreaNames_SelectOnlyV3DependentsById()
    {
        OspfV7Inventory inventory = new()
        {
            Instances =
            [
                new OspfInstanceRow { Id = "*v2", Name = "default-v2", Version = "2" },
                new OspfInstanceRow { Id = "*v3", Name = "default-v3", Version = "3" },
            ],
            Areas =
            [
                new OspfAreaRow { Id = "*a2", Name = "backbone", AreaId = "0.0.0.0", Instance = "default-v2" },
                new OspfAreaRow { Id = "*a3", Name = "backbone-v3", AreaId = "0.0.0.0", Instance = "default-v3" },
            ],
            Templates =
            [
                new OspfInterfaceTemplateRow { Id = "*t2", Area = "backbone", Interfaces = "ether1", Networks = "10.0.0.0/30", Type = "ptp" },
                new OspfInterfaceTemplateRow { Id = "*t3", Area = "backbone-v3", Networks = "2001:db8::/64" },
            ],
            AreaRanges =
            [
                new OspfAreaRangeRow { Id = "*r2", Area = "backbone", Range = "10.0.0.0/8" },
                new OspfAreaRangeRow { Id = "*r3", Area = "backbone-v3", Range = "2001:db8::/32" },
            ],
        };

        OspfV3RemovalSelection.Result result = OspfV3RemovalSelection.Build(inventory);
        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal(["*t3"], result.TemplateIds);
        Assert.Equal(["*r3"], result.RangeIds);
        Assert.Equal(["*a3"], result.AreaIds);
        Assert.Equal(["*v3"], result.InstanceIds);
        Assert.DoesNotContain("*t2", result.TemplateIds);
        Assert.DoesNotContain("*a2", result.AreaIds);
        Assert.DoesNotContain("*v2", result.InstanceIds);
    }

    /// <summary>
    /// Scenario: v3 instance and area rows carry no .id values.
    /// Expected: Build fails and the failure detail mentions '.id'.
    /// Invariant: without stable ids the selector refuses to build a removal set.
    /// </summary>
    [Fact]
    public void MissingIds_FailClosed()
    {
        OspfV7Inventory inventory = new()
        {
            Instances = [new OspfInstanceRow { Name = "default-v3", Version = "3" }],
            Areas = [new OspfAreaRow { Name = "backbone-v3", AreaId = "0.0.0.0", Instance = "default-v3" }],
        };

        OspfV3RemovalSelection.Result result = OspfV3RemovalSelection.Build(inventory);
        Assert.False(result.Succeeded);
        Assert.Contains(".id", result.FailureDetail, StringComparison.OrdinalIgnoreCase);
    }
}
