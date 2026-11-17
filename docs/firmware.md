# Firmware management and HTTP distribution

## On-disk catalogue

Source tree: repository `FirmwareFiles/` (copied beside binaries on build/publish).

| Path | Role |
|---|---|
| `FirmwareFiles/mikrotik/version` | Plain-text RouterOS 7 target (single version line). Used by the worker process; **not** HTTP-served. |
| `FirmwareFiles/mikrotik/*.npk` | Architecture-specific packages for 6.49.23 and the v7 target. These are the only files the HTTP endpoint will stream. |

Relative `FirmwareFiles:Path` resolves from `AppContext.BaseDirectory`.

## Fixed vs dynamic versions

- **v6 stepping target** `6.49.23` is a code constant (`RouterOsUpgradeVersions`), not an appsettings key.
- **v7 target** is loaded from `mikrotik/version` and watched for valid updates (running jobs keep their immutable snapshot).

## Kestrel mapping (`.npk` only)

Configured in `MikrotikWorkerHost.ConfigureMiddleware`:

- Listener: `Listening:IpAddress` / `Listening:Port` (default `0.0.0.0:8080`).
- URL prefix: `/firmware_files` mapped to the configured firmware root on disk.
- Content type map is cleared; only `.npk` → `application/octet-stream` is registered.
- `ServeUnknownFileTypes = false`.
- An `OnPrepareResponse` guard returns **404** for any non-`.npk` file name (defence in depth).
- Therefore `mikrotik/version`, manifests, and other non-`.npk` artefacts under the firmware root are **not** exposed over HTTP.

Routers must reach `FirmwareUpgrade:AdvertisedBaseUrl` (not localhost from the router’s perspective), typically ending so that `/tool/fetch` can request package URLs under `/firmware_files/...`. Windows hosts typically need an inbound firewall rule for the listen port.

Backups are **not** under FirmwareFiles and are **not** served.

## Package resolution rules

Summarised from production resolvers/assessors:

- **Target catalogue is authoritative:** source inventory identifies layout and candidate independents; it is not a mandatory target package list
- **Combined layout:** target core is `routeros` / `routeros-{arch}`; independents are selected only when still distributed for the target version/architecture
- **Individual layout:** target core is `system` when the release ships it; no combined `routeros` is required
- **v6 → v7:** when `system` is not distributed for the target, core becomes architecture-matched `routeros` (layout Combined)
- Source packages discontinued in the target release, or shipped only for another architecture, produce diagnostics (`NotDistributedInTargetRelease` / `NotDistributedForTargetArchitecture`) and are not downloaded
- Stages never mix RouterOS 6 and 7 packages in one resolved set
- Missing/ambiguous **target core**, mixed architectures/versions in the selected set, and duplicate identities fail closed
- Absence of a combined `routeros` package is not by itself a resolution failure when a legitimate individual `system` installation is present

## Related

- [RouterOS upgrade](routeros-upgrade.md)
- [Deployment](deployment.md)
