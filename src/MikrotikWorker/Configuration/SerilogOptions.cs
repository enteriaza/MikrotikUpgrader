namespace MikrotikWorker.Configuration;

/// <summary>
/// Central application Serilog settings. Relative paths resolve from <see cref="AppContext.BaseDirectory"/>.
/// </summary>
public sealed class SerilogOptions
{
    public const string SectionName = "Logging:Serilog";

    /// <summary>
    /// Directory for the rolling application log file (relative to the application base directory by default).
    /// </summary>
    public string LogDirectory { get; set; } = "Logs";

    /// <summary>
    /// Rolling file name pattern. The date token is inserted by Serilog before the extension.
    /// </summary>
    public string FileName { get; set; } = "application-.log";

    /// <summary>
    /// Rolling interval name: <c>Day</c>, <c>Hour</c>, <c>Minute</c>, <c>Month</c>, <c>Year</c>, or <c>Infinite</c>.
    /// </summary>
    public string RollingInterval { get; set; } = "Day";

    public int RetainedFileCountLimit { get; set; } = 30;

    public long FileSizeLimitBytes { get; set; } = 104_857_600;

    public bool RollOnFileSizeLimit { get; set; } = true;

    public string MinimumLevel { get; set; } = "Information";

    public bool WriteToConsole { get; set; } = true;

    public bool WriteToFile { get; set; } = true;

    /// <summary>
    /// Bounded queue capacity for the asynchronous file sink wrapper.
    /// </summary>
    public int AsyncBufferCapacity { get; set; } = 10_000;

    /// <summary>
    /// When the async file buffer is full, block the logging thread briefly instead of dropping events.
    /// </summary>
    public bool AsyncBlockWhenFull { get; set; } = true;

    /// <summary>
    /// When true, events carrying a device log key are mirrored to per-device files under <see cref="DeviceLogDirectory"/>.
    /// </summary>
    public bool DeviceLogEnabled { get; set; } = true;

    /// <summary>
    /// Directory for per-device logs, relative to <see cref="LogDirectory"/> when not rooted.
    /// </summary>
    public string DeviceLogDirectory { get; set; } = "Devices";

    /// <summary>
    /// Bounded queue capacity for the asynchronous per-device log writer.
    /// </summary>
    public int DeviceLogAsyncCapacity { get; set; } = 10_000;

    /// <summary>
    /// When the device-log queue is full, block briefly (and fall back to synchronous append) instead of dropping.
    /// </summary>
    public bool DeviceLogAsyncBlockWhenFull { get; set; } = true;

    public string GetAbsoluteLogDirectory(string? baseDirectory = null)
    {
        string root = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        return Path.IsPathRooted(LogDirectory)
            ? Path.GetFullPath(LogDirectory)
            : Path.GetFullPath(Path.Combine(root, LogDirectory));
    }

    public string GetAbsoluteLogFilePath(string? baseDirectory = null) =>
        Path.Combine(GetAbsoluteLogDirectory(baseDirectory), FileName);
}
