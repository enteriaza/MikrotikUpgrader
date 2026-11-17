# Troubleshooting

## CSV rejected

- Read `Rejected/*.error.txt` for total and retained errors.
- Common causes: missing headers, `executionmode` present, bad IP, expired window, mismatched per-row windows, culture/format mismatch.

## CSV stuck in Processing

- Startup logs warn about leftover Processing files; they are **not** auto-imported.
- Inspect completeness, then move to Incoming manually.

## CSV stuck in Accepted after cancel

- Incomplete activation leaves Accepted intentionally (not Completed).
- Check logs for `Import activation finalized` / `Import activation incomplete`.
- Re-import manually only after accepting possible repeat work on already-finished rows.

## Jobs not connecting

- `Mikrotik:Enabled` must be true.
- Shared credentials, API port 8728, and network path must work.
- Review connection vs authentication retry counters and `NextRetryUtc` in logs.

## UpgradeStarted blank but Result failed

- Failure occurred before the UpgradeStarted boundary (connect/preflight/policy), or the job never scheduled.

## Firmware fetch failures

- Confirm Kestrel listening and firewall.
- Confirm `AdvertisedBaseUrl` is reachable **from the router**.
- Confirm NPK names match architecture/version rules.
- Transient API/HTTP transport interruptions may be **requeued** (scheduler FirmwareTransport / Connection budget). Check logs for `NextRetryUtc` and `ConnectionFailureCount`.
- Permanent errors (404, missing local package, catalogue/resolution failure) are terminal — not requeued as transport.
- Incomplete or wrong-size staged files never reboot; see [firmware.md](firmware.md) and [retries.md](retries.md).

## Process restarted mid-run

- In-memory jobs and retries are gone.
- Inspect device version, staged packages, Backups/, and device logs.
- Re-import CSV only after operator decision (may repeat work).

## Live tests unexpectedly running

- Default test filter excludes live categories. Do not override the filter and set live env gates unless intentionally authorised.

## Related

- [CSV import](csv-import.md)
- [Retries](retries.md)
- [Deployment](deployment.md)
