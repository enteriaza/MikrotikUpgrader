# OSPF Consolidation Source-Template Cleanup Remediation

**Repository:** `MikrotikUpgrader`  
**Branch:** `master`  
**Known HEAD at start:** `00873db`  
**Date:** 2026-11-16  
**Scope:** Exact-equal consolidation source cleanup (Cashane residual class)

---

## 1. Proven root cause

Cashane left both:

- migration-owned source: `interfaces=dynamic networks=102.66.242.0/24` on the site area  
- consolidated matcher: `interfaces=all` including the same `102.66.242.0/24`

`OspfSiteMatcherConsolidation.FoldPrefix` correctly **retained** the public `/24` on the consolidated matcher (it is outside fleet aggregates and outside MGMT area-ranges). The source template was recorded as superseded provenance when capture semantics matched.

`OspfConsolidatedPrefixObsolescence.TryExplain` previously required **strict containment only** (`IsStrictlyContainedBy`). Exact-equal members returned false, so the provenance-free containment path did not classify the source as KnownLegacy.

Fallback paths were brittle for this shape:

| Path | Cashane-like outcome |
|---|---|
| Consolidated-prefix obsolescence | **Miss** — exact equal ≠ strict containment |
| Desired-state supersession | Structural match OK; **semantic often fails** when consolidated matcher has `comment=MGMT` and the source has no comment |
| Planner provenance | Works only with complete semantic identity match |

Therefore a migration-owned exact-equal source could survive cleanup as `PreservedUnmanaged` (no matching provenance / failed desired-state), while a later verify that *did* match provenance reported KnownLegacy residual — or the duplicate simply remained and failed inventory count. The authoritative defect is the missing exact-equal coverage on the consolidated-prefix obsolescence path (same design intent as strict-containment cleanup, which already deletes without provenance).

Removal/read-back in `RemoveObsoleteTemplatesAsync` was already correct once a row is KnownLegacy. The bug was **classification**, not missing remove/verify plumbing.

---

## 2. Exact-equal versus strict-containment (before / after)

| Live networks vs planned consolidated prefix | Before | After |
|---|---|---|
| `P/27` under planned `P/24` (strict) | KnownLegacy via containment | Unchanged — KnownLegacy via containment |
| `P/24` exact member of planned `…,P/24,…` | Not via containment; depended on provenance/desired-state | KnownLegacy via containment (`exact consolidated member`) |
| Unrelated `Q/24` not a member / not contained | PreservedUnmanaged (if non-conflicting) | Unchanged |
| Same prefix, **different area** | Preserved (area gate) | Unchanged |
| Exact planned `interfaces=all` identity | Protected (not obsolete) | Unchanged |

Helpers added: `IsExactMemberOf`, `IsCoveredByConsolidated`. `IsStrictlyContainedBy` remains strict (equals → false).

---

## 3. Files and methods changed

| File | Change |
|---|---|
| `src/MikrotikWorker/Upgrade/Ospf/OspfConsolidatedPrefixObsolescence.cs` | Cover exact members + strict containment in `TryExplain` / `AllLiveNetworksCoveredByConsolidated` |
| `tests/.../OspfExactEqualConsolidationCleanupTests.cs` | New production-path cleanup + verify regression suite |
| `tests/.../OspfConsolidatedPrefixCleanupTests.cs` | Document exact-member helpers beside strict-containment unit checks |
| `docs/backup-and-ospf.md` | Template cleanup notes exact-member + strict-containment obsolescence |
| `docs/standards/OSPF-CONSOLIDATION-CLEANUP-REMEDIATION.md` | This report |

**Unchanged (by design):** static-summary redistribution, routing-filter wipe/create policy, backup gate, OSPFv3 removal, backhaul cost/guards, disabled-object eligibility.

---

## 4. Cleanup ownership rules (after fix)

A live template is consolidated-prefix obsolete when **all** of:

1. Same area as a planned passive `interfaces=all` site matcher  
2. Not itself the planned site matcher identity (`MatchesPlannedSite`)  
3. Every live network is either an **exact member** of, or **strictly contained** by, a planned consolidated prefix  

