# Final Safety Audit — Scheduler Retries, Partial Package Files and Upgrade Stage Boundaries

> **Historical safety audit** (authored ~2026-11-17). Findings describe the implementation at audit time. Current staging and stage-boundary behaviour: [../firmware.md](../firmware.md), [../routeros-upgrade.md](../routeros-upgrade.md), [../retries.md](../retries.md).

## A. Verdict

**PASS WITH FINDINGS**

Pre-installation transient fetch failures are safe to requeue. Incomplete or wrong-size staged files cannot trigger installation. One confirmed defect (sticky `RebootInitiated` blocking safe later-stage transport retries after verified v6 stepping) was fixed with `TransportRetryEligible`. RouterOS completeness evidence remains size-based (not cryptographic integrity).

---

## B. Package staging safety

### Lifecycle (actual code)

1. Resolve packages (`UpgradePackageResolver`) — identity/arch/version from catalogue.
2. Pre-upgrade backup (stage gate).
3. For each required package: `MikrotikApiClient.FetchUrlToFileAsync`.
4. **Before fetch:** if a same-named router file exists, it is **removed** (prevents leftover partial/complete reuse by name alone).
5. `/tool/fetch` must end with status `finished` (failed status → permanent or classified exception).
6. `WaitForFetchedPackageSizeAsync` requires `RouterFileInfo.SizeBytes == CatalogPackage.SizeBytes` (exact match). Existence alone is insufficient.
7. Only after **every** package in the resolution set passes size verification does the stage call `RebootAsync`.
8. Transient fetch/incomplete outcomes return `TransientTransportFailure` with `TransportRetryEligible=true` and **no reboot**.

### Incomplete / corrupt / prior files

| Situation | Behaviour |
|---|---|
| Fetch throws mid-transfer | Partial may remain until next attempt; attempt aborts before reboot. Next attempt removes then re-fetches. |
| Fetch “finished” but wrong/zero size | `TransientTransportFailure`; no reboot. |
| Multi-package: A,B OK, C fails | No reboot; incomplete set never installs. |
| Scheduler retry | Full stage staging repeats; each package remove-then-fetch (no silent name-based reuse). |
| Local catalogue missing / 404 | Permanent `UpgradeFailed` / resolution failure — not transport-retried. |

### RouterOS evidence used

- `/tool/fetch` progress/final **status** (`finished` / `failed` + failure text).
- `/file` (via `FindFileAsync`) **name + SizeBytes**.

**Limitation (not a defect in this change):** there is no RouterOS-exposed package checksum/hash verification in this path. Completeness is **byte-size equality** against the local catalogue file length, plus fetch finished status. Corrupt content of equal length would not be detected.

---

## C. Upgrade-stage safety

### Actual stage sequence (`RouterOsV6UpgradeExecutor.ExecuteAsync`)

1. Admin / version / assessment / schedule gates; record `UpgradeStarted`.
2. Legacy FleetUpgrade automation cleanup.
3. Optional **v6 stepping** stage: resolve → backup → fetch all → reboot → reconnect → verify.
4. Identity classification; OSPF **capture/validate** (plan only).
5. **RouterOS 7** stage (when eligible): resolve → backup → fetch all → reboot → reconnect → verify.
6. Central verified backup gate → OSPF apply/verify → optional static-summary → RouterBOARD → terminal success.

### Transient failure boundaries

| Boundary | Outcome | Scheduler retry? | Destructive follow-on? |
|---|---|---|---|
| Fetch / incomplete size **before** stage reboot | `TransientTransportFailure`, `TransportRetryEligible=true` | Yes (Connection budget) | No install/reboot/OSPF/RB |
| After **this stage’s** reboot started | Timeouts / verify failures etc. (not `TransientTransportFailure`) | No transport requeue | No blind re-install of that stage via transport path |
| After verified v6, during v7 **pre-install** fetch | `TransientTransportFailure`, `RebootInitiated=true` (prior stage), `TransportRetryEligible=true` | **Yes** (after fix) | OSPF/RB not entered; next job attempt re-reads version and skips completed v6 |
| Post-v7 OSPF / RB failures | Dedicated outcomes (`BackupFailed`, OSPF/RB failures) | Not as firmware transport | Existing gates unchanged |

`CompletedStage` is **per attempt** (in-memory). Scheduler retries re-enter `ExecuteAsync` from the start with a fresh connection; safety relies on **current device version/inventory**, not persisted stage checkpoints. That is correct for pre-install fetch failures and for post-v6 retry (device already on stepping).

Cancellation / shutdown never maps to transport retry.

