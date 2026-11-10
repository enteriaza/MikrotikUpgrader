# MikrotikUpgrader

.NET 10 Worker Service that hosts:

- a background worker for future MikroTik fleet upgrade jobs
- a Kestrel HTTP server that streams RouterOS firmware packages from disk
- a tik4net-based native RouterOS API communication foundation
- CSV-based scheduled job ingestion with a bounded, time-aware in-memory scheduler

CSV ingestion, in-memory scheduling, and **connect/disconnect-only** router job execution are implemented. **Firmware upgrades, package transfers, and RouterOS configuration changes are not implemented yet.**

**Deployment model:** a single application instance. All job and scheduling state is in memory only. There is no durable job store. A restart discards in-memory state; retained CSV files are the operator-controlled source for re-import (which may repeat work).

## Repository layout

```text
MikrotikUpgrader/
├── FirmwareFiles/                   # Source packages; copied into build/publish output
│   └── mikrotik/
├── src/MikrotikWorker/
└── tests/MikrotikWorker.Tests/
```

## Runtime layout (beside binaries)

```text
MikrotikWorker/
├── MikrotikWorker.exe
├── appsettings.json
├── FirmwareFiles/
│   └── mikrotik/
├── Logs/
│   └── application-YYYYMMDD.log     # Central Serilog application log
└── ScheduledJobs/
    ├── *.csv                        # Incoming drop folder
    ├── Processing/
    ├── Accepted/
    ├── Completed/
    └── Rejected/
```

Relative paths resolve from `AppContext.BaseDirectory`.

## ScheduledJobs CSV

Create a CSV in Microsoft Excel and save it into `ScheduledJobs` (Incoming).

Required columns (case-insensitive, any order):

| Column | Description |
|---|---|
| `datestart` | When jobs become eligible |
| `datestop` | When jobs must stop starting |
| `routername` | Router identity/name |
| `routerip` | IPv4 or IPv6 address (not a hostname) |

Example:

```csv
datestart,datestop,routername,routerip
01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1
01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-02,192.168.1.2
```

Excel CSV exports depend on regional settings. Configure the application culture to match the exported date representation.

### Culture / timezone

```json
{
  "Csv": {
    "Culture": "en-ZA",
    "TimeZoneId": "Africa/Johannesburg",
    "DateTimeFormats": [
      "dd/MM/yyyy HH:mm:ss",
      "dd/MM/yyyy HH:mm",
      "d/M/yyyy H:mm:ss",
      "d/M/yyyy H:mm",
      "yyyy/MM/dd HH:mm:ss",
      "yyyy/MM/dd HH:mm"
    ]
  }
}
```

- Dates are parsed with `DateTime.TryParseExact` using the configured culture and formats.
- Timezone-naive values are interpreted in `TimeZoneId`, then stored/compared as **UTC**.
- Ambiguous daylight-saving local times use the earlier (standard) offset; invalid DST gaps are rejected.

### Validation rules

- Required headers present
- Non-empty required values
- Valid IPv4/IPv6 literals only
- `datestart` / `datestop` both in the future at submission time
- `datestop` later than `datestart`
- Identical scheduling window on every row of a file
- Configurable max file size, max record count, and max retained validation error details

Invalid files are moved to `Rejected/` with a sibling `*.error.txt` report (total error count included; excess details may be omitted).

### File lifecycle

```text
Incoming CSV
  → wait until file is ready (exclusive open + stable size)
  → Processing/
  → validate entire file
      → Rejected/ (+ error report)
      → schedule all jobs into the in-memory scheduler (with backpressure)
      → Accepted/
          → when every job for that import reaches a terminal state → Completed/
```

Terminal outcomes:

- **Succeeded** — API connect and disconnect completed cleanly (no upgrade claimed)
- **Failed** — credential resolution, connect, or disconnect failed
- **Expired** — window ended before execution (`DateStopUtc` exclusive)
- **Cancelled** — abort/shutdown cancelled the job

`Completed` means all associated in-memory jobs are terminal, **not** that routers were upgraded.

### Scheduler vs execution workers

| Component | Responsibility |
|---|---|
| `InMemoryJobScheduler` | Future/expired decisions, dispatch eligible jobs, capacity, import batch completion |
| `RouterJobExecutionService` | Bounded connect/disconnect workers (`MaxConcurrentRouterJobs`) |

Eligible jobs are handed off on a bounded channel (sized to `MaximumPendingJobs`). Workers recheck the UTC window immediately before connecting. Future jobs never block eligible ones. Occupied workers never block the scheduler loop.

### Concurrency

```json
"MaxConcurrentRouterJobs": 12
```

Default and **minimum** is **12**. Raise only after considering CPU, memory, socket limits, and router latency. There is no auto-scaling in this milestone.

### Shared RouterOS API credentials

All routers use the **same** username and password from `appsettings.json`. CSV files supply only router addresses (`routerip`) — never credentials.

```json
{
  "Mikrotik": {
    "Enabled": false,
    "Username": "your-router-username",
    "Password": "your-router-password",
    "Port": 8728,
    "ConnectionTimeout": "00:00:10",
    "ReconnectInterval": "00:00:30"
  }
}
```

