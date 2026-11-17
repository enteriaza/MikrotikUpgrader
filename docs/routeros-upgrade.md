# RouterOS upgrade lifecycle

Primary executor: `RouterOsV6UpgradeExecutor`. Orchestration/preflight: `RouterJobExecutor`.

## Independent gates

| Gate | Effect when false |
|---|---|
| `Mikrotik:Enabled` | Jobs fail closed without opening API connections |
| `FirmwareUpgrade:Enabled` | Connect may occur for assessment paths, but upgrade is administratively disabled and jobs end **Failed** (never Succeeded) |

`FirmwareUpgrade:AdvertisedBaseUrl` must be a router-reachable HTTP base ending such that `/tool/fetch` can retrieve packages (documented in README as ending in `/firmware_files`). Empty advertised URL is rejected by validators when upgrades are enabled.

## High-level sequence (eligible device)

Exact ordering is implemented in `RouterOsV6UpgradeExecutor` (with branching for already-on-target, skip v6, etc.). The successful full path is approximately:

```mermaid
flowchart TD
  A[Connect + login] --> B[Inventory / assess]
  B --> C{Policy / window OK?}
  C -->|no| Block[Terminal Blocked/Failed]
  C -->|yes| D[Record UpgradeStarted]
  D --> E[Legacy FleetUpgrade cleanup]
  E --> F[v6 stage if needed: backup + fetch + reboot + verify]
  F --> G[OSPF plan]
  G --> H[v7 stage: backup + fetch + reboot + verify]
  H --> I[Post-v7 backup]
  I --> J[OSPF apply/verify]
  J --> K[Optional static redistribute]
  K --> L[RouterBOARD assess]
  L --> M{UpgradeRequired?}
  M -->|yes| N["/system/routerboard/upgrade + reboot + verify"]
  M -->|no| O[Terminal Succeeded]
  N --> O
```

Exact branching for versions already on 6.49.23+, already on RouterOS 7, AlreadyUpToDate, etc. is implemented in the executor and covered in the root README tables.

## UpgradeStarted boundary

`UpgradeStarted` is persisted **after** connect, preflight, and policy gates succeed, and **before** mutation work (legacy cleanup / backup / fetch). Connection/authentication retries that never reach this boundary leave `UpgradeStarted` blank.

## Package staging vs destructive stages

Within each package stage (v6 stepping or RouterOS 7):

1. Resolve packages → mandatory backup → fetch/verify **all** packages → **then** reboot/install → reconnect → verify.
2. A transient fetch/incomplete-size failure returns `TransientTransportFailure` with `TransportRetryEligible=true` and **does not** reboot, apply OSPF, or run RouterBOARD.
3. Permanent package/config failures stay terminal (`UpgradeFailed`, resolution failures, etc.).
4. After a stage reboot has started, failures are **not** treated as ordinary firmware-transport retries for that stage.
5. `RebootInitiated` records that a reboot occurred (Conn₀ dispose semantics). A **prior** verified stage reboot does not by itself block transport requeue of a later pre-install fetch failure (`TransportRetryEligible`).

Scheduler retry policy (10 attempts / 10-minute Connection delay, shared `ConnectionFailureCount` for transport): [retries.md](retries.md). Staging rules: [firmware.md](firmware.md).

Retries re-enter `ExecuteAsync` with a fresh connection. Progress relies on the device’s **current** version/inventory (for example a device already on 6.49.23 skips v6 stepping). Do not describe blind re-execution of already completed destructive OSPF/RouterBOARD work after a pre-install fetch failure.

## Terminal outcomes

Scheduler terminal states map to CSV `Result` strings via `ImportedCsvResultColumns.FormatResult`. Only **Succeeded** and **AlreadyUpToDate** mean the device is on the configured RouterOS 7 target.

## Backup and OSPF

- Pre-upgrade backup is mandatory for upgrade stages that fetch/reboot.
- OSPF migration is fail-closed when required by the path. **OSPFv3 removal is mandatory and unconditional** during OSPF migration (see [backup-and-ospf.md](backup-and-ospf.md)).
- Optional `RedistributeSummaryviaStatics` runs after successful OSPF verification and before RouterBOARD assessment when enabled.
- Post-v7 backup precedes OSPF apply on the applicable path (not the reverse). Central backup verification must complete before OSPF **mutation**.

## RouterBOARD (RouterBOOT) firmware

After RouterOS package stages (and optional static redistribution), `RouterboardFirmwareUpgradeService`:

1. Assesses RouterBOOT via `RouterboardFirmwareAssessor` (`current-firmware` vs `upgrade-firmware`).
2. When **NotRequired** / conclusive success without upgrade, completes the RouterBOARD stage without issuing the API command.
3. When **UpgradeRequired**, calls `IMikrotikRouterOperations.UpgradeRouterboardAsync` → `MikrotikApiClient` issues **`/system/routerboard/upgrade`** (RouterBOOT loader only, not a RouterOS package install).
4. Reboots, then polls reconnect using **`FirmwareUpgrade:ReconnectInterval`** until **`FirmwareUpgrade:RebootTimeout`**, and verifies `current-firmware` advanced.

Compatibility assessment logging that mentions RouterBOOT versions does **not** itself issue `/system/routerboard/upgrade`; only this dedicated stage does when required.

## Related

- [Backup and OSPF](backup-and-ospf.md)
- [Firmware](firmware.md)
- [CSV import](csv-import.md)
