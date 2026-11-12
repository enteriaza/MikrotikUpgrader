// -----------------------------------------------------------------------------
// File: BackupManifest.cs
//
// Description:
// DTOs for the JSON backup manifest written beside central .rsc/.backup files,
// and the DeviceBackupResult returned by IDeviceBackupService including remote
// cleanup status.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Backup;

/// <summary>
/// Immutable metadata written to <c>Backups/Manifests/{stem}.json</c> after a successful central backup.
/// </summary>
public sealed record BackupManifest
{
    /// <summary>Scheduler job id (<c>import:row</c>).</summary>
    public required string JobId { get; init; }

    /// <summary>CSV import identifier.</summary>
    public required Guid ImportId { get; init; }

    /// <summary>Router name from the CSV row.</summary>
    public required string RouterName { get; init; }

    /// <summary>Router management IP as string.</summary>
    public required string RouterIp { get; init; }

    /// <summary>RouterOS identity when known.</summary>
    public string? RouterIdentity { get; init; }

    /// <summary>Device RouterOS version captured before upgrade.</summary>
    public string? RouterOsVersionBeforeUpgrade { get; init; }

    /// <summary>Device architecture when known.</summary>
    public string? Architecture { get; init; }

    /// <summary>Board name when known.</summary>
    public string? BoardName { get; init; }

    /// <summary>RouterBOARD model when known.</summary>
    public string? RouterboardModel { get; init; }

    /// <summary>UTC timestamp used in the artefact stem.</summary>
    public required DateTimeOffset BackupTimestampUtc { get; init; }

    /// <summary>
    /// Human-readable purpose label. Default: <c>pre-upgrade</c>.
    /// </summary>
    public string Purpose { get; init; } = "pre-upgrade";

    /// <summary>Local export file name (<c>.rsc</c>).</summary>
    public required string ExportFileName { get; init; }

    /// <summary>Export file size in bytes after download.</summary>
    public required long ExportSizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the local export.</summary>
    public required string ExportSha256 { get; init; }

    /// <summary>Local binary backup file name (<c>.backup</c>).</summary>
    public required string BinaryBackupFileName { get; init; }

    /// <summary>Binary backup size in bytes after download.</summary>
    public required long BinaryBackupSizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the local binary backup.</summary>
    public required string BinaryBackupSha256 { get; init; }

    /// <summary>Completion status string recorded in the manifest (e.g. success).</summary>
    public required string BackupCompletionStatus { get; init; }

    /// <summary>Remote export file name that was created on the router, when known.</summary>
    public string? RemoteExportFileName { get; init; }

    /// <summary>Remote binary file name that was created on the router, when known.</summary>
    public string? RemoteBinaryFileName { get; init; }
}

/// <summary>
/// Outcome of <see cref="IDeviceBackupService.CreatePreUpgradeBackupAsync"/>.
/// </summary>
public sealed class DeviceBackupResult
{
    /// <summary>True when local artefacts and manifest were verified successfully.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Failure reason when <see cref="Succeeded"/> is false; never contains credentials.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Local path to the downloaded <c>.rsc</c>, when created.</summary>
    public string? ExportLocalPath { get; init; }

    /// <summary>Local path to the downloaded <c>.backup</c>, when created.</summary>
    public string? BinaryLocalPath { get; init; }

    /// <summary>Local path to the written manifest JSON, when created.</summary>
    public string? ManifestLocalPath { get; init; }

    /// <summary>Manifest payload when successfully written.</summary>
    public BackupManifest? Manifest { get; init; }

    /// <summary>True when remote leftover cleanup removed all owned remote files.</summary>
    public bool RemoteCleanupSucceeded { get; init; }

    /// <summary>True when remote cleanup attempted but at least one owned remote file remained.</summary>
    public bool RemoteCleanupFailed { get; init; }

    /// <summary>
    /// Remote file names still present after cleanup attempts. Defaults to empty.
    /// </summary>
    public IReadOnlyList<string> RemainingRemoteFiles { get; init; } = [];
}
