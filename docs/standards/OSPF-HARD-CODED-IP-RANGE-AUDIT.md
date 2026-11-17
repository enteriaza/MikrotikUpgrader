# OSPF Hard-Coded IP Range and Address-Plan Dependency Audit

**Audit type:** Read-only investigation  
**Report file (sole permitted write):** `docs/standards/OSPF-HARD-CODED-IP-RANGE-AUDIT.md`  
**No production code, tests, or other documentation were modified.**

---

## Executive summary

| Item | Assessment |
|---|---|
| **Verdict** | **PASS WITH FINDINGS** |
| Hard-coded IP ranges present? | **Yes** — several intentional fleet conventions plus protocol constants |
| Unintended restrictions? | **No residual `10.16.0.0/24` or `/12` Loopback filter** in production paths. **Management discovery is still gated to `10.0.0.0/8`**, which is an intentional but address-plan-dependent restriction |
| Address-plan independent? | **No** — not fully. Loopback and management eligibility require `10.0.0.0/8`; site consolidation folds CGNAT/`172.16/12` into fleet aggregates; backhaul requires exact `/30` + PTP topology evidence (not a `10.16.*` prefix). Lab recovery pins Tommy-specific `10.16.*` addresses |
| Highest-risk findings | **P1:** Management `/24` derivation rejects non-`10/8` management addressing (`OspfSiteManagementSummary.TenDotZero`). **P1:** Active backhaul must be exact `/30` (rejects `/31`). **P2:** Fleet aggregates `100.64.0.0/10` and `172.16.0.0/12` force consolidation of any site matcher inside those ranges |

The OSPF migration is **topology-driven for backhaul classification** (OSPF PTP / Full neighbour + interface–network association) but **not allocation-agnostic for Loopback or management**, both of which require hosts inside `10.0.0.0/8`. That matches the documented Loopback policy; management’s identical `10/8` gate is a separate network-design convention that will reject otherwise-valid `*_MGMT` addresses in `172.16/12` or `192.168/16`.

---

## Complete address/range inventory

Production decision-controlling findings first; then lab/test/docs.

| Address / Range | File and line | Symbol / Method | Classification | Purpose | Production impact |
|---|---|---|---|---|---|
| `10.0.0.0/8` | `OspfLoopbackRouterId.cs:47` | `InfrastructureScopeCidr` / `IsInfrastructureScopeHost` / `Derive` | **B** Network-design convention (documented Loopback policy) | Eligible Loopback `/32` hosts for OSPF router-id | Rejects Loopback outside 10/8; selects among in-scope hosts |
| `10.0.0.0/8` | `OspfSiteManagementSummary.cs:28` | `TenDotZero` / `Derive` (~L134–138, ~L354) | **B** Network-design convention | Only interface addresses in 10/8 can form management `/24` candidates / corroboration hints | **Rejects** `*_MGMT` addresses in 172.16/192.168/etc.; planning fails if no 10/8 mgmt `/24` |
| `100.64.0.0/10` | `OspfSiteMatcherConsolidation.cs:25` | `SharedAddressSpace` / `FoldPrefix` (~L230–238) | **B** Fleet PPPoE/ACS aggregate convention | Fold any site matcher inside CGNAT space into canonical aggregate | Consolidation/cleanup treat covered matchers as superseded by `100.64.0.0/10` |
| `172.16.0.0/12` | `OspfSiteMatcherConsolidation.cs:28` | `Private172Aggregate` / `FoldPrefix` (~L241–249) | **B** Fleet inactive-PPPoE aggregate convention | Fold site matchers under RFC1918 172.16/12 into canonical aggregate | Same as above for `172.16.0.0/12` |
| `0.0.0.0` | `OspfMigrationPlanner.cs:34`, `OspfFleetNaming.cs:36` | `BackboneAreaId` | **A** OSPF protocol / RouterOS backbone area-id | Identify backbone area | Missing/ambiguous backbone fails planning |
| Prefix length `/32` | `OspfLoopbackRouterId.cs` (~L314–317), planner/safety | Loopback host requirement | **B** Design + OSPF router-id practice | Loopback must be host route | Non-`/32` Loopback fails derivation |
| Prefix length `/30` | `OspfBackboneSafety.cs:69–72`, `OspfMigrationPlanner.cs:1036–1040` | Backhaul validation / neighbour classify | **B** Design convention (PTP transport) | Active backhaul network must be exact `/30` | `/31` or other lengths rejected |
| Prefix length `/24` | `OspfSiteManagementSummary.cs:147–148` | Mask to `/24` aggregate | **B** Management summarisation rule | Collapse subdivided mgmt into `/24`; do not supernet distinct `/24`s | Controls area-range + site matcher injection |
| Cost `10` | `OspfMigrationPlanner.cs:30` | `FleetBackhaulCost` | **B** Fleet OSPF cost convention | Normalise/require backhaul cost | Non-10 costs normalised or fail ensure/verify |
| Type `ptp` / `point-to-point` | Planner / backbone safety / cleanup | `IsPointToPoint`, type checks | **B** Topology role marker | Backhaul role evidence | Non-PTP not classified as active backhaul |
| Comment `MGMT` | `OspfFleetNaming.cs:44` | `ManagementRangeComment` | **B** Naming convention | Tag management area-ranges | Corroboration / ensure MGMT ranges |
| Site area-id = Loopback RID | `OspfMigrationPlanner.cs:371–377` | Plan validation | **B** Fleet OSPF area-id convention | Site area-id must equal Loopback IP | Mismatch fails planning |
| Lab pins `10.16.0.11`, `10.16.11.0/24`, `10.16.248.77`, … | `OspfOnlyLabRecoveryRunner.cs` (~L435–991) | Tommy lab recovery checks | **C** Site/lab-specific | Pin expected Tommy plan/neighbour | **Lab-only path**; not fleet migration |
| Example `10.16.0.11` in comments | `OspfLoopbackRouterId.cs:284`, ops comments | Comments only | **D** Illustrative | Documentation in code | None |
| Test fixtures (`10.16.*`, `172.16.*`, …) | `tests/.../Upgrade/Ospf/*` | Fixtures | **D** Test-only | Synthetic inventory | Do not control production; many encode 10.16 fleet shape |
| Docs examples | `docs/backup-and-ospf.md`, standards docs | Narrative | **D** Documentation | Policy/examples | Policy now states Loopback `10.0.0.0/8` |

