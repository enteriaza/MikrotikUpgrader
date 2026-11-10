namespace MikrotikWorker.Backup;

public sealed class BackupManifest
{
    public required string JobId { get; init; }

    public required Guid ImportId { get; init; }

    public required string RouterName { get; init; }

    public required string RouterIp { get; init; }

    public string? RouterIdentity { get; init; }

    public string? RouterOsVersionBeforeUpgrade { get; init; }

    public string? Architecture { get; init; }

    public string? BoardName { get; init; }

    public string? RouterboardModel { get; init; }

    public required DateTimeOffset BackupTimestampUtc { get; init; }

    public string Purpose { get; init; } = "pre-upgrade";

    public required string ExportFileName { get; init; }

    public required long ExportSizeBytes { get; init; }

    public required string ExportSha256 { get; init; }

    public required string BinaryBackupFileName { get; init; }

    public required long BinaryBackupSizeBytes { get; init; }

    public required string BinaryBackupSha256 { get; init; }

    public required string BackupCompletionStatus { get; init; }
}

public sealed class DeviceBackupResult
{
    public required bool Succeeded { get; init; }

    public string? FailureReason { get; init; }

    public string? ExportLocalPath { get; init; }

    public string? BinaryLocalPath { get; init; }

    public string? ManifestLocalPath { get; init; }

    public BackupManifest? Manifest { get; init; }
}
