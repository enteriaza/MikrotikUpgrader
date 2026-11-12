// -----------------------------------------------------------------------------
// File: FirmwarePathTests.cs
//
// Description:
// Tests FirmwareFilesOptions path resolution (relative vs absolute) and RequestPath
// constant used by the static firmware middleware.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Tests.Configuration;
/// <summary>
/// Validates FirmwareFilesOptions path helpers and request path constant.
/// </summary>
public sealed class FirmwarePathTests
{
    /// <summary>
    /// Relative Path=FirmwareFiles with default GetAbsolutePath.
    /// Expects full path under AppContext.BaseDirectory, not an arbitrary CWD.
    /// </summary>
    [Fact]
    public void RelativeFirmwarePath_ResolvesAgainstAppContextBaseDirectory()
    {
        FirmwareFilesOptions options = new() { Path = "FirmwareFiles" };

        string resolved = options.GetAbsolutePath();

        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FirmwareFiles")),
            resolved);
    }

    /// <summary>
    /// Absolute Path with an ignored baseDirectory argument.
    /// Expects the absolute path returned unchanged.
    /// </summary>
    [Fact]
    public void AbsoluteFirmwarePath_IsPreserved()
    {
        string absolutePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "absolute-firmware", Guid.NewGuid().ToString("N")));
        FirmwareFilesOptions options = new() { Path = absolutePath };

        string resolved = options.GetAbsolutePath(baseDirectory: Path.Combine(Path.GetTempPath(), "ignored-base"));

        Assert.Equal(absolutePath, resolved);
    }

    /// <summary>
    /// Process CWD moved to temp while baseDirectory is a simulated app base.
    /// Expects resolution against the simulated base, not Directory.GetCurrentDirectory().
    /// </summary>
    [Fact]
    public void ChangingProcessWorkingDirectory_DoesNotChangeFirmwarePathResolution()
    {
        string simulatedBase = Path.Combine(Path.GetTempPath(), "mikrotik-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(simulatedBase);
        string originalDirectory = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(Path.GetTempPath());

            FirmwareFilesOptions options = new() { Path = "FirmwareFiles" };
            string resolved = options.GetAbsolutePath(simulatedBase);
            string workingDirectoryResolved = Path.GetFullPath(
                Path.Combine(Directory.GetCurrentDirectory(), "FirmwareFiles"));

            Assert.Equal(
                Path.GetFullPath(Path.Combine(simulatedBase, "FirmwareFiles")),
                resolved);
            Assert.NotEqual(workingDirectoryResolved, resolved);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(simulatedBase, recursive: true);
        }
    }

    /// <summary>
    /// TestHostFactory.CreateApplication pointed at a non-existent firmware root.
    /// Expects OptionsValidationException mentioning Firmware directory does not exist.
    /// </summary>
    [Fact]
    public void MissingFirmwareDirectory_PreventsStartup()
    {
        string missingDirectory = Path.Combine(Path.GetTempPath(), "missing-firmware-" + Guid.NewGuid().ToString("N"));

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => TestHostFactory.CreateApplication(
                missingDirectory,
                TestHostFactory.GetFreeTcpPort()));

        Assert.Contains("Firmware directory does not exist", exception.Message, StringComparison.Ordinal);
    }
}
