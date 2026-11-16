# OSPF Migration Completeness Forensic Audit

**Repository:** `MikrotikUpgrader`  
**Branch:** `master`  
**Known HEAD at audit start:** `00873db62a9851d34d323b7bc5066b1fb756e195`  
**Scope:** Read-only forensic audit of RouterOS v7 OSPF migration + optional static summary redistribution  
**Evidence case:** `BR01.POP002.016.Cashane` (`10.16.0.2`), RouterOS `7.23.8`  
**Audit date:** 2026-11-16  
**Constraint:** No code changes, no router commands, no commits/pushes. Prior uncommitted OSPF work in the working tree was preserved and not discarded.

---

## 1. Executive verdict

**Cashane did not complete the full OSPF + static-summary fleet postcondition set.** Final verification failed on a residual eligible template `interfaces=dynamic networks=102.66.242.0/24`, classified as migration-owned KnownLegacy via planner superseded provenance. Because verification failed, the optional static-summary stage never ran. Absence of routing filters, blackhole summaries, and `redistribute=static` in the resulting exports is therefore **expected under current orchestration**, not proof that those features are unimplemented.

| Question | Verdict |
|---|---|
| Are routing filters / blackholes / static redistribute missing from the implementation? | **No** — implemented in `OspfStaticSummaryRedistributionService`, gated after successful OSPF verify. |
| Were they issued on Cashane? | **No** — gated behind verify success; Cashane never reached that stage. |
| Is `redistribute=""` after OSPF apply unexpected? | **No** — apply explicitly clears redistribution; verify requires empty redistribute/filters. |
| Is the dynamic residual incorrectly modelled as migration-owned? | **No for this shape** — prefix is retained on the consolidated `interfaces=all` site matcher; planner provenance correctly marks the source matcher KnownLegacy. |
| Why does the residual remain after “reconciliation commands completed”? | **Not fully proven from exports alone.** Apply success after cleanup + verify KnownLegacy residual is a real lifecycle contradiction that needs log/binary correlation. Leading hypotheses: cleanup classified PreservedUnmanaged while verify matched provenance, or the row was removed then reappeared after cleanup. |
| Does the implementation currently prove complete OSPF migration (topology + summaries)? | **No.** Core OSPF verify is a hard gate; Cashane failed it. Full fleet completeness also requires the optional summary stage when `RedistributeSummaryviaStatics=true`. |
| Did `AlreadyAtTarget` mask incomplete network config? | **No** on this failure path — verify failure returns `OspfMigrationVerificationFailed` before RouterBOARD / already-at-target remapping. |

---

## 2. Required migration postcondition matrix

Authoritative sources: `docs/backup-and-ospf.md`, `docs/configuration.md`, `OspfMigrationPlanner`, `OspfV7Reconciler.ApplyAsync`, `OspfV7MigrationVerifier.VerifyAsync`, `OspfStaticSummaryRedistributionService`, `RouterOsV6UpgradeExecutor`.

