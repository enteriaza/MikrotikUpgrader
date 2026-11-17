# Scheduler retries (connection, authentication, firmware transport)

Configuration section: `JobRetry` (`JobRetryOptions`). Authority: `InMemoryJobScheduler.TryScheduleTransientRetry`.

Retries are **scheduler-level requeues of the same job** (same `ImportId`, `JobId`, CSV row, and `DateStartUtc` / `DateStopUtc`). There is **no** durable retry store and **no** independent local retry loop around `/tool/fetch`.

## Categories

| Category | When | Counter | Delay |
|---|---|---|---|
| **Connection** | Transient failure during `CreateAsync` connect/login handshake (timeouts, socket resets, incomplete responses, etc.) | `ConnectionFailureCount` | `JobRetry:ConnectionRetryDelay` |
| **Authentication** | Transient authentication rejection during connect | `AuthenticationFailureCount` | `JobRetry:AuthenticationRetryDelay` |
| **FirmwareTransport** | Transient package-fetch / transport failure **before** that stage’s install/reboot (`UpgradeOutcome.TransientTransportFailure` with `TransportRetryEligible=true`) | **Shared** `ConnectionFailureCount` | **Same** `ConnectionRetryDelay` |

Classification helpers:

- Connect/login: `MikrotikConnectFailureClassifier`
- Package fetch: `FirmwareTransportFailureClassifier` (exception types / HTTP status when available; permanent markers such as HTTP 404 stay terminal)

Cancellation and application shutdown are **never** treated as transport retries (`UpgradeInterrupted` / cancel path).

## Settings (defaults)

| Key | Default | Meaning |
|---|---|---|
| `JobRetry:ConnectionRetryDelay` | `00:10:00` | Delay after Connection **or** FirmwareTransport failure |
| `JobRetry:AuthenticationRetryDelay` | `00:10:00` | Delay after authentication rejection |
| `JobRetry:MaximumUnsuccessfulAttempts` | `10` | Retryable unsuccessful attempts per budget; the **next** failure is terminal. `0` disables retry (first failure terminal) |

Firmware transport shares the Connection unsuccessful-attempt budget and delay. Do not document a separate download retry count.

## Counter reset rules

After a **successful authenticated connect**:

- `AuthenticationFailureCount` is always cleared.
- `ConnectionFailureCount` is cleared **unless** the previous retry category was `FirmwareTransport` (so a mid-upgrade reconnect cannot wipe the shared fetch budget).

`NextRetryUtc` is cleared when a fresh attempt starts.

## Firmware transport eligibility

Package staging failures may set:

- `UpgradeOutcome.TransientTransportFailure`
- `TransportRetryEligible` — true only when the **failing package stage** has not issued a reboot for that stage

`RebootInitiated` may still be true when an **earlier verified** stage already rebooted (for example v6 stepping succeeded, then v7 fetch failed). Dispose of Conn₀ still uses `RebootInitiated`; the scheduler gates requeue on **`TransportRetryEligible`**, not merely `!RebootInitiated`.

Permanent package/config failures (`UpgradeFailed`, resolution failures, HTTP 404, etc.) are **not** requeued as FirmwareTransport.

Backup, OSPF, RouterBOARD, and other non-transport upgrade failures remain terminal (not covered by this policy).

## Staging safety (why transport retry is safe)

Before installation/reboot, every required package must:

1. Complete `/tool/fetch` with status **`finished`**
2. Match catalogue **`SizeBytes` exactly** on the router (`WaitForFetchedPackageSizeAsync`)

Existence alone is insufficient. Same-named router files are removed before each fetch. Incomplete sets never reboot. Size checks are **not** cryptographic hashes — equal-length corrupt content would not be detected.

Details: [firmware.md](firmware.md), [routeros-upgrade.md](routeros-upgrade.md). Historical design notes: [standards/FIRMWARE-TRANSPORT-SCHEDULER-RETRY.md](standards/FIRMWARE-TRANSPORT-SCHEDULER-RETRY.md).

## Behaviour summary

1. Classify failure.
2. If under the maximum for that budget, set `NextRetryUtc = now + delay`, requeue the same job entry.
3. Import `Remaining` / outstanding count is **not** decremented on retry.
4. Terminal CSV columns are written only on eventual terminal outcome (not on each retry).
5. `UpgradeStarted` is not written during connect/login retries that never reach the upgrade boundary; transport retries after `UpgradeStarted` retain the existing CSV association.

## Restart

Delayed retries exist only in memory. Process restart discards them. Operators must re-import CSVs if work should continue.

## Related

- [Scheduler](scheduler.md)
- [CSV import](csv-import.md)
- [Firmware](firmware.md)
- [RouterOS upgrade](routeros-upgrade.md)
