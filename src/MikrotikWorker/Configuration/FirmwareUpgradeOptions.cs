namespace MikrotikWorker.Configuration;

/// <summary>
/// Automated RouterOS v6 → target upgrade settings.
/// </summary>
public sealed class FirmwareUpgradeOptions
{
    public const string SectionName = "FirmwareUpgrade";

    /// <summary>
    /// When false, jobs perform assessment only and never fetch/install/reboot.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Target RouterOS v6 release. Default <c>6.49.23</c>.
    /// </summary>
    public string TargetRouterOsVersion { get; set; } = "6.49.23";

    /// <summary>
    /// HTTP base URL reachable from routers, including the <c>/firmware_files</c> prefix.
    /// Example: <c>http://198.18.0.1:8080/firmware_files</c>. Must not be localhost.
    /// </summary>
    public string AdvertisedBaseUrl { get; set; } = string.Empty;

    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan RebootTimeout { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan PostUpgradeVerifyTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan BackupFileReadyTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan BackupFileReadyPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Optional password for <c>/system/backup/save</c>. Never reuse the RouterOS login password.
    /// When empty, binary backups are unencrypted on-router and protected only by central storage ACLs.
    /// </summary>
    public string BackupPassword { get; set; } = string.Empty;

    /// <summary>
    /// Relative or absolute backup root. Default <c>Backups</c> under the application base directory.
    /// </summary>
    public string BackupDirectory { get; set; } = "Backups";

    /// <summary>
    /// SSH/SCP port used to retrieve <c>.rsc</c> / <c>.backup</c> files (RouterOS API cannot transfer binary intact).
    /// </summary>
    public int ScpPort { get; set; } = 22;

    public TimeSpan ScpTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public string GetAbsoluteBackupDirectory(string? baseDirectory = null)
    {
        string root = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        return Path.IsPathRooted(BackupDirectory)
            ? Path.GetFullPath(BackupDirectory)
            : Path.GetFullPath(Path.Combine(root, BackupDirectory));
    }
}