| # | Postcondition | Stage | Mandatory? | Authoritative check | Cashane evidence |
|---|---|---|---|---|---|
| P1 | OSPFv2 instance renamed/bound to `default-v2` | Apply + Verify | Yes | `EnsureOspfInstanceNameAsync`; verify live name == `TargetInstanceName`/`default-v2` | Present in export |
| P2 | Literal Loopback-derived router-id on instance | Apply + Verify | Yes | `SetInstanceRouterIdAsync`; loopback RID equality in verify | `router-id=10.16.0.2` |
| P3 | Unconditional OSPFv3 removal | Apply + Verify | Yes | `RemoveOspfV3Async` + `OspfV3ResidualDetector` | Capture/plan said OspfV3=0; export shows only v2 |
| P4 | Areas ensured (backbone + stub site); no-summaries absent | Apply + Verify | Yes | `EnsureOspfAreaAsync`; area type/default-cost/no-summaries checks | backbone + stub site present |
| P5 | Area ranges for management/site summaries | Apply + Verify | Yes | `EnsureOspfAreaRangeAsync`; range presence + `VerifyManagementSummarisationPostconditions` | Three ranges present |
| P6 | Active backhaul templates, cost `10`, non-passive PTP | Apply + Verify | Yes | `EnsureBackhaulTemplateAsync`; verify cost/passive | Five backbone PTP templates; costs 10 where shown |
| P7 | Passive Loopback template (not PTP) | Apply + Verify | Yes | `EnsureLoopbackTemplateAsync` | `Loopback-OSPF` /32 passive present |
| P8 | Consolidated passive site matcher (`interfaces=all`) with required networks | Apply + Verify | Yes | `EnsureSiteNetworkTemplateAsync` + site network membership | Present; includes `102.66.242.0/24` |
| P9 | No residual KnownLegacy / conflicting Unknown templates; eligible count = planned + PreservedUnmanaged | Cleanup + Verify | Yes | `RemoveObsoleteTemplatesAsync`; residual classify in verify | **FAILED** — dynamic residual KnownLegacy; eligible 8 vs expected 7 |
| P10 | Unrelated non-conflicting unmanaged templates preserved | Cleanup + Verify | Yes (safety) | `PreservedUnmanaged` classification | Cashane residual was **not** preserved; classified KnownLegacy |
| P11 | All routing filters removed; instance in/out filter refs cleared | Apply + Verify | Yes **for core OSPF stage** | `RemoveAllRoutingFiltersAsync`; verify `CountRoutingFiltersAsync == 0` | Export `/routing/export` shows no filters — **consistent with core stage** |
| P12 | OSPF redistribute cleared | Apply + Verify | Yes **for core OSPF stage** | `ClearOspfRedistributionAsync`; verify empty redistribute props | `redistribute=""` — **consistent with core stage** |
| P13 | Distance-250 blackhole statics for unique area-range prefixes | Optional summary | Conditional (`RedistributeSummaryviaStatics=true`) | `ReconcileBlackholeRoutesAsync` + `VerifyBlackholeRoutesAsync` | **Not run** (verify gate) |
| P14 | Managed `ospf-v2-out` filter chain + attach as instance `out-filter-chain` | Optional summary | Conditional | `ReconcileOutFilterChainAsync` + attach/verify | **Not run** |
| P15 | Instance `redistribute` includes `static` **after** filter attach | Optional summary | Conditional | `EnableStaticRedistributionAsync` + verify | **Not run**; empty redistribute expected until this stage |
| P16 | Central verified backup before mutation | Pre-apply gate | Yes | Backup gate / executor ordering | Outside this export forensic; assumed for controlled execution |
| P17 | Disabled OSPFv2 objects excluded from decisions | Global | Yes | `OspfInventoryEligibility` | N/A to this residual (enabled) |

**Contract split (critical):**

- **Core OSPF migration success** explicitly requires **zero** routing filters and **empty** redistribute.
- **Fleet summary redistribution** is a **separate later stage** that re-creates filters and enables `redistribute=static`.
- Treating Cashane’s post-OSPF export as evidence that filters/summaries are “missing implementation” confuses the two stages.

---

## 3. Routing filter implementation trace

### Where expected filters are defined

| Concern | Location |
|---|---|
| Core migration clears **all** filters | `OspfV7Reconciler.ApplyAsync` → `OspfMikrotikOperations.RemoveAllRoutingFiltersAsync` |
| Menus | v7: `/routing/filter/rule` and `/routing/filter/select-rule` (never legacy `/routing/filter` table) — comments in `OspfMikrotikOperations` |
| Core verify | `OspfV7MigrationVerifier.VerifyAsync` → `CountRoutingFiltersAsync` must be `0` |
| Summary-stage managed chain | `OspfStaticSummaryRedistributionConstants.OutFilterChainName = "ospf-v2-out"` |
| Accept rule shape | `BuildAcceptRule`: `protocol static && distance == 250 && dst == <cidr> && dst-len == <len> { accept }` then terminal `reject` |
| Chain reconcile/verify | `ReconcileOutFilterChainAsync` / `VerifyOutFilterChainAsync` |
| Attach before redistribute | `AttachOutFilterChainAsync` → instance `out-filter-chain=ospf-v2-out`, then `VerifyOutFilterAttachedAsync` |

### Mandatory?

- **During/after core OSPF migration:** yes — filters must be **absent**.
- **After optional summary stage (when enabled):** yes — exactly the managed `ospf-v2-out` chain must exist and be attached.

### Command result / inventory checks

- Core wipe: remove-by-id loops; verify re-counts filters.
- Summary stage: reconcile → verify chain contents → attach → verify attachment → only then enable redistribute.
- Summary stage **does** re-read instance/filter state after mutations.

