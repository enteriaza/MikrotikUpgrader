// -----------------------------------------------------------------------------
// File: SerilogOptions.cs
//
// Description:
// Options bound from "Logging:Serilog" for the central application log file,
// async file sink behaviour, and optional per-device log mirroring under Logs/Devices.
// Relative paths resolve from AppContext.BaseDirectory.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Configuration;

/// <summary>
/// Central application Serilog settings. Relative paths resolve from <see cref="AppContext.BaseDirectory"/>.
/// </summary>
public sealed class SerilogOptions
{
    /// <summary>Configuration section name: <c>Logging:Serilog</c>.</summary>
    public const string SectionName = "Logging:Serilog";

    /// <summary>
    /// Directory for the rolling application log file (relative to the application base directory by default).
    /// Default: <c>Logs</c>.
    /// </summary>
    public string LogDirectory { get; set; } = "Logs";

    /// <summary>
    /// Rolling file name pattern. The date token is inserted by Serilog before the extension.
    /// Default: <c>application-.log</c>.
    /// </summary>
    public string FileName { get; set; } = "application-.log";

    /// <summary>
    /// Rolling interval name: <c>Day</c>, <c>Hour</c>, <c>Minute</c>, <c>Month</c>, <c>Year</c>, or <c>Infinite</c>.
    /// Default: <c>Day</c>.
    /// </summary>
    public string RollingInterval { get; set; } = "Day";

    /// <summary>
    /// Number of rolled files to retain.
    /// Default: 30.
    /// </summary>
    public int RetainedFileCountLimit { get; set; } = 30;

    /// <summary>
    /// Maximum size of a single log file before size-based rolling (when enabled).
    /// Default: 104_857_600 (100 MiB).
    /// </summary>
    public long FileSizeLimitBytes { get; set; } = 104_857_600;

    /// <summary>
    /// When true, roll the file when <see cref="FileSizeLimitBytes"/> is reached in addition to interval rolling.
    /// Default: <c>true</c>.
    /// </summary>
    public bool RollOnFileSizeLimit { get; set; } = true;

    /// <summary>
    /// Minimum Serilog level name for the configured sinks.
    /// Default: <c>Information</c>.
    /// </summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>
    /// When true, write structured events to the console sink.
    /// Default: <c>true</c>.
    /// </summary>
    public bool WriteToConsole { get; set; } = true;

    /// <summary>
    /// When true, write structured events to the rolling file sink.
    /// Default: <c>true</c>.
    /// </summary>
    public bool WriteToFile { get; set; } = true;

    /// <summary>
    /// Bounded queue capacity for the asynchronous file sink wrapper.
    /// Default: 10_000.
    /// </summary>
    public int AsyncBufferCapacity { get; set; } = 10_000;

    /// <summary>
    /// When the async file buffer is full, block the logging thread briefly instead of dropping events.
    /// Default: <c>true</c>.
    /// </summary>
    public bool AsyncBlockWhenFull { get; set; } = true;

    /// <summary>
    /// When true, events carrying a device log key are mirrored to per-device files under <see cref="DeviceLogDirectory"/>.
    /// Default: <c>true</c>.
    /// </summary>
    public bool DeviceLogEnabled { get; set; } = true;

    /// <summary>
    /// Directory for per-device logs, relative to <see cref="LogDirectory"/> when not rooted.
    /// Default: <c>Devices</c>.
    /// </summary>
    public string DeviceLogDirectory { get; set; } = "Devices";

    /// <summary>
    /// Bounded queue capacity for the asynchronous per-device log writer.
    /// Default: 10_000.
    /// </summary>
    public int DeviceLogAsyncCapacity { get; set; } = 10_000;

    /// <summary>
    /// When the device-log queue is full, block briefly (and fall back to synchronous append) instead of dropping.
    /// Default: <c>true</c>.
    /// </summary>
    public bool DeviceLogAsyncBlockWhenFull { get; set; } = true;

    /// <summary>
    /// Resolves <see cref="LogDirectory"/> against <paramref name="baseDirectory"/>,
    /// defaulting to <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public string GetAbsoluteLogDirectory(string? baseDirectory = null)
    {
        string root = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        return Path.IsPathRooted(LogDirectory)
            ? Path.GetFullPath(LogDirectory)
            : Path.GetFullPath(Path.Combine(root, LogDirectory));
    }

    /// <summary>
    /// Combines <see cref="GetAbsoluteLogDirectory"/> with <see cref="FileName"/>.
    /// </summary>
    public string GetAbsoluteLogFilePath(string? baseDirectory = null) =>
        Path.Combine(GetAbsoluteLogDirectory(baseDirectory), FileName);
}
