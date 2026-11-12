# Build, test, and Native AOT release

## SDK

`global.json` pins SDK `10.0.403` with `rollForward: latestPatch`. Install a system-wide .NET 10 SDK matching that policy.

## Common commands

```powershell
dotnet restore
dotnet build MikrotikUpgrader.sln -c Release
dotnet test tests/MikrotikWorker.Tests/MikrotikWorker.Tests.csproj -c Release
```

The test project’s default VSTest filter excludes live categories (`Category!=Live`, `LiveUpgrade`, `LiveTommyOspfApplyOnly`, `LiveAuditGate`).

Focused example:

```powershell
dotnet test tests/MikrotikWorker.Tests/MikrotikWorker.Tests.csproj --filter "FullyQualifiedName~ImportedCsv"
```

## Framework-dependent publish

```powershell
dotnet publish src/MikrotikWorker/MikrotikWorker.csproj -c Release
```

## Native AOT release script

```powershell
pwsh -NoProfile -File tools/Publish-Release.ps1              # both platforms
pwsh -NoProfile -File tools/Publish-Release.ps1 -Platform win-x64
pwsh -NoProfile -File tools/Publish-Release.ps1 -Platform linux-x64
```

Outputs (gitignored `release/`):

```text
release/win-x64/MikrotikWorker.exe
release/win-x64/appsettings.json
release/win-x64/FirmwareFiles/...
release/linux-x64/MikrotikWorker
release/linux-x64/appsettings.json
release/linux-x64/FirmwareFiles/...
```

### Platform notes

- **win-x64:** host MSVC toolchain for Native AOT.
- **linux-x64 from Windows:** prefers WSL with clang for official same-OS AOT; if unavailable, `StuDev.AotAnywhere` enables Zig cross-link (community path; not Microsoft’s official cross-OS AOT).

### Known publish warnings

CsvHelper and Serilog may emit aggregated trim warnings **IL2104**. The project treats IL2104 as non-fatal under `PublishAot=true`. Project-owned IL2026/IL3050 are not suppressed.

`tools/Publish-Release.ps1` (and the publish-output deployment test) pass `-p:NoWarn=NU1507` so restore/publish succeed on machines with multiple NuGet feeds when central package management would otherwise elevate NU1507.

Native AOT binaries do **not** require installing a .NET runtime on the target host, but still need OS libraries, filesystem permissions, network access, and deployed `appsettings` + `FirmwareFiles`.

## Related

- [Deployment](deployment.md)
- Root [README](../README.md)
