# Finalisation — Target-Release-Driven RouterOS Package Selection

**Date:** 2026-11-17  
**HEAD:** `cb2d60af1f02fa13edda5e444b040c37a7b254ff` (`master`)  
**Constraints:** No live MikroTik operations; no commits or pushes.

---

## A. Verdict

**PASS**

Package selection is now target-release-driven. Source packages that MikroTik no longer distributes separately for the target version/architecture produce diagnostics and do not fail resolution. Missing or ambiguous **target cores** still fail closed. Transfer and verification safety remain intact. Full non-live suite, Release build, and both Native AOT publishes succeeded.

---

## B. Package-selection policy

1. Filter the firmware catalogue to the **exact target RouterOS version**.
2. Classify source inventory layout (combined `routeros*` vs individual `system`; mixed cores fail closed).
3. Establish the **target core** from the catalogue:
   - Combined source → require arch-matched `routeros`.
   - Individual source → use arch-matched `system` when shipped; otherwise transition to arch-matched `routeros` (target layout `Combined`).
4. For each other independent source package:
   - If exactly one arch-matched target artefact exists → **Selected**.
   - If same name exists only for other arches → **NotDistributedForTargetArchitecture** (skip).
   - If absent for all arches in the target release → **NotDistributedInTargetRelease** (skip).
   - If ambiguous → **Rejected** / fail closed.
5. Never download packages for another architecture or another release.
6. Never treat the source install list as a mandatory target package set.

---

## C. Discontinued packages

Proven by tests:

- Source `hotspot` / `routing` / `lora` / `user-manager` / `tr069-client` absent from target → resolution succeeds; package not selected.
- Koperfontein-shaped inventory against real `FirmwareFiles` for 7.23.8 selects `routeros` + still-distributed extras; discontinued packages are diagnostics only.
- Executor no longer returns `PackageResolutionFailed` solely because an optional is absent from the catalogue.

---

## D. Architecture differences

- Packages present only for another architecture (e.g. `hotspot-7.23.8-smips.npk` on an `arm` device) → `NotDistributedForTargetArchitecture` with `otherArchitectures=[smips]`; not selected; not fatal.
- Core package architecture mismatch still fails closed (Case E).
- Selected set is asserted arch- and version-homogeneous in resolution tests.

---

## E. Core layout

| Source | Target catalogue | Target layout | Core |
|---|---|---|---|
| Combined `routeros*` | `routeros` for arch | Combined | `routeros` |
| Individual `system` | `system` for arch | Individual | `system` |
| Individual `system` | no `system`, has `routeros` | Combined | `routeros` |
| Both independent cores | — | fail | — |
| No usable target core | — | fail | — |

`FirmwarePackageLayout` on the resolution result now reflects the **target** layout after core selection.

---

## F. Transfer and verification

Unchanged safety chain:

1. Resolve selected target packages only.
2. Backup gate before fetch.
3. Fetch each selected package; size-check each; any failure → no reboot.
4. Multi-package second-fetch failure still blocks reboot (existing test).
5. `PostUpgradePackageVerifier` checks only **resolved** required packages (combined or `system` core + selected independents). Discontinued source packages are not required post-upgrade.

---

## G. Findings

### Confirmed defects (fixed)

1. **Source-mandatory selection** — Combined and non-transition Individual paths failed when a source independent lacked a target artefact. Fixed: absence is a diagnostic unless the package is the required target core.
2. **Misleading “absorption only on v7 transition”** — Same discontinuation rules now apply whenever the target catalogue lacks a separate counterpart.
3. **Layout semantics** — After system→routeros, layout is reported as `Combined` (target), not source Individual.

### Missing coverage (addressed)

- Structured dispositions (`PackageResolutionDisposition` + `PackageResolutionDiagnostic`).
- Combined discontinued optional; combined→v7 discontinued routing; individual same-family arch-exclusive hotspot; auditor Compatible-with-diagnostic expectations; executor discontinued-optional fetch.

### Genuine limitations (open)

- Catalogue cannot prove “incorporated into routeros” vs “operator omitted the `.npk`”. Absence from the target catalogue for this arch remains the authoritative installability signal; an incomplete multi-arch tree can under-select extras. This matches the product rule that the target release defines what is installable.

---

## H. Changed files

| File | Reason |
|---|---|
| `src/.../UpgradePackageResolver.cs` | Target-driven selection; skip discontinued/arch-exclusive; target layout |
| `src/.../PackageResolutionDisposition.cs` | New dispositions + diagnostic model |
| `src/.../FirmwarePackageLayout.cs` | Document target-layout semantics |
| `src/.../PackageCompatibilityAuditor.cs` | Compatible stages include target-difference diagnostics |
| `src/.../PackageCompatibilityAuditModels.cs` | Clarify legacy `MissingOptionalPackage` |
| `tests/.../UpgradePackageResolverTests.cs` | Policy/disposition regressions + FirmwareFiles cases |
| `tests/.../PackageCompatibilityAuditorTests.cs` | Compatible + diagnostic expectations |
| `tests/.../RouterOsV6UpgradeExecutorTests.cs` | Discontinued optional does not block fetch |
| `docs/firmware.md` | Document target-authoritative policy |
| `docs/standards/TARGET-RELEASE-PACKAGE-SELECTION-FINALISATION.md` | This report |

No OSPF, scheduler, backup-policy, or credentials changes.

---

## I. Validation

| Run | Result |
|---|---|
| Focused package/executor/auditor | **102 passed** |
| Full non-live suite (Release) | **1287 passed**, 0 failed |
| Release build | **Succeeded** (0 warnings / 0 errors) |
| Windows Native AOT | **Succeeded** (`release/win-x64/MikrotikWorker.exe`, ~26.6 MB) |
| Linux Native AOT | **Succeeded** (`release/linux-x64/MikrotikWorker`, ~23.7 MB; Zig/AotAnywhere) |

---

## J. Safety and Git state

- No live router operations  
- No commits or pushes  
- Existing uncommitted work preserved  
- No unrelated OSPF or scheduler changes  
- Branch: `master`  
- HEAD: `cb2d60af1f02fa13edda5e444b040c37a7b254ff`  
- Working tree: dirty (package-layout + this finalisation + prior local edits)

**Acceptance:** The target release determines the installable package set. Discontinued and architecture-exclusive source packages do not cause `PackageResolutionFailed`. Missing target core still fails closed.
