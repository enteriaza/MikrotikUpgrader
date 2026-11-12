# CSV import and result lifecycle

## Directories

Relative to `AppContext.BaseDirectory` + `ScheduledJobs:Path` (default `ScheduledJobs`):

| Folder | Role |
|---|---|
| Incoming (root of Path) | Drop CSV files here |
| `Processing/` | Exclusive ownership while validating/scheduling |
| `Accepted/` | Validated import; updated in place with result columns |
| `Completed/` | Import archived after all **scheduled** jobs are terminal (full activation only) |
| `Rejected/` | Validation failure + sibling `*.error.txt` |

## Required input columns

Case-insensitive, any order:

| Column | Meaning |
|---|---|
| `datestart` | Inclusive eligibility start (local culture/timezone → UTC) |
| `datestop` | Exclusive eligibility end |
| `routername` | Display / device-log identity |
| `routerip` | IPv4 or IPv6 literal (not a hostname) |

The removed `executionmode` header is rejected. Application-owned result columns may already be present on re-import and are **ignored** as inputs (`ScheduledJobCsvParser` maps only required headers).

## Result columns

Written on the **original Accepted CSV** (not a separate report file):

| Column | When written |
|---|---|
| `UpgradeStarted` | After connect/preflight/policy gates succeed and immediately before mutation work in `RouterOsV6UpgradeExecutor` |
| `UpgradeCompleted` | When the job reaches a terminal scheduler outcome |
| `Result` | Same terminal write; formatted from `ScheduledJobTerminalState` (e.g. `Success`, `Failed - Connection`, `Cancelled`) |

Timestamps use invariant `yyyy-MM-dd'T'HH:mm:sszzz`.

### Never-scheduled rows

If activation is cancelled after Accepted but before every row is scheduled, unscheduled rows keep **blank** result fields. The implementation does **not** fabricate a per-device execution result for rows that never entered the scheduler.

## Import identity

| Concept | Meaning |
|---|---|
| `ImportId` | New `Guid` per successful acceptance |
| `RowNumber` | 1-based physical CSV row (header is row 1; first data row is typically 2) |
| Job key | `(ImportId, RowNumber)` — stable duplicate protection |

Identical `routerip` values in different imports do not share CSV writers or path registrations; isolation is by `ImportId` → Accepted path.

## Acceptance and activation sequence

```mermaid
sequenceDiagram
  participant W as Watcher
  participant P as FileProcessor
  participant S as Scheduler
  participant Store as PathStore
  W->>P: Incoming CSV ready
  P->>P: Move to Processing
  P->>P: Validate
  P->>S: RegisterImport(N)
  P->>P: Move to Accepted
  P->>S: SetAcceptedPath(actual path)
  Note over S,Store: Path registered before any dispatch
  P->>S: ScheduleAsync per row
  P->>S: FinalizeImportActivation(N, complete)
  Note over S: Jobs execute; CSV results written
  S->>S: Remaining hits 0
  S->>P: Archive Accepted to Completed
```

### Path-before-dispatch

`SetAcceptedPath` registers the **actual** path returned by `MoveSafely` in `ImportedCsvPathStore` before jobs become dispatchable. The scheduler also holds any pre-accept schedules in `HeldForDispatch` until that registration succeeds (safety net for non-production ordering).

### Activation finalization

`FinalizeImportActivation(scheduledCount, activationIncomplete)` reconciles outstanding work:

- `ValidatedCount` — rows accepted at validation
- `ScheduledCount` — rows successfully passed to `ScheduleAsync`
- `Remaining` — outstanding **scheduled** jobs (never-scheduled rows are subtracted at finalize)

Called once on success, or from ingest failure/cancellation after Accepted. Idempotent via `ActivationFinalized`.

## Incomplete activation (cancellation)

| Situation | Behaviour |
|---|---|
| Cancel before Accepted move | Abort import tracking; after `RegisterImport` the file is already in **Processing** (not Incoming). No Accepted path registration; no Completed archive for that incomplete activation |
| Cancel after Accepted, 0 jobs scheduled | Retire tracking + clear path store; CSV stays in **Accepted** (not Completed/Rejected) |
| Cancel after k of N scheduled | Stop scheduling; keep path while k jobs finish; never-scheduled rows blank; then retire leaving **Accepted** |
| All N scheduled | Normal archive to **Completed** when all jobs terminal |

Incomplete activation **never** archives to Completed as a fully successful import.

## Persistence and job outcomes

- Terminal CSV writes are awaited synchronously in `CompleteTerminalJob` **before** `OnJobTerminal` decrements `Remaining` and before path clear / archive.
- Writer failures are logged; they do **not** change the in-memory router job outcome.
- Concurrent updates serialize on a process-wide `SemaphoreSlim` and replace via `*.results.tmp`.

## Restart / re-import

- No automatic import of Accepted/Completed at startup.
- Leftover `Processing/*.csv` are warned and left in place.
- Manual re-import: copy CSV into Incoming. New `ImportId`; work may repeat.

## Related

- [Scheduler](scheduler.md)
- [Retries](retries.md)
- Root [README](../README.md)
