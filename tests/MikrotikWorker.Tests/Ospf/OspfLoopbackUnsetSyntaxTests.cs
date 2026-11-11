using System.Reflection;
using MikrotikWorker.Upgrade.Ospf;
using tik4net;

namespace MikrotikWorker.Tests.Ospf;

/// <summary>
/// Documents the supported tik4net/RouterOS API syntax used to clear interface-template type:
/// <c>/routing/ospf/interface-template/unset</c> with <see cref="TikSpecialProperties.UnsetValueName"/> (=value-name=type).
/// </summary>
public sealed class OspfLoopbackUnsetSyntaxTests
{
    [Fact]
    public void Tik4net_ExposesUnsetValueName_AsValueName()
    {
        Assert.Equal("value-name", TikSpecialProperties.UnsetValueName);
    }

    [Fact]
    public void Operations_DeclareUnsetHelper_ForAreaAndTemplatePaths()
    {
        MethodInfo? unset = typeof(OspfMikrotikOperations).GetMethod(
            "UnsetPropertyAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(unset);

        // Ensure loopback reconciliation path exists as a dedicated method (not type=null only).
        MethodInfo? loopback = typeof(OspfMikrotikOperations).GetMethod(nameof(OspfMikrotikOperations.EnsureLoopbackTemplateAsync));
        Assert.NotNull(loopback);
    }
}