### Why Cashane `/routing/export` has no filters

**Confirmed:** core apply deletes all filter rules and clears instance filter references; verify would reject any remaining filters. Cashane never entered the stage that creates `ospf-v2-out`. Export omission is therefore consistent with a completed (or at least filter-cleared) core apply, not with missing filter code.

---

## 4. Static route implementation trace

### Intended prefixes

`OspfStaticSummaryRedistributionService.CollectUniqueAreaRangePrefixes(plan)` — unique IPv4 prefixes from `plan.AreaRanges` (authoritative management/site ranges from the plan, **not** hardcoded Cashane values).

For a Cashane-like plan matching the export, that set would be:

- `100.99.100.0/24`
- `10.16.2.0/24`
- `10.16.22.0/24`

### Blackhole properties

| Property | Value |
|---|---|
| Menu | `/ip/route/add` |
| `blackhole` | flag set (empty parameter) |
| `distance` | `250` (`BlackholeDistance`) |
| Ownership recognition | `IsManagedBlackhole` — blackhole flag true + distance exactly 250 |
| Conflicts | Wrong-distance blackhole or ordinary static/gateway → fail closed |
| Ignored same-dst | connect / dynamic / ospf protocol routes (`IsConflictingRouteForManagedBlackhole` / `IsOrdinaryStaticOrGatewayRoute`) |

### Apply / verify

1. `ReconcileBlackholeRoutesAsync` — add missing managed blackholes  
2. `VerifyBlackholeRoutesAsync` — fresh `/ip/route/print`, require exactly one managed blackhole per prefix  

### Cashane `/ip/route/export`

Shows unrelated statics only; **no** distance-250 blackholes. **Confirmed cause:** optional stage not entered after verify failure. **Not** evidence that blackhole add/API mapping failed on this run.

---

## 5. Redistribution implementation trace

### Intended ROS7 configuration (project design)

Not “set a single property and done.” Ordered contract in `OspfStaticSummaryRedistributionService.ApplyAndVerifyAsync`:

1. Blackhole statics for area-range prefixes  
2. Build/verify `ospf-v2-out` accept/reject rules  
3. Attach `out-filter-chain=ospf-v2-out`  
4. Enable `redistribute` to include `static` (`EnableStaticRedistributionAsync` merges into existing list)  
5. Verify redistribute includes static **and** out-filter still attached  
6. On post-enable failure: compensate by restoring prior redistribute/out-filter snapshot  

### Core OSPF stage (opposite polarity)

`OspfV7Reconciler.ApplyAsync`:

- `ClearOspfRedistributionAsync` → `/routing/ospf/instance/set redistribute=""`
- `ClearOspfInstanceFilterReferencesAsync`
- Verify rejects any non-empty redistribute / in-filter / out-filter

### Why export shows `redistribute=""`

**Confirmed:** expected after core apply. Does **not** prove summary redistribute is missing from the product; proves the summary stage did not run (or was never reached).

---

## 6. Dynamic template classification findings

### Object under investigation

```text
area=BR01.POP002.016.Cashane
interfaces=dynamic
networks=102.66.242.0/24
passive
priority=1
type=ptp
```

### Why classified superseded / KnownLegacy

1. **Consolidation retained the prefix** on the planned site matcher:

   ```text
   interfaces=all
   networks=10.16.2.0/24,10.16.22.0/24,100.64.0.0/10,102.66.242.0/24
   ```

   `OspfSiteMatcherConsolidation.FoldPrefix` returns prefixes unchanged when they are not under fleet aggregates or same-area area-ranges. `102.66.242.0/24` is outside `100.64.0.0/10` and outside the MGMT `/24` ranges, so it is **kept as its own member** of the consolidated list.

2. Source matcher `interfaces=dynamic` **does not** `MatchesPlannedSite` against `interfaces=all` → recorded into `SupersededTemplates` (`ConsolidateWithProvenance`).

3. **`OspfConsolidatedPrefixObsolescence` does not classify exact equals** — only **strict** containment (`PrefixLength` longer than covering). Exact `102.66.242.0/24` under a planned list that also contains `102.66.242.0/24` is **not** obsolete via containment alone.

4. Verify failure text:  
   `Matches migration-superseded complete semantic provenance`  
   → `OspfTemplateCleanupClassifier.TryExplainKnownLegacy` → `MatchesSupersededIdentity` / `OspfTemplateSemanticIdentity.Matches`.

