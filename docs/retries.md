# Connection and authentication retries

Configuration section: `JobRetry` (`JobRetryOptions`).

## Scope

Retries apply **only** to transient failures during **initial connect/login**, before the upgrade workflow mutates the device.

Upgrade-stage failures (backup, fetch, reboot, verify, OSPF, etc.) are **not** retried by this policy. They complete as terminal job outcomes.

## Settings (defaults from `appsettings.json` / option initializers)

| Key | Default | Meaning |
|---|---|---|
| `JobRetry:ConnectionRetryDelay` | `00:10:00` | Delay before re-eligibility after connection failure |
| `JobRetry:AuthenticationRetryDelay` | `00:10:00` | Delay after authentication rejection |
| `JobRetry:MaximumUnsuccessfulAttempts` | `10` | Per-category attempts that remain retryable; the next failure is terminal. `0` disables retry (first failure terminal) |

## Independent counters

Each `MikrotikScheduledJob` tracks:

- `ConnectionFailureCount`
- `AuthenticationFailureCount`

A successful authenticated connect resets **both** counters. Categories do not share a single counter.

## Behaviour

1. Classify failure (`MikrotikConnectFailureClassifier` / login exceptions).
2. If under the maximum for that category, set `NextRetryUtc = now + delay`, requeue the **same** job entry (not a new job).
3. `Remaining` / import outstanding count is **not** decremented on retry.
4. Terminal CSV columns (`UpgradeCompleted` / `Result`) are **not** written on retry — only on eventual terminal outcome.
5. `UpgradeStarted` is not written during connect/login retries (upgrade executor has not reached the start boundary).

## Restart

Delayed retries live only in memory. Process restart discards them. Operators must re-import CSVs if work should continue.

## Related

- [Scheduler](scheduler.md)
- [CSV import](csv-import.md)
