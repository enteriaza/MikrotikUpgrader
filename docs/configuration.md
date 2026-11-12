# Configuration reference

Sources: `appsettings.json`, environment-specific `appsettings.*.json`, optional gitignored `appsettings.*.local.json`, and environment variables (`Section__Key`).

Paths that are relative resolve from **`AppContext.BaseDirectory`**, not the process working directory.

Do not commit real credentials. Use placeholders in repository files.

## `Listening`

| Key | Default | Purpose |
|---|---|---|
| `Listening:IpAddress` | `0.0.0.0` | Kestrel bind address |
| `Listening:Port` | `8080` | Kestrel port for `/firmware_files` |

## `FirmwareFiles`

| Key | Default | Purpose |
|---|---|---|
| `FirmwareFiles:Path` | `FirmwareFiles` | On-disk package root |

## `FirmwareUpgrade`

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `true` (shipped json) | Administrative kill-switch for fetch/install |
| `AdvertisedBaseUrl` | `""` | Router-reachable HTTP base for `/tool/fetch` (required when enabled) |
| `FetchTimeout` | `00:10:00` | Package fetch timeout |
| `ReconnectInterval` | `00:00:15` | Post-reboot reconnect polling interval |
| `RebootTimeout` | `00:05:00` | Reboot wait budget |
| `PostUpgradeVerifyTimeout` | `00:05:00` | Post-upgrade verify budget |
| `BackupFileReadyTimeout` | `00:02:00` | Wait for backup file readiness |
| `BackupFileReadyPollInterval` | `00:00:01` | Poll interval |
| `BackupPassword` | `""` | Optional binary backup password |
| `BackupDirectory` | `Backups` | Central backup store |
| `ScpPort` | `22` | TCP port for backup download (SSH.NET **SFTP**; name retained for historical reasons) |
| `ScpTimeout` | `00:05:00` | SFTP transfer timeout |
| `OspfLabTarget:RouterIp` / `RouterIdentity` | empty | Lab targeting only |

## `Mikrotik`

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | When false, no API connections for scheduled jobs |
| `Username` / `Password` | placeholders | Shared RouterOS API credentials |
| `Port` | `8728` | API port |
| `ConnectionTimeout` | `00:00:10` | Connect+login timeout |
| `ReconnectInterval` | `00:00:30` | **Reserved / unused** for reconnect behaviour (validated `> 0` only). Post-reboot polling uses `FirmwareUpgrade:ReconnectInterval`. |

**Independence:** `Mikrotik:Enabled` and `FirmwareUpgrade:Enabled` are separate gates.

## `ScheduledJobs`

| Key | Default | Purpose |
|---|---|---|
| `Path` | `ScheduledJobs` | Incoming + subfolders root |
| `ScanInterval` | `00:01:00` | Directory scan period |
| `FileReadyCheckInterval` | `00:00:02` | Stability poll |
| `FileReadyTimeout` | `00:02:00` | Max wait for stable file |
| `MaximumFileSizeBytes` | `52428800` | Max CSV size |
| `MaximumRecords` | `500000` | Max rows |
| `MaximumPendingJobs` | `10000` | Scheduler admission capacity |
| `MaxConcurrentCsvImports` | `2` | Concurrent ingestions |
| `MaxValidationErrorsRetained` | `100` | Error report detail cap |
| `DiscoveryQueueCapacity` | `1024` | Discovery buffer |
| `MaxConcurrentRouterJobs` | `12` | Execution workers (minimum 12) |

## `JobRetry`

| Key | Default | Purpose |
|---|---|---|
| `ConnectionRetryDelay` | `00:10:00` | Requeue delay after connection failure |
| `AuthenticationRetryDelay` | `00:10:00` | Requeue delay after auth rejection |
| `MaximumUnsuccessfulAttempts` | `10` | Per-category retryable attempts |

See [retries.md](retries.md).

## `Csv`

| Key | Default | Purpose |
|---|---|---|
| `Culture` | `en-ZA` | CSV date culture |
| `TimeZoneId` | `Africa/Johannesburg` | Naive local → UTC |
| `DateTimeFormats` | listed in appsettings | Exact parse formats |

## `Worker`

| Key | Default | Purpose |
|---|---|---|
| `HeartbeatInterval` | shipped json: `00:00:30`; `WorkerOptions` initializer if key omitted: **5 seconds** | Background heartbeat period |

## `Logging:Serilog`

See root README logging section. Defaults include daily rolling application logs and optional per-device logs under `Logs/Devices`.

## `LivePackageCompatibilityAudit`

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Second gate for live read-only audit |
| `MaxConcurrentConnections` | `5` | Live audit concurrency |
| `ConnectionTimeout` / `TotalAuditTimeout` | null | Optional overrides |

## `RedistributeSummaryviaStatics`

Root boolean (not nested). When `true`, enables the optional OSPF static redistribution stage after verified migration. Shipped `appsettings.json` sets **`true`**. The options property defaults to **`false`** if the key is omitted (CLR `bool` default). See [backup-and-ospf.md](backup-and-ospf.md).

## Related

- Root [README](../README.md)