5. Desired-state supersession **may** also apply structurally (`everyLiveEqualsPlannedMember` + specific→all) but **semantic compatibility** requires comment parity with the planned site matcher. Planned consolidated row carries `comment=MGMT`; the live dynamic row has no comment → desired-state path can fail even when provenance succeeds.

### Is the network intentionally retained?

**Yes, by current consolidation rules** — any capture-classified site matcher prefix that does not fold into an aggregate/range is retained on `interfaces=all`. This is generic, not Cashane-specific.

### PreservedUnmanaged bypass?

Docs and tests preserve **unrelated** passive `interfaces=dynamic` public prefixes (typically on backbone / not consolidation sources) as `PreservedUnmanaged` (`OspfVerifierDisabledAndDynamicResidualTests`).

Cashane’s row is **not** that case: same site area + exact prefix retained on consolidated matcher + planner provenance → **KnownLegacy**, not PreservedUnmanaged. Classification is consistent with migration ownership of a consolidated source, not a bypass of preservation policy for unrelated dynamics.

### Why it remains after reconciliation

Apply ends with log: `OSPF v7 reconciliation commands completed; verification required next` **after** `RemoveObsoleteTemplatesAsync`. That method:

- classifies inventory with `ClassifyOrThrow`
- removes every KnownLegacy fingerprint
- re-reads and asserts fingerprint gone
- final inventory must have `KnownLegacy.Count == 0`

So a successful Apply implies: at cleanup finalisation, this fingerprint was **not** KnownLegacy (or was removed and stayed gone through the final cleanup read).

Verify then saw it as KnownLegacy. That is a **lifecycle contradiction**.

| Hypothesis | Status |
|---|---|
| H1. Cleanup classified row PreservedUnmanaged (provenance mismatch on cleanup inventory mapping) while verify matched planner provenance | **Plausible, unproven** — same `TryExplainKnownLegacy` rules; different inventory projection (`MapTemplateInventoryRow` vs `ToCleanupInventoryRow`). Needs Apply logs of preserved vs legacy lists. |
| H2. Row removed successfully, then recreated between cleanup final read and verify inventory | **Plausible, unproven** — would explain Apply success + Verify fail; no in-repo proof ROS recreates `interfaces=dynamic` for retained equal prefixes. |
| H3. Remove command rejected / ignored without detection | **Unlikely under current code** — post-remove `.id` and fingerprint presence checks throw. |
| H4. Verify incorrectly classifies unrelated dynamic as KnownLegacy | **Refuted for this shape** — prefix is on consolidated matcher; provenance message matches design. |
| H5. `PreservedUnmanaged` policy incorrectly deleted the row | **Refuted** — residual still present; failure is verify residual, not accidental delete. |
| H6. Apply used EnrichFromLiveInventory; Verify does not | **Confirmed difference**, but Cashane failure cites **planner** provenance, so Enrich omission is not the matching path for this error text. |
| H7. Executed binary ≠ audited workspace | **Unproven** — working tree has large uncommitted OSPF delta atop `00873db`. |

**Confirmed defect (design gap, independent of H1/H2):** exact-equal consolidation sources depend on brittle provenance/desired-state matching because containment obsolescence excludes equals. If provenance drifts, cleanup may **preserve** a dynamic source while still advertising the same prefix on `interfaces=all` (dual matcher). If provenance matches, cleanup must delete; residual after Apply success indicates an execution/lifecycle gap (H1/H2), not “should have been preserved.”

---

## 7. Verification and completion-semantics findings

### Call chain

```text
RouterOsV6UpgradeExecutor
  └─ OspfMigrationService.ApplyAndVerifyAsync
        ├─ OspfV7Reconciler.ApplyAsync          // mutate; clear filters/redistribute; cleanup
        └─ OspfV7MigrationVerifier.VerifyAsync  // fresh inventory; fail → OspfMigrationVerificationFailed
  └─ RunOptionalStaticSummaryRedistributionAsync  // ONLY if ApplyAndVerify succeeded
        └─ OspfStaticSummaryRedistributionService.ApplyAndVerifyAsync
  └─ RouterBOARD / AlreadyAtTarget remapping     // ONLY after OSPF (+ optional summary) success
```

### Can migration be “complete” with missing filters / summaries / redistribute?

