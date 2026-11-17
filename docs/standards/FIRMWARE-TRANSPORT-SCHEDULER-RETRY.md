# Firmware Transport Scheduler Retry

## A. Verdict

**PASS**

Transient firmware package-fetch / transport failures are requeued through `InMemoryJobScheduler` using the same Connection retry delay and unsuccessful-attempt budget as authentication and connectivity failures. Permanent package/configuration failures remain terminal. Partial transfers never reboot.

---

## B. Root cause

Authentication and `CreateAsync` connectivity failures already called `TryScheduleTransientRetry` from `RouterJobExecutor.HandleConnectFailure`. Package `/tool/fetch` failures were caught inside `RouterOsV6UpgradeExecutor`, mapped to `UpgradeOutcome.UpgradeFailed`, and completed as terminal `Failed` — they never entered the scheduler retry path.

Observed production symptom (17 Nov 2026): RouterOS `/tool/fetch` lost the API connection (`TikConnectionReceiveTimeoutException`, 30s receive timeout) → `UpgradeFailed` → terminal `Failed` with no `NextRetryUtc`.

---

## C. Failure classification

| Kind | Examples | Outcome |
|---|---|---|
| **Retryable (transient transport)** | `TikConnectionReceiveTimeoutException`, `TikCommandAbortException`, socket reset, `IOException`, HTTP 5xx / request timeout, incomplete/missing staged size after fetch, “connection lost” fetch status text | `UpgradeOutcome.TransientTransportFailure` → scheduler Connection retry |
| **Terminal (permanent)** | HTTP 404 / 410 / 4xx client errors, catalogue/selection/`InvalidOperationException` without transport markers, missing local package, invalid architecture/version, integrity/validation failures | `UpgradeOutcome.UpgradeFailed` (or existing permanent outcomes) |
| **Not transport retry** | Caller cancellation / shutdown | `UpgradeInterrupted` / cancel path — no Connection attempt consumed |

Classifier: `FirmwareTransportFailureClassifier` (exception types first; HTTP status when present; message markers only as secondary).

---

## D. Retry policy

Confirmed shared with Connection failures:

- Category: `JobRetryFailureCategory.FirmwareTransport`
- Counter: `ConnectionFailureCount` (shared budget)
- Delay: `JobRetryOptions.ConnectionRetryDelay` (configured 10 minutes)
- Cap: `JobRetryOptions.MaximumUnsuccessfulAttempts` (configured 10)
- Exhaustion: 11th unsuccessful attempt → one terminal `Failed`
- No separate download retry loop, delay, or counter

Successful connect after a prior **FirmwareTransport** failure does **not** clear `ConnectionFailureCount` (so mid-upgrade reconnect cannot reset the fetch budget). Authentication and ordinary Connection failures still clear as before.

---

## E. Execution safety

- Fetch failures return before package installation / reboot (`RebootInitiated` remains false; `CompletedStage` stays pre-install).
- Incomplete package sets never trigger installation.
- OSPF mutation and RouterBOARD upgrade remain behind existing backup gates and stage ordering; a retry re-enters the normal pipeline with a fresh connection and credentials.
- Completed destructive stages are not blindly repeated: installation/reboot only run after a complete verified package set on that attempt.
- Existing fetched packages on the router are removed before re-fetch (`MikrotikApiClient.FetchUrlToFileAsync`), so stale/partial files are not treated as success.
- Cancellation is never classified as transient transport.

---

## F. Scheduler integrity

- Same job requeued (`ImportId`, `JobId`, router identity, `DateStartUtc`/`DateStopUtc`, CSV row, result-file association preserved).
- Outstanding-job accounting unchanged (retry via `TryScheduleTransientRetry`, not a new schedule).
- CSV not completed/archived while retries remain.
- `DateStopUtc` remains authoritative; window is not extended.
- Channel / max-pending backpressure unchanged.

---

## G. Changed files

| File | Reason |
|---|---|
| `src/.../Upgrade/FirmwareTransportFailureClassifier.cs` | **New** — transient vs permanent fetch/transport classification |
| `src/.../Upgrade/UpgradeOutcome.cs` | `TransientTransportFailure` |
| `src/.../Upgrade/RouterOsV6UpgradeExecutor.cs` | Map fetch/incomplete to transient vs permanent outcomes |
| `src/.../Scheduling/JobRetryFailureCategory.cs` | `FirmwareTransport` |
| `src/.../Scheduling/InMemoryJobScheduler.cs` | Share Connection delay/budget for `FirmwareTransport` |
| `src/.../Scheduling/RouterJobExecutor.cs` | Handle transient transport → `TryScheduleTransientRetry`; preserve ConnectionFailureCount after FirmwareTransport |
| `src/.../Scheduling/ImportedCsvResultColumns.cs` | CSV result text for FirmwareTransport |
| `tests/.../FirmwareTransportFailureClassifierTests.cs` | **New** classification tests |
| `tests/.../JobTransientRetryTests.cs` | Scheduler requeue / exhaustion / permanent-upgrade cases |
| `tests/.../FirmwareUploadStorageModelTests.cs` | Partial/multi-package fetch + transient timeout |
| `tests/.../RouterOsV6UpgradeExecutorTests.cs` | Incomplete staging expects `TransientTransportFailure` |
| `tests/.../Infrastructure/Hosts/SchedulerExecutionTestHost.cs` | Stub upgrade failure/attempt plumbing |
| `docs/standards/FIRMWARE-TRANSPORT-SCHEDULER-RETRY.md` | This report |

Unrelated prior package-layout / OSPF working-tree files were preserved and not redesigned by this change.

---

## H. Validation

| Run | Result |
|---|---|
| Focused classifier + transport + JobTransientRetry (+ connect classifier) | **60 passed** |
| Narrow regression set (executor incomplete/404 + transport) | **18 passed** |
| Full non-live suite (Release; default VSTest live exclusions) | **1301 passed**, 0 failed |
| Release build | **Succeeded** (0 warnings / 0 errors) |
| Windows Native AOT | **Succeeded** (`release/win-x64/MikrotikWorker.exe`, ~26.6 MB) |
| Linux Native AOT | **Succeeded** (`release/linux-x64/MikrotikWorker`, ~23.7 MB; Zig/AotAnywhere) |

Notes:

- An earlier mis-filtered run (`FullyQualifiedName!~Live`) incorrectly included live Tommy harnesses and reported 7 failures; that filter is not the project default.
- `ImportedCsvPathRegistrationTests.TerminalConnectionFailure_BeforeUpgrade_PersistsAfterPathRegistration` timed out once under that noisy run; it passed in the clean 1301-run (timing flake candidate if it recurs).

---

## I. Safety and Git state

- No live router operations
- No commits or pushes
- Existing uncommitted work preserved
- No unrelated OSPF, package-selection, or scheduler redesign
- Branch: `master`
- HEAD: `cb2d60af1f02fa13edda5e444b040c37a7b254ff`
- Working tree: dirty (package-layout prior work + this scheduler retry change)

**Acceptance:** Transient firmware download/fetch failures requeue through the existing scheduler with the same delay and attempt limits as auth/connectivity failures; permanent errors remain terminal; partial transfers cannot install/reboot.
