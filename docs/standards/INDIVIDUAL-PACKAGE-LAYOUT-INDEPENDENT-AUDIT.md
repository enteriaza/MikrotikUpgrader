# Independent Audit — Combined and Individual RouterOS Package Resolution

> **Historical independent audit.** Findings reflect the codebase at audit time. Current package-layout behaviour: [../firmware.md](../firmware.md).

**Date:** 2026-11-17  
**Auditor stance:** Code and tests are authoritative; implementation report is secondary.  
**HEAD audited:** `cb2d60af1f02fa13edda5e444b040c37a7b254ff` (`master`)  
**Constraint compliance:** No live MikroTik operations; no commits or pushes.

---

## A. Verdict

**PASS WITH FINDINGS**

Both combined and individual layouts resolve correctly on the production execution path. One confirmed fail-closed gap (mixed independent `routeros` + `system`) was fixed. Absorption of extras present only for other architectures during system→routeros remains a documented catalogue limitation (not a defect against MikroTik’s real FirmwareFiles distribution). Pre-existing CSV/scheduling timing flakes appeared in the full suite and are unrelated to package resolution.

---

## B. Root cause and implementation assessment

### Original production defect (confirmed)

Device logs (`BR01.POP009.016.Kopefontein`, arm, individual `system` inventory, Bundled=0) failed at `UpgradePackageResolver` with:

```text
No main combined RouterOS package was found.
Expected an unbundled 'routeros' or 'routeros-{arch}' entry.
```

Failure occurred before backup fetch / transfer / reboot. Assessment had already marked packages Compatible against catalogue `routeros-*-{target}.npk`.

### Current implementation assessment

The uncommitted package-layout work correctly:

| Requirement | Assessment |
|---|---|
| Combined layout preserved | Yes — inventory-driven `FirmwarePackageLayout.Combined` |
| Individual `system` layout without combined `routeros` | Yes — `ResolveIndividual` |
| Arch/version exact matching | Yes — normalised arch; `policyCatalog` filtered to target version |
| Incomplete individual set (same major family) fails closed | Yes |
| v6→v7 `system`→`routeros` core transition | Yes — catalogue absence of `system` for target |
| Pipeline uses full resolved set | Yes — executor loops `resolution.Packages`; reboot only after all size checks |
| Verifier supports both cores | Yes — `requiresRouterOsMain` vs `requiresSystemCore` |
| No router-IP / site special cases | Yes |

Layout classification is based on **source inventory** (independent `routeros*` vs `system`), not on inventing a target layout label. Target artefacts still come exclusively from the target-version catalogue. After a system→routeros transition, `Layout` remains `Individual` (source layout) while selected packages and verifier expectations follow the target core — intentional and covered by tests.

---

## C. Package selection

### Evidence used

1. **Independent installs only** (`!IsBundledComponent`) — bundled peers never get their own `.npk`.
2. **Required set** = every independent install that still has an arch+version catalogue match (or the `system`→`routeros` core override).
3. **Optional** = packages never independently installed are never pulled (no “download every `.npk`”).
4. **Absorption (transition only)** = independent install with **no** arch-matched artefact for the target version while core transitions to `routeros`. Catalogue presence is the only evidence; no invented dependency graph.

### Checked-in FirmwareFiles evidence (v7)

- `system-*.npk` exists for 6.49.23; **absent** for 7.23.8 / 7.25.1.
- `routeros-7.23.8-{arch}.npk` present for fleet arches.
- Extras still distributed for arm on 7.23.8 include `wireless`, `gps`, `ups`, `user-manager`, `tr069-client`, `openflow`, `calea`, …
- `hotspot-7.23.8-smips.npk` exists **only** for smips — for arm this is absorbed during transition with an other-arch diagnostic.

Koperfontein reproduction tests against real `FirmwareFiles` select 19 packages for v6 stepping and the correct v7 subset (routeros + remaining extras; routing/dhcp/hotspot absorbed).

---

## D. v6 → v7 transition

1. Source layout identified from inventory (`system` independent, no `routeros`).
2. Target core: if `system` unavailable for device arch on target version and `routeros` is available → core override `routeros`.
3. Target packages never carry source `.npk` paths; each row is a fresh catalogue match.
4. Packages with no target artefact are absorbed with diagnostics (including “present for: …” when other arches exist).
5. Missing target core (`system` and `routeros` both unavailable for arch) fails before transfer.

**Limitation (open, documented):** during transition, a package missing for this arch but present for another arch is absorbed. That matches MikroTik’s hotspot/smips distribution, but an **incomplete** multi-arch catalogue missing e.g. `wireless-*-arm.npk` while retaining `wireless-*-mipsbe.npk` would also absorb. Completeness cannot be proven beyond catalogue contents.

---

## E. Execution pipeline

| Stage | Implementation | Individual-layout behaviour |
|---|---|---|
| Catalogue discovery | `FirmwarePackageCatalog.GetPackages` scans `FirmwareFiles/**/*.npk` | Shared |
| Resolution | `UpgradePackageResolver.Resolve` | Combined / Individual / fail |
| Local validation | Catalogue size/SHA from disk; relative URL path | Shared |
| HTTP hosting | Kestrel static `/firmware_files`, `.npk` only | Unchanged |
| Transfer | `FetchUrlToFileAsync` per resolved package + size wait | All required packages |
| Partial failure | Any fetch/size failure → `UpgradeFailed`, **no reboot** | Covered by multi-package test |
| Backup gate | Before fetch | Unchanged |
| Verify | `PostUpgradePackageVerifier.Verify` | system or routeros core + each required row |

No separate CDN download subsystem — firmware is pre-provisioned under `FirmwareFiles` and served locally.

---

## F. Findings

