// -----------------------------------------------------------------------------
// File: LiveMikrotikTestGate.cs
//
// Description:
// Environment/config gate that skips or enables tests requiring a real MikroTik device.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Tests.Infrastructure.Helpers;
/// <summary>
/// Structural opt-in gate for tests that may open a live MikroTik API/SSH session.
/// Call before any TCP probe or <c>OpenConnection</c> so an incorrectly scoped non-live run
/// fails closed without contacting a router.
/// </summary>
/// <remarks>Gates live device tests behind explicit opt-in.</remarks>
internal static class LiveMikrotikTestGate
{
    /// <summary>Environment variable that opts in to read-only live MikroTik tests.</summary>
    public const string AllowLiveMikrotikEnv = "ALLOW_LIVE_MIKROTIK";

    /// <summary>Environment variable that opts in to live tests that perform a real router upgrade.</summary>
    public const string AllowLiveUpgradeEnv = "ALLOW_LIVE_UPGRADE";

    /// <summary>Environment variable that opts in to the Tommy OSPF apply-only live harness.</summary>
    public const string AllowTommyOspfApplyOnlyEnv = "TOMMY_OSPF_APPLY_ONLY";

    /// <summary>Determines whether an opt-in environment variable is set to exactly <c>1</c>.</summary>
    /// <param name="environmentVariableName">Name of the environment variable to inspect.</param>
    /// <returns><see langword="true"/> when the variable equals <c>1</c> (ordinal); otherwise <see langword="false"/>.</returns>
    public static bool IsArmed(string environmentVariableName) =>
        string.Equals(
            Environment.GetEnvironmentVariable(environmentVariableName),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Hard-fail (not pass) when the opt-in env var is unset, before any network I/O.
    /// </summary>
    /// <param name="environmentVariableName">Opt-in environment variable that must be <c>1</c>.</param>
    /// <param name="categoryLabel">Test category name included in the failure message.</param>
    public static void RequireArmed(string environmentVariableName, string categoryLabel)
    {
        Assert.True(
            IsArmed(environmentVariableName),
            $"Live MikroTik test blocked before any router contact. "
            + $"Set {environmentVariableName}=1 to opt in to Category={categoryLabel}.");
    }
}
