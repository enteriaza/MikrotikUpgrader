using System.Diagnostics;
using System.Security.Cryptography;

namespace MikrotikWorker.Tests;

public sealed class FirmwareDeploymentTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] SampleFirmware = "deployed-firmware-bytes"u8.ToArray();

    [Fact]
    public void FirmwareFiles_AreCopiedToBuildOutput()
    {
        string sourceRoot = RepoPaths.GetSourceFirmwareRoot();
        string outputRoot = Path.Combine(RepoPaths.GetWorkerBuildOutputDirectory(), "FirmwareFiles");

        Assert.True(Directory.Exists(sourceRoot), $"Source firmware directory missing: {sourceRoot}");
        Assert.True(Directory.Exists(outputRoot), $"Build output firmware directory missing: {outputRoot}");

        AssertFirmwareTreeCopied(sourceRoot, outputRoot);
    }

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

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static void RunDotnetPublish(string projectPath, string outputDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            Arguments = $"publish \"{projectPath}\" -c Release -o \"{outputDirectory}\"",
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