### F1 — Mixed independent `routeros` + `system` accepted as Combined (confirmed defect — **fixed**)

- **Severity:** Medium  
- **File/method:** `UpgradePackageResolver.Resolve`  
- **Evidence:** Prior code preferred Combined whenever `mainPackages.Count == 1`, ignoring co-installed independent `system`.  
- **Consequence:** Contradictory inventory could select both layouts’ artefacts on v6.  
- **Fix:** Fail closed with `MultipleMainPackages` and explicit “mixes combined and individual” message.  
- **Test:** `Resolve_MixedCombinedAndIndividualCores_FailsClosed`

### F2 — Other-arch extras during transition (limitation — **documented, not changed to fail-closed**)

- **Severity:** Low (limitation)  
- **File/method:** `UpgradePackageResolver.ResolveIndividual` absorption branch  
- **Evidence:** Real catalogue has `hotspot-7.23.8-smips.npk` only; Koperfontein arm must absorb hotspot on v7. Fail-closed on other-arch presence would block legitimate upgrades.  
- **Change:** Diagnostics now include `present for: {arches}` when absorbing. Non-transition individual layout still fails architecture mismatch.  
- **Test:** `Resolve_IndividualToV7_ExtraPresentOnlyForOtherArch_IsAbsorbedWithDiagnostic` + real FirmwareFiles v7 test

### F3 — Duplicate resolved identities (hardening — **fixed**)

- **Severity:** Low  
- **File/method:** `FailOnDuplicateSelections`  
- **Evidence:** Audit required duplicate identity detection; previously absent.  
- **Fix:** Fail `AmbiguousPackage` if two selected rows share `ExpectedInstalledPackageName`.

### F4 — Missing coverage for production inventory / partial multi-package transfer (coverage — **added**)

- Real FirmwareFiles Koperfontein v6 + v7 resolution tests  
- Determinism / ambiguous catalogue / missing both cores  
- Verifier incomplete individual + individual→v7 resolve→verify  
- Executor: first package OK, second fetch fails → no reboot  

### F5 — Pre-existing scheduling timing flakes (unrelated — **open**)

- Full Release suite: **1284 passed / 1 failed** — `ImportedCsvResultRetryIntegrationTests.RetryExhaustion_WritesTerminalFailure_WithBlankStart` (timeout)  
- Isolated retry: **passed**  
- Scheduling filter later: `ImportedCsvPathRegistrationTests.JobsHeldUntilAcceptedPathRegistered_ThenUpgradeStartedPersists` timed out once; isolated retry **passed**  
- Not package-layout regressions

---

## G. Changed files (this audit remediation only)

| File | Why |
|---|---|
| `src/.../UpgradePackageResolver.cs` | Mixed-layout fail-closed; duplicate selection guard; richer absorption diagnostics |
| `tests/.../UpgradePackageResolverTests.cs` | Audit regressions + real FirmwareFiles Koperfontein scenarios |
| `tests/.../PostUpgradePackageVerifierTests.cs` | Individual→v7 verify; incomplete/wrong-version cases |
| `tests/.../FirmwareUploadStorageModelTests.cs` | Multi-package second-fetch failure blocks reboot |
| `docs/standards/INDIVIDUAL-PACKAGE-LAYOUT-INDEPENDENT-AUDIT.md` | This report |

No OSPF, scheduler, backup-policy, or credentials changes.

---

## H. Validation

| Run | Result |
|---|---|
| Focused resolver+verifier+storage | **55 passed** |
| Broader package pipeline filter | **94 passed** |
| Full non-live suite (Release) | **1284 passed, 1 failed** (CSV retry timeout flake) |
| Isolated flake retries | **Passed** |
| Release build | **Succeeded** (0 warnings / 0 errors) |
| Windows Native AOT | **Succeeded** (`release/win-x64/MikrotikWorker.exe`, ~26.6 MB) |
| Linux Native AOT | **Succeeded** (`release/linux-x64/MikrotikWorker`, ~23.6 MB; Zig/AotAnywhere) |

Honest disclosure: the full parallel suite did **not** report a clean pass on the first attempt due to scheduling flakes. Package-focused runs were green.

---

## I. Safety

- No live router connections or firmware operations from this audit  
- No commits or pushes  
- Existing uncommitted OSPF and package work preserved  
- Backup gate / fetch-before-reboot unchanged  
- No secrets added to diagnostics  
- No OSPF or scheduler behaviour changes  

---

## J. Git state

- Branch: `master`  
- HEAD: `cb2d60af1f02fa13edda5e444b040c37a7b254ff`  
- Working tree: dirty (prior OSPF work + package layout + this audit remediation)  
- No commits or pushes performed  

---

## Baseline map (methods)

| Concern | Primary types |
|---|---|
| Catalogue discovery | `FirmwarePackageCatalog.GetPackages` / `FirmwarePackageFileNameParser` |
| Layout classification | `UpgradePackageResolver.Resolve` → Combined / Individual |
| Arch/version matching | `ArchitectureAcceptable`, version-filtered `policyCatalog` |
| Required/optional selection | Independent inventory ∩ catalogue; never-installed = not selected |
| v6→v7 core transition | `ResolveIndividual` system availability → `routeros` override |
| Download/local validation | Local `FirmwareFiles` scan (size/SHA); no remote CDN |
| Static hosting | `MikrotikWorkerHost` `/firmware_files` |
| Transfer / reboot | `RouterOsV6UpgradeExecutor` package loop → reboot |
| Post-upgrade verify | `PostUpgradePackageVerifier.Verify` |

**Acceptance:** A legitimate individual-package installation no longer fails with `PackageResolutionFailed` merely because no combined `routeros` package exists. Proven against production Koperfontein inventory shapes and the checked-in firmware tree.
