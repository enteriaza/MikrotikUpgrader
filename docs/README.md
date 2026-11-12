# Documentation index

Operator and developer documentation for MikroTik Upgrader. The root [README](../README.md) is the primary entry point; these pages expand specific subsystems.

| Document | Purpose |
|---|---|
| [architecture.md](architecture.md) | Host composition, components, concurrency, durability |
| [configuration.md](configuration.md) | Configuration keys and defaults |
| [csv-import.md](csv-import.md) | CSV format, result columns, activation and cancellation |
| [scheduler.md](scheduler.md) | Scheduler queues, capacity, import accounting |
| [retries.md](retries.md) | Connection and authentication retry policy |
| [routeros-upgrade.md](routeros-upgrade.md) | Upgrade lifecycle and gates |
| [firmware.md](firmware.md) | Package catalogue and HTTP distribution |
| [backup-and-ospf.md](backup-and-ospf.md) | Backup and OSPF stages |
| [build-and-release.md](build-and-release.md) | Build, test, Native AOT publish |
| [deployment.md](deployment.md) | Windows/Linux deployment checklist |
| [troubleshooting.md](troubleshooting.md) | Common failure modes |

All statements are intended to reflect the current implementation under `src/MikrotikWorker/`. When docs and code disagree, trust the code and update the docs.
