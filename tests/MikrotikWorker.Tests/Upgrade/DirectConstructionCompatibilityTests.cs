// -----------------------------------------------------------------------------
// File: DirectConstructionCompatibilityTests.cs
//
// Description:
// Confirms intentional direct construction of CatalogueCoverageReporter and
// ReadOnlyTikCommandExecutor after unused interface/DI removals.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;
using MikrotikWorker.Tests.Infrastructure.Hosts;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;

/// <summary>
/// Compatibility: audit helpers that are no longer DI-injected remain constructible and usable.
/// </summary>
public sealed class DirectConstructionCompatibilityTests
{
    /// <summary>
    /// Fixture/live report paths construct <see cref="CatalogueCoverageReporter"/> with an
    /// <see cref="IFirmwarePackageCatalog"/>; coverage rows are produced without DI.
    /// </summary>
    [Fact]
    public void CatalogueCoverageReporter_DirectConstruction_BuildsCoverageRows()
    {
        string root = Directory.CreateTempSubdirectory("cov-direct-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "routeros-mipsbe-6.49.23.npk"), [1, 2, 3]);
            IFirmwarePackageCatalog catalog = new StaticFirmwarePackageCatalog(root);

            CatalogueCoverageReporter reporter = new(catalog);
            IReadOnlyList<CatalogueCoverageArchitectureRow> rows =
                reporter.BuildCoverage(RouterOsUpgradeVersions.V6Stepping, ["mipsbe"]);

            Assert.Single(rows);
            Assert.Equal("mipsbe", rows[0].Architecture);
            Assert.Equal("routeros-mipsbe-6.49.23.npk", rows[0].MainPackageFileName);
            Assert.Equal("MainOnly", rows[0].CatalogueStatus);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp catalogue.
            }
        }
    }

    /// <summary>
    /// Inventory collection constructs <see cref="ReadOnlyTikCommandExecutor"/> directly; the
    /// allow-list contract remains enforceable without an injectable interface.
    /// </summary>
    [Fact]
    public void ReadOnlyTikCommandExecutor_DirectConstruction_EnforcesAllowList()
    {
        ReadOnlyTikCommandExecutor executor = new();
        Assert.Empty(executor.CommandsIssued);
        Assert.Contains("/system/package/print", ReadOnlyTikCommandExecutor.AllowedCommands);
        Assert.Throws<InvalidOperationException>(() => ReadOnlyTikCommandExecutor.EnsureAllowed("/tool/fetch"));
        ReadOnlyTikCommandExecutor.EnsureAllowed("/system/identity/print");
    }
}