**Not found in production OSPF eligibility:** `10.16.0.0/12`, `10.16.0.0/16`, or `10.16.0.0/24` as a remaining Loopback or management gate (only historical mention in Loopback XML that the scope is *not* restricted to `10.16.0.0/24`).

**IPv6:** No hard-coded `fc00::/7`, `fe80::/10`, or `::1` in OSPF IPv4 planning. OSPFv3 objects are inventoried for **mandatory removal**; static-summary stage explicitly rejects IPv6 area ranges. Consolidation obsolescence can reason about IPv6 containment for cleanup of residual templates, but IPv4 management/backhaul/Loopback paths dominate decisions.

---

## Detailed findings (production-relevant)

### F1 — Loopback scope `10.0.0.0/8`

- **Location:** `OspfLoopbackRouterId.InfrastructureScopeCidr` (`OspfLoopbackRouterId.cs:47`), used in `Derive` (~L128–132).
- **Purpose:** Authoritative Loopback router-id eligibility (documented policy after Loopback remediation).
- **Consumers:** `OspfMigrationPlanner.Build`, `OspfLiveIdentityResolver`, verification that plan/live RID match Loopback.
- **Behaviour:** Only supported Loopback interface names (`lo` / `Loopback` / `Loopback-OSPF`) + `/32` + host in `10.0.0.0/8`. Outside-range Loopbacks ignored; multi in-scope without unique OSPF router-id corroboration fails closed.
- **Legitimately restrictive?** Yes — matches current documented policy.
- **Other allocations:** Loopbacks outside 10/8 rejected; within 10/8 accepted (e.g. `10.200.0.1`).
- **Disposition:** **Retain** (documented convention). Not a residual `10.16/24` bug.

### F2 — Management discovery also gated to `10.0.0.0/8`

- **Location:** `OspfSiteManagementSummary.TenDotZero` (`OspfSiteManagementSummary.cs:28`); exclude outside-10/8 at ~L134–138; corroboration hints also require `TenDotZero` (~L352–354).
- **Purpose:** Derive site management `/24` set from enabled interface addresses on/near `*_MGMT`, collapsing `/26`/`/27` into `/24`, excluding Loopback/`/30`/backhaul/PPPoE.
- **Evidence:** File header and failure text explicitly require addresses “in 10.0.0.0/8”.
- **Consumers:** Planner before consolidation (`OspfMigrationPlanner.cs` ~L308–348); MGMT area-range ensure; verification of management summarisation.
- **Behaviour:** A valid `Bridge-VLAN-99_MGMT` with only `172.20.30.1/24` contributes **zero** candidates → planning error “No eligible site management /24…”.
- **Legitimately restrictive?** Intentional fleet convention, **not** required by the structural `*_MGMT` rules alone. Distinct from the `/24` consolidation rule.
- **Disposition:** **Document as address-plan dependency**; if cross-RFC1918 management is ever required, **parameterise or replace gate with topology-only evidence** (not authorised in this audit).

### F3 — Fleet aggregates `100.64.0.0/10` and `172.16.0.0/12`

