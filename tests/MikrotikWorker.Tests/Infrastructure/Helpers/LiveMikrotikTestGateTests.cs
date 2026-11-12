// -----------------------------------------------------------------------------
// File: LiveMikrotikTestGateTests.cs
//
// Description:
// Unit tests for LiveMikrotikTestGate opt-in behaviour that protects live router tests.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Tests.Infrastructure.Helpers;
/// <summary>
/// Covers the live MikroTik test opt-in gate.
/// </summary>
public sealed class LiveMikrotikTestGateTests
{
    /// <summary>
    /// Clears ALLOW_LIVE_MIKROTIK.
    /// Expects RequireArmed throws an xUnit failure mentioning ALLOW_LIVE_MIKROTIK=1
    /// and that arming must happen before any router contact.
    /// </summary>
    [Fact]
    public void RequireArmed_FailsClosed_WhenEnvUnset()
    {
        string previous = Environment.GetEnvironmentVariable(LiveMikrotikTestGate.AllowLiveMikrotikEnv) ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable(LiveMikrotikTestGate.AllowLiveMikrotikEnv, null);
            Xunit.Sdk.XunitException ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                LiveMikrotikTestGate.RequireArmed(LiveMikrotikTestGate.AllowLiveMikrotikEnv, "Live"));
            Assert.Contains("ALLOW_LIVE_MIKROTIK=1", ex.Message, StringComparison.Ordinal);
            Assert.Contains("before any router contact", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                LiveMikrotikTestGate.AllowLiveMikrotikEnv,
                string.IsNullOrEmpty(previous) ? null : previous);
        }
    }

    /// <summary>
    /// Reads known live/OSP F harness sources from disk.
    /// Expects each file contains RequireArmed and that call appears before
    /// OpenConnection / IsTcpReachableAsync when those APIs are used.
    /// </summary>
    [Fact]
    public void LiveConnectingTests_DeclareOptInGateBeforeConnectionPatterns()
    {
        string testsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string[] files =
        [
            Path.Combine(testsRoot, "Upgrade", "Live", "LivePostUpgradeVerifierAuditTests.cs"),
            Path.Combine(testsRoot, "Upgrade", "Live", "LiveMikrotikPreflightTests.cs"),
            Path.Combine(testsRoot, "Upgrade", "Live", "LiveMikrotikInventoryAndBackupTests.cs"),
            Path.Combine(testsRoot, "Upgrade", "Live", "LiveMikrotikUpgradeTests.cs"),
            Path.Combine(testsRoot, "Upgrade", "Ospf", "TommyOspfApplyOnlyHarness.cs"),
            Path.Combine(testsRoot, "Upgrade", "Ospf", "TommyReadOnlyOspfAuditHarness.cs"),
            Path.Combine(testsRoot, "Upgrade", "Ospf", "TommyOspfRecoveryPreparationHarness.cs"),
        ];

        foreach (string file in files)
        {
            Assert.True(File.Exists(file), file);
            string text = File.ReadAllText(file);
            Assert.Contains("LiveMikrotikTestGate.RequireArmed", text, StringComparison.Ordinal);
            int gate = text.IndexOf("LiveMikrotikTestGate.RequireArmed", StringComparison.Ordinal);
            int open = text.IndexOf("OpenConnection", StringComparison.Ordinal);
            int tcp = text.IndexOf("IsTcpReachableAsync", StringComparison.Ordinal);
            if (open >= 0)
            {
                Assert.True(gate < open, $"{Path.GetFileName(file)}: gate must precede OpenConnection");
            }

            if (tcp >= 0)
            {
                Assert.True(gate < tcp, $"{Path.GetFileName(file)}: gate must precede TCP reachability");
            }
        }
    }
}