Still **not** sufficient for deletion:

- Prefix appears somewhere else but different area  
- Prefix not a member/containment of any planned site matcher (unrelated dynamic)  
- Blanket `interfaces=dynamic`  
- Disabled/unknown admin-state rules unchanged (disabled remain excluded from eligible verify counts; unknown fails closed)

Provenance and desired-state remain additional KnownLegacy paths; containment is no longer dependent on them for exact-equal sources.

---

## 5. Removal and read-back verification behaviour

Unchanged pipeline in `OspfMikrotikOperations.RemoveObsoleteTemplatesAsync`:

1. Classify full inventory (`ClassifyOrThrow`) — abort on Unknown  
2. Remove each KnownLegacy fingerprint by live `.id`  
3. Re-read; fail if `.id` or fingerprint remains  
4. Final classify: KnownLegacy must be 0; planned + preserved counts stable  

`OspfV7MigrationVerifier.VerifyAsync` still fails closed on residual KnownLegacy and inventory-count mismatch. Apply failure still blocks optional static-summary (`RouterOsV6UpgradeExecutor`).

---

## 6. Regression test coverage

`OspfExactEqualConsolidationCleanupTests`:

- Exact-equal dynamic source, **no provenance**, MGMT comment mismatch → remove + verify success  
- Strict containment + unrelated preserved  
- Multiple sources (exact + contained) → all removed; consolidated retains all prefixes  
- Owned dynamic removed; unrelated backbone dynamic preserved  
- Same prefix / different area → only site source removed  
- Interface distinction → planned `all` protected; specific source removed  
- Idempotent second cleanup → zero removes; verify twice  
- Simulated remove failure → cleanup throws; verify does not succeed  

Existing consolidation, provenance, dynamic-preserve, and disabled-eligibility suites remain green.

---

## 7. Full validation results

| Step | Result |
|---|---|
| Focused exact-equal + consolidation/provenance/disabled filters | **126 passed** |
| OSPF non-live (`Category!=Live` / `!=LiveTommyOspfApplyOnly`) | **555 passed** |
| Full non-live suite (initial) | **1260 passed**, **1 failed** — `ImportedCsvPathRegistrationTests.JobsHeldUntilAcceptedPathRegistered_ThenUpgradeStartedPersists` (scheduling timeout flake, unrelated to OSPF) |
| CSV flake retry (isolated) | **Passed** |
| Full non-live suite (final) | **1261 passed** |
| Release build (`dotnet build -c Release`) | **Succeeded** (0 warnings / 0 errors) |
| Windows Native AOT | **Succeeded** (`release/win-x64/MikrotikWorker.exe`, ~26.6 MB; no managed DLL). `pwsh` unavailable on host; equivalent `dotnet publish -p:PublishAot=true -r win-x64` used |
| Linux Native AOT | **Succeeded** (`release/linux-x64/MikrotikWorker`, ~23.6 MB; Zig/AotAnywhere cross-link; no managed DLL). Equivalent `dotnet publish -r linux-x64` used |

Live Tommy harnesses were **not** armed and were excluded (fail-closed without env opt-in; no live router contact).

---

## 8. Established migration invariants

Preserved:

- Mandatory OSPFv3 removal; incomplete inventory cannot prove removal  
- `default-v2` instance; literal Loopback `/32` router-id in `10.0.0.0/8`  
- Management `/24` expansion rules; no supernet merge; disabled/unknown excluded  
- Backhaul cost 10; post-mutation backbone guard  
- Central verified backup before OSPF mutation  
- Unrelated / PreservedUnmanaged retention  
- OSPF verify gate before optional static-summary stage  

---

## 9. Operational integrity

- No live MikroTik operations  
- No production configuration changes  
- No commits or pushes  
- Prior uncommitted work preserved (no reset/clean/stash)  

---

## Claim boundary

Exact-equal source-template cleanup is **fixed and proven by non-live end-to-end cleanup+verify tests**.  

**Do not claim complete OSPF migration for Cashane** until a subsequent controlled RouterOS execution passes the complete core verification gate (and optional summary stage if enabled).