| Setting | Role |
|---|---|
| `Enabled` | When `false`, jobs fail closed without opening API connections. When `true`, shared credentials are required at startup. |
| `Username` / `Password` | Shared RouterOS API account for every scheduled router |
| `Port` | Default RouterOS API TCP port (8728) |
| `ConnectionTimeout` | TCP connect + login handshake timeout |

Set real credentials only on the deployment host. Keep placeholders in the repository copy of `appsettings.json` — do not commit production passwords.

The shared account must be allowed to open the RouterOS API (port 8728) and SFTP (port 22 by default) from the worker host. With `FirmwareUpgrade:Enabled=false` (default), jobs perform connect → assessment → disconnect only. With upgrades enabled, eligible v6 devices continue through backup → fetch → reboot → verify.

Local overrides (gitignored): place `appsettings.Development.local.json` beside the appsettings files with real credentials for development/live testing. Do not commit that file.

### Firmware compatibility assessment

Target RouterOS baseline (configurable, default **6.49.23**). Versions are parsed numerically; channel suffixes such as `(long-term)`, `rc`, and `beta` are recognised and ignored for comparison.

| Detected version | Overall status |
|---|---|
| v6 earlier than target + matching main package | `ReadyForUpgrade` — automated upgrade to 6.49.23 is permitted when `FirmwareUpgrade:Enabled=true` |
| Exactly target v6 | `AlreadyAtTarget` |
| v6 later than target | `ManualReviewRequired` — no automatic downgrade |
| v7+ | `Unsupported` |
| Unparseable / missing package / arch mismatch | `Blocked` |

`FirmwareAssessment:TargetRouterOsVersion` and `FirmwareUpgrade:TargetRouterOsVersion` must remain **v6** releases; v7 targets are rejected at startup.

**Package matching** uses the FirmwareFiles catalog:

- Main v6: `routeros-{arch}-{version}.npk`
- Optional/split v6: `{name}-{version}-{arch}.npk` (e.g. `tr069-client-6.49.23-mipsbe.npk`, `user-manager-6.49.23-mipsbe.npk`)
- Device `ppc` maps to `powerpc` for matching
- Exact package name + architecture + version required; ambiguous duplicates fail safe

RouterBOARD current vs upgrade firmware is collected and logged only. This milestone never runs `/system/routerboard/upgrade`. RouterOS v7.23.8 upgrades are deferred.

### Automated RouterOS v6 → 6.49.23 upgrade

Enable with `FirmwareUpgrade:Enabled=true` and set `AdvertisedBaseUrl` to an HTTP URL **reachable from routers** (not localhost), ending in `/firmware_files`.

Sequence for eligible devices:

1. Inventory installed packages (`/system/package/print`)
2. Resolve the complete target package set (main + every installed optional package)
3. **Mandatory pre-upgrade backup** (blocking):
   - `/export file={name}` → `{name}.rsc`
   - `/system/backup/save name={name}` (optional separate `BackupPassword`; never the login password)
   - Wait until both files exist with non-zero size
   - Retrieve via **SFTP** to `Backups/Exports/` and `Backups/Binary/` (API `contents` cannot transfer binary intact on RouterOS v6)
   - Write SHA-256 + JSON manifest under `Backups/Manifests/`
4. Only then `/tool/fetch` each package from Kestrel (`mode=http`, `keep-result=yes`, `dst-path=…`)
5. Verify on-router file sizes against the catalog
6. `/system/reboot` (`.npk` install on reboot)
7. Reconnect and verify exact RouterOS 6.49.23 + installed package versions

Backup failure, missing packages, fetch failures, or verification mismatches stop that device — no fetch/reboot after a failed backup. There is no automatic rollback or durable crash recovery: if the worker stops mid-upgrade, inspect the device and re-import the CSV manually.

Backups may contain secrets. They are stored under `Backups/` (not under `FirmwareFiles`, not served by Kestrel, not published). Protect the directory with filesystem ACLs. Backups are never auto-deleted.

### Per-device logs

Beside the application log, each router job mirrors connection + assessment events to:

`Logs/Devices/{RouterName}_{RouterIp}.log`

Example: `Logs/Devices/MikroTik_198.18.0.83.log` (name sanitized; IP prevents identity collisions). The writer uses a bounded async queue and opens/closes files per write so the process does not retain a handle per device.

### In-memory scheduler

- Priority queue ordered by `DateStartUtc` with a single `TimeProvider`-based wake
- Future jobs never block immediately eligible jobs
- Inclusive start (`>= DateStartUtc`), exclusive stop (`>= DateStopUtc` ⇒ expired)
- Bounded by `MaximumPendingJobs`; ingestion waits for capacity and never silently drops rows
- Execution concurrency is separate (`MaxConcurrentRouterJobs`)

### Restart and re-import

- Restart discards all in-memory jobs and import tracking.
- `Accepted/` and `Completed/` CSVs are **not** auto-imported at startup.
- Leftover `Processing/*.csv` files are logged as warnings and left in place (not auto-imported).