| Item | Core OSPF “Succeeded” | Full fleet with `RedistributeSummaryviaStatics=true` |
|---|---|---|
| Routing filters present | **No** — must be zero | **Yes** — managed `ospf-v2-out` required |
| Blackhole summaries | Not required | Required |
| `redistribute=static` | **Forbidden** (must be empty) | Required (with filter attached) |

Cashane failed **core** verify → neither column is satisfied.

### Does `AlreadyAtTarget` mask incomplete network config?

**No on this path.** `SkippedAlreadyAtTarget` is only assigned after successful OSPF ApplyAndVerify, optional summary gate, and RouterBOARD execution without reboot (`RouterOsV6UpgradeExecutor` already-v7 path). Verify failure returns `UpgradeOutcome.OspfMigrationVerificationFailed` with completed stage still pre-OSPF-success.

### Job outcome clarity

Outcomes distinguish:

- `OspfMigrationApplyFailed`
- `OspfMigrationVerificationFailed`
- summary redistribution failure outcomes from `OspfStaticSummaryRedistributionResult`
- firmware `SkippedAlreadyAtTarget` / `UpgradeSucceeded`

Firmware “already at target” does **not** subsume a failed OSPF verify in the traced code.

---

## 8. Exact file and method references

| Area | File | Methods / types |
|---|---|---|
| Orchestration | `src/MikrotikWorker/Upgrade/RouterOsV6UpgradeExecutor.cs` | `ApplyAndVerifyAsync` call sites; `RunOptionalStaticSummaryRedistributionAsync` |
| Apply gate | `src/MikrotikWorker/Upgrade/Ospf/OspfMigrationService.cs` | `ApplyAndVerifyAsync` |
| Mutating reconcile | `src/MikrotikWorker/Upgrade/Ospf/OspfV7Reconciler.cs` | `ApplyAsync` |
| Verify | `src/MikrotikWorker/Upgrade/Ospf/OspfV7Reconciler.cs` | `OspfV7MigrationVerifier.VerifyAsync`; `ToCleanupInventoryRow` |
| Planner / plan model | `OspfMigrationPlanner.cs`, `OspfModels.cs` | plan fields; `RemoveAllRoutingFilters`, `ClearAllRedistribution` |
| Consolidation | `OspfSiteMatcherConsolidation.cs` | `ConsolidateWithProvenance`, `FoldPrefix`, `MatchesPlannedSite` |
| Containment obsolescence | `OspfConsolidatedPrefixObsolescence.cs` | `TryExplain`, `IsStrictlyContainedBy` |
| Provenance / desired-state | `OspfLiveSupersededProvenance.cs` | `EnrichFromLiveInventory`, `TryExplainLiveSupersession`, `TryExplainStructuralReplacement`, `TryExplainSemanticCompatibility` |
| Classification | `OspfTemplateCleanupClassifier.cs` | `ClassifyOrThrow`, `ClassifyUnplannedResidual`, `TryExplainKnownLegacy` |
| Semantic match | `OspfTemplateSemanticIdentity.cs` | `Matches`, capture/normalise helpers |
| Live remap | `OspfLiveIdentityResolver.cs` | `Resolve`, `RemapPlan` |
| ROS ops | `OspfMikrotikOperations.cs` | `RemoveAllRoutingFiltersAsync`, `ClearOspfRedistributionAsync`, `RemoveObsoleteTemplatesAsync`, `MapTemplateInventoryRow`, ensure/assert helpers |
| Summary stage | `OspfStaticSummaryRedistribution.cs` | `ApplyAndVerifyAsync`, blackhole/filter/redistribute helpers, constants |
| Docs | `docs/backup-and-ospf.md`, `docs/configuration.md` | stage ordering; `RedistributeSummaryviaStatics` |
| Regression anchors | `OspfVerifierDisabledAndDynamicResidualTests.cs`, `OspfStaticSummaryRedistributionTests.cs`, filter/instance clear tests | preserve unrelated dynamic; summary stage behaviour |

---

## 9. Confirmed defects versus unproven hypotheses

### Confirmed

