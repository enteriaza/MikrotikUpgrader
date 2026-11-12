namespace MikrotikWorker.Tests.Infrastructure.Helpers;
/// <summary>
/// Structural opt-in gate for tests that may open a live MikroTik API/SSH session.
/// Call before any TCP probe or <c>OpenConnection</c> so an incorrectly scoped non-live run
/// fails closed without contacting a router.
/// </summary>
internal static class LiveMikrotikTestGate
{
    public const string AllowLiveMikrotikEnv = "ALLOW_LIVE_MIKROTIK";
    public const string AllowLiveUpgradeEnv = "ALLOW_LIVE_UPGRADE";
    public const string AllowTommyOspfApplyOnlyEnv = "TOMMY_OSPF_APPLY_ONLY";

    public static bool IsArmed(string environmentVariableName) =>
        string.Equals(
            Environment.GetEnvironmentVariable(environmentVariableName),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Hard-fail (not pass) when the opt-in env var is unset, before any network I/O.
    /// </summary>
    public static void RequireArmed(string environmentVariableName, string categoryLabel)
    {
        Assert.True(
            IsArmed(environmentVariableName),
            $"Live MikroTik test blocked before any router contact. "
            + $"Set {environmentVariableName}=1 to opt in to Category={categoryLabel}.");
    }
}
