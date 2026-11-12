// -----------------------------------------------------------------------------
// File: OspfV6OspfV3CaptureGuardTests.cs
//
// Description:
// Guardrail: only positively identified absent/unsupported OSPFv3 menus are
// empty; permission, timeout, and generic API failures must fail closed.
//
// -----------------------------------------------------------------------------

using System.Reflection;
using MikrotikWorker.Upgrade.Ospf;

namespace MikrotikWorker.Tests.Upgrade.Ospf;
/// <summary>
/// Guardrail: only positively identified absent/unsupported OSPFv3 menus are empty;
/// permission, timeout, and generic API failures must fail closed.
/// </summary>
public sealed class OspfV6OspfV3CaptureGuardTests
{
    /// <summary>
    /// Scenario: router error messages that mean the ospf-v3 menu does not exist (no such command prefix, unknown command, bad command name, failure: no such command).
    /// Expected: IsAbsentMenuException returns true for each.
    /// Invariant: RouterOS 6 devices without the v3 menu are treated as having nothing to capture.
    /// </summary>
    [Theory]
    [InlineData("no such command prefix")]
    [InlineData("unknown command")]
    [InlineData("bad command name")]
    [InlineData("failure: no such command 'ospf-v3'")]
    public void AbsentMenuMessages_AreRecognised(string message)
    {
        Assert.True(InvokeIsAbsent(new InvalidOperationException(message)));
    }

    /// <summary>
    /// Scenario: genuine capture failures such as permission errors, timeouts, connection resets, 'no such item' and interrupted commands.
    /// Expected: IsAbsentMenuException returns false for each.
    /// Invariant: a real capture failure must surface and never be mistaken for an absent OSPFv3 menu.
    /// </summary>
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

    /// <summary>
    /// Uses reflection to call the private static <c>IsAbsentMenuException</c> on <c>OspfMikrotikOperations</c> and
    /// returns its result.
    /// </summary>
    private static bool InvokeIsAbsent(Exception ex)
    {
        MethodInfo method = typeof(OspfMikrotikOperations).GetMethod(
            "IsAbsentMenuException",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsAbsentMenuException not found.");
        return (bool)method.Invoke(null, [ex])!;
    }
}
