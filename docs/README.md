# Documentation index

Operator and developer documentation for MikroTik Upgrader. The root [README](../README.md) is the primary entry point; these pages expand specific subsystems.

When documentation and code disagree, **trust the code** under `src/MikrotikWorker/` and update the docs.

## Operations and design

| Document | Purpose |
|---|---|
| [architecture.md](architecture.md) | Host composition, components, concurrency, durability |
| [configuration.md](configuration.md) | Configuration keys and defaults |
| [csv-import.md](csv-import.md) | CSV format, result columns, activation and cancellation |
| [scheduler.md](scheduler.md) | In-memory scheduler queues, capacity, import accounting |
| [retries.md](retries.md) | Connection, authentication, and firmware-transport retries |
| [routeros-upgrade.md](routeros-upgrade.md) | Upgrade lifecycle, staging vs install, gates |
| [firmware.md](firmware.md) | Package catalogue, HTTP distribution, staging verification |
| [backup-and-ospf.md](backup-and-ospf.md) | Backup gate, OSPFv2/v3, Loopback, management, backhaul |
| [build-and-release.md](build-and-release.md) | Build, test, Native AOT publish |
| [deployment.md](deployment.md) | Windows/Linux deployment checklist |
| [troubleshooting.md](troubleshooting.md) | Common failure modes |

## Standards and historical reports

Point-in-time audits and remediation write-ups live under [standards/](standards/README.md). They retain historical findings; current operational behaviour is described in the subsystem documents above.
