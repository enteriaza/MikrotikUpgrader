// -----------------------------------------------------------------------------
// File: FirmwareDeploymentTests.cs
//
// Description:
// Tests firmware directory deployment/serving assumptions used by advertised base URL
// fetch workflows.
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Security.Cryptography;

namespace MikrotikWorker.Tests.Hosting;
/// <summary>
/// Covers firmware directory deployment/serving assumptions.
/// </summary>
public sealed class FirmwareDeploymentTests
{
    /// <summary>Upper bound for each host start/stop and HTTP wait in these tests.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Sample firmware payload served and compared byte-for-byte.</summary>
    private static readonly byte[] SampleFirmware = "deployed-firmware-bytes"u8.ToArray();

    /// <summary>
    /// Compares repo source FirmwareFiles to worker build output FirmwareFiles.
    /// Expects every source file present with matching SHA-256.
    /// </summary>
    [Fact]
    public void FirmwareFiles_AreCopiedToBuildOutput()
    {
        string sourceRoot = RepoPaths.GetSourceFirmwareRoot();
        string outputRoot = Path.Combine(RepoPaths.GetWorkerBuildOutputDirectory(), "FirmwareFiles");

        Assert.True(Directory.Exists(sourceRoot), $"Source firmware directory missing: {sourceRoot}");
        Assert.True(Directory.Exists(outputRoot), $"Build output firmware directory missing: {outputRoot}");

        AssertFirmwareTreeCopied(sourceRoot, outputRoot);
    }

    /// <summary>
    /// dotnet publish of the worker project to a temp directory.
    /// Expects FirmwareFiles tree copied with matching hashes.
    /// </summary>
    [Fact]
    public void FirmwareFiles_AreIncludedInPublishOutput()
    {
        string sourceRoot = RepoPaths.GetSourceFirmwareRoot();
        string publishRoot = Directory.CreateTempSubdirectory("mikrotik-publish-").FullName;

        try
        {
            RunDotnetPublish(RepoPaths.GetWorkerProjectPath(), publishRoot);

            string publishedFirmwareRoot = Path.Combine(publishRoot, "FirmwareFiles");
            Assert.True(
                Directory.Exists(publishedFirmwareRoot),
                $"Publish output firmware directory missing: {publishedFirmwareRoot}");

            AssertFirmwareTreeCopied(sourceRoot, publishedFirmwareRoot);
        }
        finally
        {
            Directory.Delete(publishRoot, recursive: true);
        }
    }

    /// <summary>
    /// Build-output FirmwareFiles tree.
    /// Expects nested mikrotik/ paths preserved (not flattened).
    /// </summary>
    [Fact]
    public void NestedDirectoryStructures_ArePreservedInBuildOutput()
    {
        string outputRoot = Path.Combine(RepoPaths.GetWorkerBuildOutputDirectory(), "FirmwareFiles");
        string[] nestedFiles = Directory.GetFiles(outputRoot, "*", SearchOption.AllDirectories);

        Assert.NotEmpty(nestedFiles);
        Assert.Contains(
            nestedFiles,
            static path => path.Contains($"{Path.DirectorySeparatorChar}mikrotik{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/mikrotik/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Writes sample.npk under AppContext FirmwareFiles; CWD moved to temp; relative firmware root.
    /// Expects HTTP download matches bytes and options resolve against BaseDirectory.
    /// </summary>
    [Fact]
    public async Task HttpRequests_RetrieveFilesFromDeployedFirmwareDirectory()
    {
        string deployedRoot = Path.Combine(AppContext.BaseDirectory, "FirmwareFiles");
        string relativeFile = Path.Combine("mikrotik", "deployed-sample.npk");
        string deployedFile = Path.Combine(deployedRoot, relativeFile);
        Directory.CreateDirectory(Path.GetDirectoryName(deployedFile)!);
        await File.WriteAllBytesAsync(deployedFile, SampleFirmware);

        string originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Path.GetTempPath());

            await using WebApplication app = TestHostFactory.CreateApplication(
                firmwareRoot: "FirmwareFiles",
                port: TestHostFactory.GetFreeTcpPort(),
                heartbeatInterval: TimeSpan.FromMilliseconds(50));

            await app.StartAsync().WaitAsync(TestTimeout);

            try
            {
                using HttpClient client = new()
                {
                    BaseAddress = TestHostFactory.GetListeningAddress(app),
                    Timeout = TestTimeout,
                };

                byte[] downloaded = await client.GetByteArrayAsync("/firmware_files/mikrotik/deployed-sample.npk");
                Assert.Equal(SampleFirmware, downloaded);

                Assert.Equal(
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FirmwareFiles")),
                    app.Services.GetRequiredService<IOptions<FirmwareFilesOptions>>().Value.GetAbsolutePath());
            }
            finally
            {
                await app.StopAsync().WaitAsync(TestTimeout);
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            if (File.Exists(deployedFile))
            {
                File.Delete(deployedFile);
            }
        }
    }

    /// <summary>
    /// Asserts every source file exists at the destination with identical SHA-256.
    /// </summary>
    private static void AssertFirmwareTreeCopied(string sourceRoot, string destinationRoot)
    {
        string[] sourceFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(sourceFiles);

        foreach (string sourceFile in sourceFiles)
        {
            string relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            string destinationFile = Path.Combine(destinationRoot, relativePath);

            Assert.True(File.Exists(destinationFile), $"Missing copied firmware file: {relativePath}");
            Assert.Equal(ComputeSha256(sourceFile), ComputeSha256(destinationFile));
        }
    }

    /// <summary>
    /// Hex-encoded SHA-256 of a file's contents.
    /// </summary>
    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Runs dotnet publish -c Release and asserts exit code 0.
    /// </summary>
    private static void RunDotnetPublish(string projectPath, string outputDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            Arguments = $"publish \"{projectPath}\" -c Release -o \"{outputDirectory}\" -p:NoWarn=NU1507",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet publish.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"dotnet publish failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }
}
