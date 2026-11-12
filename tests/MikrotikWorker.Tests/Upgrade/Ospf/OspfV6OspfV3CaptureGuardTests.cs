using System.Reflection;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Guardrail: only positively identified absent/unsupported OSPFv3 menus are empty;
/// permission, timeout, and generic API failures must fail closed.
/// </summary>
public sealed class OspfV6OspfV3CaptureGuardTests
{
    [Theory]
    [InlineData("no such command prefix")]
    [InlineData("unknown command")]
    [InlineData("bad command name")]
    [InlineData("failure: no such command 'ospf-v3'")]
    public void AbsentMenuMessages_AreRecognised(string message)
    {
        Assert.True(InvokeIsAbsent(new InvalidOperationException(message)));
    }

    [Theory]
    [InlineData("not enough permissions (9)")]
    [InlineData("cannot open file")]
    [InlineData("timeout")]
    [InlineData("connection reset")]
    [InlineData("no such item")]
    [InlineData("failure: interrupted")]
    public void CaptureFailures_AreNotTreatedAsAbsent(string message)
    {
        Assert.False(InvokeIsAbsent(new InvalidOperationException(message)));
    }

    private static bool InvokeIsAbsent(Exception ex)
    {
        MethodInfo method = typeof(OspfMikrotikOperations).GetMethod(
            "IsAbsentMenuException",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsAbsentMenuException not found.");
        return (bool)method.Invoke(null, [ex])!;
    }
}
