# Backup and OSPF behaviour

## Backup

Implemented primarily by `DeviceBackupService` and upgrade-path orchestration.

- Pre-upgrade backup is **blocking** for stages that proceed to fetch/reboot.
- Artefacts land under `FirmwareUpgrade:BackupDirectory` (default `Backups/`), resolved from `AppContext.BaseDirectory`.
- Binary `.backup` files may use `FirmwareUpgrade:BackupPassword` when configured.
- Transfer settings: `ScpPort` (22) and `ScpTimeout` configure SSH.NET **SFTP** download (historical `Scp*` names).
- Central backups are not auto-deleted on success; protect with filesystem ACLs.
- Backup content is never served by the firmware HTTP listener.

Failure to create/verify/cleanup backups stops that device’s upgrade path for the job (terminal failure) — no fetch/reboot after a failed mandatory backup.

## OSPF migration

OSPF planning/apply/verify lives under `src/MikrotikWorker/Upgrade/Ospf/`.

- Plans are captured and validated fail-closed when the upgrade path requires migration.
- Apply/verify occurs after RouterOS 7 verification on the applicable path.
- Optional root setting `RedistributeSummaryviaStatics` installs distance-250 blackhole statics and enables static redistribution when `true`, after successful OSPF verification and before RouterBOARD assessment. The shipped `appsettings.json` currently sets this to `true`; confirm the intended value for each environment.
- This workaround must not be described as producing Type-3 area-range summaries; Type-5 external LSA content is not independently inspected.

Lab-only OSPF helpers (`OspfLabTarget`, recovery runners, Tommy harnesses) are for controlled lab/tests and are gated separately from production CSV upgrades.

## Related

- [RouterOS upgrade](routeros-upgrade.md)
- [Configuration](configuration.md)