1. **Cashane core OSPF verification failed** on residual KnownLegacy dynamic template; eligible inventory count mismatch (8 vs 7).  
2. **Static summary redistribution did not run** on this execution because it is strictly gated on OSPF verify success.  
3. **Missing filters / blackholes / `redistribute=static` in exports are explained by stage gating + core clear**, not by absence of implementation.  
4. **Core verify intentionally requires empty filters and empty redistribute** — opposite of the summary-stage end state.  
5. **`102.66.242.0/24` is intentionally present on the consolidated site matcher** under FoldPrefix rules; the dynamic source is correctly migration-owned KnownLegacy when provenance matches.  
6. **Exact-equal obsolete sources are not covered by consolidated-prefix containment**; cleanup/verify depend on provenance and/or desired-state semantic compatibility.  
7. **Desired-state semantic compatibility can fail on `comment=MGMT` vs uncommented dynamic** even when networks are exact equals under `interfaces=all`.  
8. **`AlreadyAtTarget` does not mask this verify failure** in `RouterOsV6UpgradeExecutor`.  
9. **Implementation does not prove complete Cashane migration** (core failed; summary never started).

### Unproven (need logs / controlled re-run / binary identity)

1. Whether cleanup classified the Cashane dynamic row as PreservedUnmanaged vs KnownLegacy.  
2. Whether a remove was issued for that `.id`.  
3. Whether RouterOS recreated the template after successful remove.  
4. Whether production binary matched this workspace (large uncommitted OSPF delta exists on `master`).  
5. Whether any API property mapping bug affected cleanup inventory only (no export-only proof).

### Explicitly not claimed

- That static summary redistribution “ran successfully” on Cashane.  
- That `/routing/export` omitting filters means the filter feature is missing.  
- That unrelated dynamic templates should always be deleted.  
- That Cashane-specific hardcoding is required.

---

## 10. Recommended minimal remediation sequence

Do **not** implement in this audit task. Suggested order when remediation is authorised:

1. **Reproduce with logging** of cleanup `KnownLegacy` / `PreservedUnmanaged` descriptors and remove `.id`s for site-area exact-equal dynamics retained on consolidated matchers.  
2. **Close the exact-equal obsolescence gap** generically: treat same-area passive (or disabled) templates whose every network is an **exact member** of a planned `interfaces=all` site matcher as KnownLegacy **without** requiring full semantic provenance, while still preserving dynamics whose networks are **not** members of any planned site matcher.  
3. **Align comment policy** for desired-state vs provenance so `comment=MGMT` on the consolidated target does not silently disable desired-state supersession for uncommented sources (or document that provenance is mandatory for that path).  
4. **Add a post-cleanup / pre-verify residual sweep** that fails Apply (not only Verify) if any planner `SupersededTemplates` fingerprint remains — shrinks the Apply-success/Verify-fail window.  
5. **Only after core verify is green**, re-run with `RedistributeSummaryviaStatics=true` and confirm blackholes + `ospf-v2-out` + `redistribute` containing `static`.  
6. **Do not** weaken PreservedUnmanaged for unrelated dynamics; **do not** add router-specific Cashane conditions.

---

## 11. Tests needed for each confirmed defect / gap

| Gap | Suggested tests |
|---|---|
| Exact-equal retained prefix + `interfaces=dynamic` on **site** area | Classifier: KnownLegacy without relying solely on brittle full provenance when networks ⊆ planned `interfaces=all` members. Verify: residual fails until removed; after remove, verify succeeds with preserved-unmanaged=0. |
| Unrelated dynamic on backbone / non-member prefix | Existing preserve tests must remain green (`OspfVerifierDisabledAndDynamicResidualTests`). |
| MGMT comment on consolidated vs uncommented source | Desired-state vs provenance matrix: document/fix classification when only comment differs. |
| Apply cleanup final vs verify residual | Integration-style fake ops: row present as KnownLegacy at verify if still present after `RemoveObsoleteTemplatesAsync` final report claiming zero legacy — assert Apply throws or cleanup removes. |
| Stage gating | Executor/service test: verify failure ⇒ summary service not invoked; exports-equivalent state keeps empty redistribute/filters. |
| Summary happy path (separate from Cashane residual) | Existing `OspfStaticSummaryRedistributionTests`: blackhole distance 250, connect ignored, filter attach before redistribute, verify read-back. |

---

## Audit integrity statement

- Investigation was **read-only**.  
- **No** RouterOS live commands were issued.  
- **No** application code or router configuration was modified for this audit.  
- **No** git commits or pushes were performed.  
- Prior uncommitted OSPF working-tree changes were left in place.  
- Deliverable added: this document under `docs/standards/`.