**Manual re-import:** copy or move a CSV from `Accepted/` or `Completed/` (or a verified `Processing/` file) into the Incoming `ScheduledJobs` folder. A new `ImportId` and new jobs are created. Work already performed before a crash may run again.

**Interrupted Processing recovery:** inspect the leftover file, verify it is complete, then move it to Incoming. Do not expect the application to reconstruct partial job state.

## Firmware HTTP server

URL prefix remains `/firmware_files` and maps to the deployed `FirmwareFiles` directory.

```powershell
curl -O http://127.0.0.1:8080/firmware_files/mikrotik/routeros-tile-6.49.23.npk
```

## Logging (Serilog)

Serilog is the **single** logging provider for the Generic Host, Worker Services, ScheduledJobs, Kestrel, and ASP.NET Core infrastructure. Components continue to use constructor-injected `ILogger<T>`; there is one central application log in this milestone (per-router log files come later).

### Destinations

| Destination | Default |
|---|---|
| Console | Readable template with timestamp, level, source context, message, exception |
| Rolling file | `Logs/application-YYYYMMDD.log` beside the published binaries |

Relative `LogDirectory` values resolve from `AppContext.BaseDirectory` (not the process working directory). The log directory is created automatically.

### Defaults

- Daily rolling, UTF-8, shared file access
- Retain 30 files
- 100 MB file size limit with roll-on-size
- Asynchronous bounded file buffer (`AsyncBufferCapacity`, default 10 000; `AsyncBlockWhenFull` avoids silent drops)
- Shutdown calls `Log.CloseAndFlushAsync()` so buffered events are written

Trade-off: under extreme burst load, `AsyncBlockWhenFull` can briefly slow the logging caller instead of dropping events. Do not log CSV contents, firmware payloads, or credentials.

### Configuration

```json
{
  "Logging": {
    "Serilog": {
      "LogDirectory": "Logs",
      "FileName": "application-.log",
      "RollingInterval": "Day",
      "RetainedFileCountLimit": 30,
      "FileSizeLimitBytes": 104857600,
      "RollOnFileSizeLimit": true,
      "MinimumLevel": "Information",
      "WriteToConsole": true,
      "WriteToFile": true,
      "AsyncBufferCapacity": 10000,
      "AsyncBlockWhenFull": true
    }
  }
}
```

Development defaults to `MinimumLevel: Debug`. Override via environment-specific `appsettings` or environment variables (`Logging__Serilog__MinimumLevel`, etc.).

### Levels

| Level | Usage |
|---|---|
| Trace / Debug | Scheduler internals, retries, high-frequency diagnostics |
| Information | Startup/shutdown, imports, scheduling, job eligibility, HTTP requests |
| Warning | Rejected CSV, expired jobs, capacity pressure, HTTP 4xx |
| Error | Failed file operations, unexpected operational errors, HTTP 5xx |
| Fatal | Host cannot continue / unhandled startup failure |

HTTP request logging uses Serilog request middleware (method, path, status, duration). Query strings and bodies are not logged.

### systemd

Console output goes to the service journal when running under systemd. Prefer the rolling file under `Logs/` for persistent operational history. Point `LogDirectory` at a writable path if the service user cannot write beside the binaries.

### Future per-router logs

Router operations should keep structured properties such as `RouterName`, `RouterIp`, `RouterIdentity`, `JobId`, and `ImportId` (and `LogContext` where useful). Per-router sinks are **not** implemented yet; a later milestone can route by those properties without changing every call site.

## Configuration excerpt

```json
{
  "Logging": {
    "Serilog": {
      "LogDirectory": "Logs",
      "FileName": "application-.log",
      "MinimumLevel": "Information"
    }
  },
  "FirmwareFiles": { "Path": "FirmwareFiles" },
  "ScheduledJobs": {
    "Path": "ScheduledJobs",
    "ScanInterval": "00:01:00",
    "FileReadyCheckInterval": "00:00:02",
    "FileReadyTimeout": "00:02:00",
    "MaximumFileSizeBytes": 52428800,
    "MaximumRecords": 500000,
    "MaximumPendingJobs": 10000,
    "MaxConcurrentCsvImports": 2,
    "MaxValidationErrorsRetained": 100,
    "DiscoveryQueueCapacity": 1024,
    "MaxConcurrentRouterJobs": 12
  },
  "Mikrotik": {
    "Enabled": false,
    "Username": "your-router-username",
    "Password": "your-router-password",
    "Port": 8728,
    "ConnectionTimeout": "00:00:10",
    "ReconnectInterval": "00:00:30"
  }
}
```

## Build / test / publish

```powershell
dotnet restore
dotnet clean
dotnet build --no-restore
dotnet test --no-build
dotnet publish src/MikrotikWorker/MikrotikWorker.csproj -c Release
```

## Dependencies

- **tik4net** 4.0.4 — native RouterOS API
- **CsvHelper** 33.1.0 — CSV parsing
- **Serilog.AspNetCore** 10.0.0 — host/ASP.NET Core logging integration (console + file sinks)
- **Serilog.Sinks.Async** 2.1.0 — bounded asynchronous file writes
