// -----------------------------------------------------------------------------
// File: RepoPaths.cs
//
// Description:
// Resolves repository-relative paths for test fixtures and firmware sample trees.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Tests.Infrastructure.Helpers;
/// <summary>
/// Resolves repo-relative paths for tests.
/// </summary>
internal static class RepoPaths
{
    /// <summary>Walks up from the test base directory to find the folder containing <c>MikrotikUpgrader.sln</c>.</summary>
    /// <returns>Full path of the repository root.</returns>
    /// <exception cref="InvalidOperationException">When no solution file is found in any ancestor.</exception>
    public static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MikrotikUpgrader.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root from the test base directory.");
    }

    /// <summary>
    /// Path of <c>MikrotikWorker.csproj</c> under <c>src/MikrotikWorker</c>, resolved from the repository root
    /// located by <see cref="FindRepositoryRoot"/>.
    /// </summary>
    /// <returns>Full path of <c>MikrotikWorker.csproj</c>.</returns>
    public static string GetWorkerProjectPath() =>
        Path.Combine(FindRepositoryRoot(), "src", "MikrotikWorker", "MikrotikWorker.csproj");

    /// <summary>
    /// Checked-in <c>FirmwareFiles</c> directory at the repository root: the source-tree copy, not a copy under a
    /// build output folder.
    /// </summary>
    /// <returns>Full path of the source firmware root.</returns>
    public static string GetSourceFirmwareRoot() =>
        Path.Combine(FindRepositoryRoot(), "FirmwareFiles");

    /// <summary>
    /// Build output directory of the worker project
    /// (<c>src/MikrotikWorker/bin/{configuration}/{targetFramework}</c>), derived from the test run's own base
    /// directory so it matches whichever configuration and target framework is under test.
    /// </summary>
    /// <returns>Full path of <c>src/MikrotikWorker/bin/{configuration}/{targetFramework}</c>.</returns>
    /// <exception cref="InvalidOperationException">When the build configuration cannot be derived from the test base directory.</exception>
    public static string GetWorkerBuildOutputDirectory()
    {
        DirectoryInfo baseDirectory = new(AppContext.BaseDirectory);
        string targetFramework = baseDirectory.Name;
        string configuration = baseDirectory.Parent?.Name
            ?? throw new InvalidOperationException("Unable to determine build configuration.");

        return Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MikrotikWorker",
            "bin",
            configuration,
            targetFramework);
    }
}
