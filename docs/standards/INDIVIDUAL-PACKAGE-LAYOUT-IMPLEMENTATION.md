# Combined and Individual RouterOS Package Layout Support

> **Historical remediation report** (2026-11-17). Documents the production defect and the layout-support fix. Current behaviour: [../firmware.md](../firmware.md).

**Date:** 2026-11-17  
**Branch:** `master`  
**Production defect:** `PackageResolutionFailed` on individual-package routers (Koperfontein `10.16.0.9`, Thirsty Falls `10.16.0.10`)

---

## A. Root cause

`UpgradePackageResolver.Resolve` required an independently installed combined main package (`routeros` / `routeros-{arch}`). Routers with the legitimate legacy **individual** installation model expose independent packages such as `system`, `routing`, `wireless`, … with **zero** `bundle=` parents and **no** combined `routeros` entry.

Production inventory (Koperfontein):

```text
Installed packages: gps, ntp, ppp, ups, dhcp, ipv6, lora, mpls, hotspot, security,
  advanced-tools, tr069-client, calea, user-manager, wireless, system, openflow,
  multicast, routing
Bundled components: 0
```

Combined-package assumptions that rejected this layout:

| Location | Assumption |
|---|---|
| `UpgradePackageResolver` | Hard fail `MissingMainPackage` when no `routeros`/`routeros-{arch}` |
| Failure text | “Expected an unbundled 'routeros' or 'routeros-{arch}' entry” |
| `PostUpgradePackageVerifier` | Required an independent main `routeros*` after every upgrade |
| Diagnostics | Did not distinguish “no combined package” from “no compatible package set” |

Assessment still passed (catalogue had `routeros-*-{target}.npk` for the architecture). Failure occurred at upgrade-stage resolution before download/transfer/reboot.

---

## B. Design

Resolver now classifies installation layout from independent inventory:

| Layout | Anchor | Behaviour |
|---|---|---|
| `Combined` | Independent `routeros` / `routeros-{arch}` | Existing behaviour: select main + other independents; ignore bundled components |
| `Individual` | Independent `system` (no combined main) | Select a catalogue `.npk` for each independent package |
| Neither | — | Fail closed with a clear “no compatible package set” message |

`UpgradePackageResolution` exposes `Layout` and `Diagnostics`. The executor logs layout, package count, and diagnostics before backup/fetch.

---

## C. Package selection

1. **Architecture:** normalised device architecture; never cross-arch; mismatch messages name expected vs found.  
2. **Version:** only catalogue packages whose parsed version equals the stage target.  
3. **Combined:** unchanged — one `.npk` per independent install.  
4. **Individual (same major family, e.g. v6→6.49.23):** require catalogue matches for every independent package (`system-…`, `routing-…`, …). Incomplete sets fail closed.  
5. **Individual → release without `system` (typical v6→v7):**  
   - Core transitions to architecture-matched `routeros` when `system` is not distributed for the target.  
   - Independents that still have catalogue `.npk` files are selected.  
   - Independents with **no** catalogue entry for that version/arch are **absorbed** (not downloaded) with a diagnostic — catalogue presence is the evidence; no invented dependency graph.  
6. **Optional vs required:** required = every currently independent install that still has a catalogue artefact (or the `system`→`routeros` core). Optionals never installed on the device are not pulled.

---

## D. Pipeline changes

| Stage | Change |
|---|---|
| Discovery / resolution | Layout-aware selection; improved failure diagnostics |
| Download / HTTP hosting | Unchanged — still fetches each `ResolvedUpgradePackage` URL |
| Transfer | Unchanged loop over resolved set; still refuses reboot if any fetch/size check fails |
| Post-upgrade verify | Combined still requires `routeros*`; individual `system` core verifies without combined `routeros`; each required package still checked for name+version |
| OSPF / backup / stages | Untouched |

---

## E. Files changed

| File | Role |
|---|---|
| `src/.../FirmwarePackageLayout.cs` | New layout enum |
| `src/.../UpgradePackageResolver.cs` | Combined + individual resolution |
| `src/.../PostUpgradePackageVerifier.cs` | Layout-aware core verification |
| `src/.../RouterOsV6UpgradeExecutor.cs` | Log layout / diagnostics |
| `src/.../PackageCompatibilityAuditor.cs` | Treat `system` as core for MissingMain mapping |
| `src/.../PackageResolutionFailureKind.cs` | Clarified MissingMainPackage docs |
| `tests/.../UpgradePackageResolverTests.cs` | Individual/combined/arch/version/v7-transition tests |
| `tests/.../PostUpgradePackageVerifierTests.cs` | Individual verify + Scenario A/B resolve→verify |
| `docs/firmware.md` | Document both layouts |
| `docs/standards/INDIVIDUAL-PACKAGE-LAYOUT-IMPLEMENTATION.md` | This report |
| `global.json` | `rollForward` → `latestMajor` (host lacked SDK 10.0.403; 11.0.100 present) |

---

## F. Tests

| Run | Result |
|---|---|
| Package resolver + verifier (+ related upgrade filters) | **107 passed** |
| Full non-live suite (initial) | **1273 passed**, **1 failed** — `ImportedCsvPathRegistrationTests` timeout flake |
| Isolated CSV / concurrency flake retries | **Passed** each time when run alone |
| Scheduling suite alone | **134 passed** |
| Non-scheduling suite under load | Occasional flake in `Reconnection_AtFourMinutesFiftySeconds_SucceedsBeforeDeadline` (timing); **not a package-layout regression** |
| Focused package acceptance | Individual layout no longer fails solely for missing combined `routeros` |

Honest disclosure: the full parallel non-live run was not green on every attempt due to pre-existing scheduling/timing flakes. Package-focused and scheduling-isolated runs passed; flakes were re-run in isolation and passed.

---

## G. Build and publish

| Step | Result |
|---|---|
| Release build | Succeeded (0 warnings / 0 errors) |
| Windows Native AOT | Succeeded (`release/win-x64/MikrotikWorker.exe`) |
| Linux Native AOT | Succeeded (`release/linux-x64/MikrotikWorker`, Zig/AotAnywhere) |

---

## H. Safety review

- No live MikroTik connections or firmware operations from this task  
- Backup gate / fetch-before-reboot sequence unchanged  
- No credentials logged in the new diagnostics  
- No OSPF or scheduler behaviour changes (CSV flakes are pre-existing timing tests)  
- No router-IP / site special cases  

---

## I. Outstanding limitations

1. **Absorption rule** for individual→`routeros` transitions relies on catalogue absence for the target version/arch. Packages renamed across majors (for example `lora` → another family) are not remapped; they are absorbed/skipped with a diagnostic rather than failing the upgrade.  
2. Post-upgrade verification does **not** assert absorbed packages disappeared or became bundled — only required resolved packages are checked.  
3. Assessment still reports catalogue `routeros` compatibility for the architecture; it does not itself classify individual vs combined inventory (resolution does).

---

## J. Git state

- HEAD observed during validation: `cb2d60a` (`master`)  
- No commits or pushes performed for this task  
- Working tree includes these package-layout changes (uncommitted) plus any pre-existing local edits  
- `global.json` `rollForward` set to `latestMajor` so the host can build without SDK `10.0.403` (SDK `11.0.100` installed)  

**Acceptance:** A router with a legitimate individual-package installation must no longer fail with `PackageResolutionFailed` merely because no combined RouterOS package exists — covered by resolver tests using a Koperfontein-like `system` inventory.
