// -----------------------------------------------------------------------------
// File: FirmwareUpgradeOptions.cs
//
// Description:
// Options bound from the "FirmwareUpgrade" section: administrative upgrade
// kill-switch, advertised firmware HTTP base URL, reboot/verify/backup/SCP
// timeouts, and the OSPF lab target pin. Stage version targets are not here —
// v6 stepping is a code constant; v7 comes from FirmwareFiles/mikrotik/version.
// FirmwareUpgrade:Enabled is independent of Mikrotik:Enabled.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Configuration;

/// <summary>
/// Automated RouterOS upgrade settings (timeouts, firmware URL, backups).
/// Stage targets are not configured here: v6 stepping is a code constant and the v7 target
/// comes from <c>FirmwareFiles/mikrotik/version</c>.
/// </summary>
/// <remarks>
/// <c>FirmwareUpgrade:Enabled</c> is independent of <c>Mikrotik:Enabled</c>.
/// When upgrade is disabled, eligible jobs still connect/assess but never fetch, install,
/// or reboot — each job is reported as Failed (administratively disabled), never as success.
/// </remarks>
public sealed class FirmwareUpgradeOptions
{
    /// <summary>Configuration section name: <c>FirmwareUpgrade</c>.</summary>
    public const string SectionName = "FirmwareUpgrade";

    /// <summary>
    /// Administrative kill-switch. <c>appsettings.json</c> ships with <c>true</c> so eligible devices are
    /// upgraded automatically. When false, jobs never fetch/install/reboot and each job is reported as
    /// <c>Failed</c> (upgrade administratively disabled) — never as a success.
    /// Independent of <c>Mikrotik:Enabled</c>. Defaults to <c>false</c> when omitted from binding.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Explicit lab target pin for OSPF-only recovery tooling (exact IP + exact identity).
    /// Empty by default; unused by the normal upgrade executor.
    /// </summary>
    public OspfLabTargetOptions OspfLabTarget { get; set; } = new();

    /// <summary>
    /// HTTP base URL reachable from routers, including the <c>/firmware_files</c> prefix.
    /// Example: <c>http://198.18.0.1:8080/firmware_files</c>. Must not be localhost.
    /// Defaults to empty; required when <see cref="Enabled"/> is true.
    /// </summary>
    public string AdvertisedBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Maximum time allowed for on-router package fetch from the advertised firmware URL.
    /// Default: 10 minutes.
    /// </summary>
    public TimeSpan FetchTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Delay between post-reboot reconnection attempts.
    /// Default: 15 seconds.
    /// </summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Absolute maximum time from successful reboot initiation through reconnection and
    /// post-reboot verification. Default and operational value: five minutes.
    /// </summary>
    public TimeSpan RebootTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Optional shorter poll budget for waiting for the target RouterOS version to appear after
    /// reconnect. Always capped by the remaining <see cref="RebootTimeout"/> window and cannot
    /// extend the absolute reboot deadline. Default: 5 minutes.
    /// </summary>
    public TimeSpan PostUpgradeVerifyTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to wait for remote <c>.rsc</c> / <c>.backup</c> files to become ready after save.
    /// Default: 2 minutes.
    /// </summary>
    public TimeSpan BackupFileReadyTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Poll interval while waiting for remote backup files to appear.
    /// Default: 1 second.
    /// </summary>
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
    /// Default: 22.
    /// </summary>
    public int ScpPort { get; set; } = 22;

    /// <summary>
    /// Timeout for SCP transfer of backup artefacts.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan ScpTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Resolves <see cref="BackupDirectory"/> against <paramref name="baseDirectory"/>,
    /// defaulting to <see cref="AppContext.BaseDirectory"/> when relative.
    /// </summary>
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

/// <summary>
/// Exact management IPv4 + RouterOS identity pin for OSPF lab recovery runners.
/// </summary>
public sealed class OspfLabTargetOptions
{
    /// <summary>Exact management IPv4 of the single lab router. Defaults to empty.</summary>
    public string RouterIp { get; set; } = string.Empty;

    /// <summary>Exact RouterOS identity (case-sensitive ordinal match). Defaults to empty.</summary>
    public string RouterIdentity { get; set; } = string.Empty;

    /// <summary>
    /// External management host used for OSPF management-plane route observation
    /// (e.g. the worker/NMS address). When empty, apply falls back to <see cref="RouterIp"/>.
    /// </summary>
    public string ManagementHost { get; set; } = string.Empty;
}