- **Location:** `OspfSiteMatcherConsolidation.SharedAddressSpace` / `Private172Aggregate` (`OspfSiteMatcherConsolidation.cs:25–28`); `FoldPrefix` (~L230–249); provenance messaging in `OspfLiveSupersededProvenance.cs:296`.
- **Purpose:** Collapse PPPoE/ACS (CGNAT) and inactive 172.16-style customer matchers into canonical `interfaces=all` aggregates.
- **Behaviour:** Any site-matcher prefix inside those ranges folds to the aggregate; superseded identities drive KnownLegacy cleanup.
- **Impact:** A site that intentionally advertised a more-specific `100.64.x.0/24` matcher is folded; management `/24` fold still prefers longest-prefix area-range / MGMT comment when not under those aggregates.
- **Disposition:** **Retain as documented fleet convention**; do not confuse with management eligibility.

### F4 — Backhaul exact `/30` + PTP (not `10.16.*`)

- **Location:** `OspfBackboneSafety.ValidatePlanInvariants` (~L69–72); neighbour fallback `TryClassifyBackhaulFromNeighbourEvidence` (`OspfMigrationPlanner.cs:1036–1040`); ensure/verify cost/type.
- **Purpose:** Role ActiveBackhaul = non-passive, backbone area, `ptp`, cost 10, exact `/30`, unique interface–network mapping.
- **Discovery mechanism:** OSPF network/template/interface inventory + Full neighbour evidence; **not** “address in 10.16.248.0/…”.
- **Impact:** `/31` PTP or `/29` transport rejected. Address family of the `/30` can be any IPv4 (including `192.168` or `100.64`) if topology evidence matches.
- **Disposition:** **Retain** unless `/31` support is a future requirement (**parameterise prefix-length policy**).

### F5 — Backbone area-id `0.0.0.0` and site area-id = Loopback IP

- **Location:** `BackboneAreaId`; planner L371–377.
- **Purpose:** OSPF backbone identity; fleet site area-id equals router-id (Loopback).
- **Disposition:** **Retain** (protocol + fleet naming). Site area-id equality is a design convention, not an IP-range filter.

### F6 — Tommy lab hard-pins (`OspfOnlyLabRecoveryRunner`)

- **Location:** Multiple literals e.g. `10.16.0.11`, `10.16.11.0/24`, `10.16.248.77`, `100.99.76.16/28`, `172.26.197.4/30` (~L435–991).
- **Purpose:** Lab recovery/apply gates for one known device.
- **Impact:** Does not constrain fleet migration planner for other routers.
- **Disposition:** **Retain as lab tooling**; ensure operators do not treat it as general policy.

### F7 — Residual `10.16.0.0/24` Loopback restriction

- **Status:** **Absent** from production decision paths after Loopback remediation (`InfrastructureScopeCidr = "10.0.0.0/8"`).
- **Evidence:** Grep of `src/MikrotikWorker` shows no `10.16.0.0/24` eligibility constant; only comment that scope is not that narrower range.

---

## Functional impact analysis

### Loopback / router-id

- Eligibility: interface name + `/32` + **`10.0.0.0/8`** + inventory eligibility.
- Corroboration: literal OSPF instance / plan router-id; `/routing/id` resolved for comparison/notes.
- Final RID: literal Loopback IP; instance name `default-v2`.
- Cleanup/verify: RID must still match Loopback; no `10.16/24` leftover.

### Management

- Structural: `*_MGMT` naming, exclude `/30`, Loopback, PPPoE, disabled; `/26`/`/27` → `/24`; no supernetting distinct `/24`s; ambiguity fails closed.
- **Additional hard gate:** host must be in **`10.0.0.0/8`**.
- Not gated to `10.16.0.0/12` or `/16`.

### Backhaul

- Topology + OSPF role evidence; cost 10; type ptp; **exact `/30`**.
- No production “must be 10.16.248.0/…” classifier found.

### Areas / ranges

- Backbone `0.0.0.0`; site area-id = Loopback RID; MGMT-commented `/24` area-ranges from derived set; fleet aggregates for customer/PPPoE matchers.

### Cleanup / verification

- Classification uses planned identities, superseded provenance, and role checks (cost/type/passive).
- Folding into `100.64/10` or `172.16/12` can mark more-specific matchers legacy — by design for those spaces.
- Hard-coded ranges do **not** appear to delete templates solely because they are “not 10.16”; misclassification risk is mainly **management fail-closed** (no plan) or **aggregate fold** inside CGNAT/172.16.

---

## Hypothetical scenario results

### Scenario A

