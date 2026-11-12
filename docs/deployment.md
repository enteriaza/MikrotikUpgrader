# Deployment

## Layout beside the executable

```text
MikrotikWorker/
├── MikrotikWorker(.exe)
├── appsettings.json
├── FirmwareFiles/mikrotik/{version,*.npk}
├── ScheduledJobs/{Processing,Accepted,Completed,Rejected}
├── Backups/
└── Logs/{application-*.log,Devices/}
```

Create writable directories for ScheduledJobs, Backups, and Logs for the service account.

## Windows

1. Publish with `tools/Publish-Release.ps1 -Platform win-x64` (or framework-dependent publish).
2. Place real credentials only on the host (not in git).
3. Set `FirmwareUpgrade:AdvertisedBaseUrl` to a URL routers can reach.
4. Allow inbound TCP to `Listening:Port` (default 8080) from management/router networks.
5. Run as console, scheduled task, or Windows Service wrapping the executable (service install is operator-owned; not automated by this repo).

## Linux

1. Publish linux-x64 via the release script (WSL or Zig fallback from Windows).
2. `chmod +x MikrotikWorker`.
3. Ensure the service user can write ScheduledJobs/Backups/Logs.
4. Open the firmware HTTP port in the host firewall.
5. Under systemd, console logs go to the journal; prefer file logs under `Logs/` for persistence.

## Startup validation checklist

- [ ] `Mikrotik:Enabled` intentional
- [ ] Credentials valid for API 8728 and SFTP 22
- [ ] `FirmwareUpgrade:Enabled` and non-empty reachable `AdvertisedBaseUrl`
- [ ] `FirmwareFiles/mikrotik/version` valid RouterOS 7 target
- [ ] Matching NPKs present for 6.49.23 and v7 target architectures in scope
- [ ] Sample CSV validates into Accepted in a lab window
- [ ] Routers can HTTP GET a package from AdvertisedBaseUrl

## Related

- [Build and release](build-and-release.md)
- [Firmware](firmware.md)
- [Troubleshooting](troubleshooting.md)
