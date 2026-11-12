namespace MikrotikWorker.Tests.Infrastructure.Helpers;
internal static class RepoPaths
{
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

    public static string GetWorkerProjectPath() =>
        Path.Combine(FindRepositoryRoot(), "src", "MikrotikWorker", "MikrotikWorker.csproj");

    public static string GetSourceFirmwareRoot() =>
        Path.Combine(FindRepositoryRoot(), "FirmwareFiles");

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