| Role | Address | Outcome |
|---|---|---|
| Loopback | `10.200.0.1/32` | **Accept** (`10/8` + Loopback name) |
| Management | `10.50.20.0/24` | **Accept** for `/24` derivation (in 10/8; still needs `*_MGMT` / fail-closed uniqueness) |
| Backhaul | `10.60.100.0/30` | **Accept** if PTP/Full evidence maps uniquely; not blocked by 10.16 |

### Scenario B

| Role | Address | Outcome |
|---|---|---|
| Loopback | `10.100.10.1/32` | **Accept** |
| Management | `172.20.30.0/24` | **Reject for management derivation** — `TenDotZero` excludes outside-10/8 → planning fails without a 10/8 mgmt `/24` |
| Backhaul | `192.168.100.0/30` | **Acceptable to backhaul classifier** if OSPF PTP/neighbour evidence exists (no 10.16 prefix requirement); overall migration still fails without management |

### Scenario C

| Role | Address | Outcome |
|---|---|---|
| Loopback | `10.255.255.254/32` | **Accept** |
| Management | `192.168.200.0/24`, `172.25.10.0/24` | **Both excluded** by `10.0.0.0/8` management gate |
| Backhaul | `100.64.10.0/30` | **Acceptable as `/30` backhaul** via topology; if the same prefix appeared as a **passive site matcher**, `FoldPrefix` would fold it into **`100.64.0.0/10`** |

---

## Findings and recommendations

| ID | Priority | Finding | Recommendation (not implemented) |
|---|---|---|---|
| F2 | **P1** | Management discovery hard-requires `10.0.0.0/8` | Document as fleet allocation dependency; if multi-RFC1918 mgmt is required, replace CIDR gate with topology-only rules |
| F4 | **P1** | Backhaul requires exact `/30` (no `/31`) | Document; parameterise only if `/31` PTP is a real fleet need |
| F3 | **P2** | Automatic fold into `100.64/10` and `172.16/12` | Document; confirm all customer/PPPoE matchers are intended to consolidate |
| F5 | **P2** | Site area-id must equal Loopback IP | Document as fleet OSPF naming; not an IP-range filter |
| F6 | **P3** | Tommy lab hard-pins | Keep isolated; do not copy into fleet policy |
| F1 | **P3** | Loopback `10/8` | Retain (authoritative policy) |
| — | — | Residual `10.16/24` Loopback | **None found** in production |

No **P0** safety finding: hard-coded ranges examined do not show a path that deletes arbitrary templates solely for “wrong private range”; fail-closed planning is the dominant effect of management `10/8`.

---

## Validation and audit limitations

**Searched**

- Entire `src/MikrotikWorker/Upgrade/Ospf` production tree (planner, Loopback, management, consolidation, backbone safety, reconciler, cleanup, ops, live identity, lab runner).
- Broader `src/MikrotikWorker` for `10.16.0.0/{12,16,24}` eligibility leftovers.
- Configuration projects (no OSPF CIDR options found).
- Spot-check of tests/docs for fixture vs production distinction.

**Methods**

- Regex/literal search for IPv4/IPv6/CIDR forms, `ParseRequired`, prefix-length comparisons, and known fleet constants.
- Call-path reading for Derive → Build → FoldPrefix → ensure/verify/cleanup.

**Excluded / limited**

- Generated `bin`/`obj`, NuGet packages, tik4net internals.
- Exhaustive listing of every test fixture IP (classified as **D** in aggregate).
- Live RouterOS captures — neighbour/PTP classification behaviour inferred from code only.

**Uncertainty requiring operator confirmation**

- Whether management **must** remain inside `10.0.0.0/8` forever, or whether that gate should eventually follow `*_MGMT` topology alone.
- Whether any production sites use `/31` PTP backhauls.
- Whether folding all of `172.16.0.0/12` into one aggregate remains correct for every customer deployment.

---

## Final baseline

| Item | Value |
|---|---|
| Branch | `master` |
| HEAD | `bd92fa161dbdd9aa82d3f9c84a07405bb3e80472` |
| Working tree **before** this audit | Dirty: Loopback `10/8` remediation edits + related tests/docs uncommitted (`OspfLoopbackRouterId*`, `backup-and-ospf.md`, etc.) |
| Working tree **after** this audit | Same uncommitted set **plus** this new report file only |
| File created | `docs/standards/OSPF-HARD-CODED-IP-RANGE-AUDIT.md` |
| Production/tests/docs otherwise modified? | **No** |
| Commits / pushes / live RouterOS? | **None** |

**Bottom line:** The fleet OSPF migrator is **not** tied to `10.16.0.0/12` or `/24` for Loopback anymore, and backhaul identity is **not** inferred from a `10.16.*` transport plan. It **is** still address-plan-dependent on **`10.0.0.0/8` for Loopback and management**, on **`/30` for backhaul**, and on **fleet aggregates `100.64/10` and `172.16/12` for site-matcher consolidation**.