---

## D. Findings

### F1 — Sticky `RebootInitiated` blocked safe later-stage transport retry  
- **Severity:** Medium (operational / safety-of-progress; fail-closed was overly broad)  
- **Where:** `RouterJobExecutor` previously required `!RebootInitiated`; `RouterOsV6UpgradeExecutor` ORed prior-stage reboot into stage2 failure results.  
- **Evidence:** After verified v6 reboot, v7 fetch timeout returned `TransientTransportFailure` with `RebootInitiated=true` and was mapped to terminal `Failed` instead of `TryScheduleTransientRetry`.  
- **Consequence:** Device left at stepping version without Connection-budget retries for a later pre-install fetch blip.  
- **Status:** **Fixed** — `UpgradeExecutionResult.TransportRetryEligible` set only when the **failing stage** has not rebooted; scheduler gates on that flag while preserving `RebootInitiated` for Conn₀ dispose semantics. Regression: `AfterV6Stepping_TransientV7FetchFailure_IsTransportRetryEligible_WithoutOspfOrRouterboard`, `TransientTransport_WithPriorStageReboot_StillRequeuesWhenEligible`.

### F2 — No cryptographic package integrity on router  
- **Severity:** Low (RouterOS evidence limitation)  
- **Where:** `WaitForFetchedPackageSizeAsync` / catalogue `SizeBytes`.  
- **Evidence:** Size equality only.  
- **Consequence:** Equal-length corrupt payload would not be rejected.  
- **Status:** **Open / accepted limitation** — not inventing unsupported RouterOS hash APIs in this audit.

### F3 — Leftover partial files until next attempt  
- **Severity:** Info  
- **Where:** Fetch exception path returns without immediate `RemoveFileAsync`.  
- **Evidence:** Cleanup is on the **next** `FetchUrlToFileAsync` (remove-before-fetch).  
- **Consequence:** Harmless for install eligibility (size gate + no reboot); next attempt deletes before re-fetch.  
- **Status:** Acceptable; not changed.

Retry policy (10 attempts / 10 minutes / shared `ConnectionFailureCount`) **unchanged**.

---

## E. Changed files

| File | Reason |
|---|---|
| `src/.../Upgrade/UpgradeOutcome.cs` | `TransportRetryEligible` |
| `src/.../Upgrade/RouterOsV6UpgradeExecutor.cs` | Set/propagate eligibility; stage2 wrap preserves prior reboot for dispose |
| `src/.../Scheduling/RouterJobExecutor.cs` | Gate transport requeue on `TransportRetryEligible` |
| `tests/.../SchedulerExecutionTestHost.cs` | Stub sets eligibility for Transient |
| `tests/.../JobTransientRetryTests.cs` | Prior-reboot eligible requeue + ineligible terminal |
| `tests/.../TwoStageUpgradeWorkflowTests.cs` | v6-then-v7 fetch fail eligibility; StageTrackingOps fetch fail hook |
| `tests/.../FirmwareUploadStorageModelTests.cs` | Wrong-size rejection; eligibility asserts; remove-before-fetch source checks |
| `tests/.../RouterOsV6UpgradeExecutorTests.cs` | Incomplete staging eligibility asserts |
| `docs/standards/FIRMWARE-TRANSPORT-RETRY-SAFETY-AUDIT.md` | This report |

No package-selection, OSPF policy, or scheduler architecture redesign.

---

## F. Validation

| Run | Result |
|---|---|
| Focused staging/retry/classifier set | **46 passed** |
| Focused OSPF/RB/staging/retry slice | **39 passed** |
| Full non-live Release suite | **1305 passed**, 0 failed |
| Release build | **Succeeded** (0 warnings / 0 errors) |
| Windows Native AOT | **Succeeded** (`release/win-x64/MikrotikWorker.exe`, ~26.6 MB) |
| Linux Native AOT | **Succeeded** (`release/linux-x64/MikrotikWorker`, ~23.7 MB; Zig/AotAnywhere) |

---

## G. Safety and Git state

- No live router operations  
- No commits or pushes  
- Existing uncommitted work preserved  
- No unrelated package-selection / OSPF / scheduler redesign  
- Branch: `master`  
- HEAD: `cb2d60af1f02fa13edda5e444b040c37a7b254ff`  
- Working tree: dirty (prior package-layout work + transport retry + this audit fix)

**Acceptance:** Incomplete packages cannot install/reboot; pre-install retries are safe; post-install transport requeue remains blocked unless the failing stage never rebooted; OSPF/RB gates intact; scheduler retry policy unchanged.
