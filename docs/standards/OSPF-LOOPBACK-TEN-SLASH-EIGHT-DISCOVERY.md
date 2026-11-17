# OSPF Loopback Address Discovery Correction (`10.0.0.0/8`)

> **Historical remediation report.** Documents the change from the prior narrower Loopback scope to `10.0.0.0/8`. Current Loopback and address-plan rules: [../backup-and-ospf.md](../backup-and-ospf.md).

## Verdict

**PASS**

Loopback `/32` discovery and validation now accept any eligible local Loopback address in `10.0.0.0/8`. The previous `10.16.0.0/24` infrastructure-scope filter is removed. Literal Loopback IP router-id, `default-v2`, OSPFv3 removal, and fail-closed ambiguity behaviour are preserved.

---

## Baseline

| Item | Value |
|---|---|
| Branch | `master` |
| HEAD | `bd92fa161dbdd9aa82d3f9c84a07405bb3e80472` |
| Working tree at start | Clean (prior firmware/transport work already committed) |

---

## Old `10.16.0.0/12` / `10.16.0.0/24` assumptions

The task mentioned `10.16.0.0/12`. The **actual code** restricted Loopback identity to **`10.16.0.0/24`** via `OspfLoopbackRouterId.InfrastructureScopeCidr`.

| Location | Old assumption | Resolution |
|---|---|---|
| `OspfLoopbackRouterId.InfrastructureScopeCidr` | `10.16.0.0/24` | Changed to `10.0.0.0/8` |
| Error strings in `Derive` | “in-scope (10.16.0.0/24)” | Updated to `10.0.0.0/8` |
| XML/docs on `OspfLoopbackRouterId`, `OspfModels`, `OspfLiveIdentityResolver` | BR identity range `10.16.0.0/24` | Updated to `10.0.0.0/8` |
| `docs/backup-and-ospf.md` | Loopback `/32` in `10.16.0.0/24` | Policy updated |
| `docs/standards/OSPF-CONSOLIDATION-CLEANUP-REMEDIATION.md` | Same | Updated |
| Tests asserting `10.255.0.1` outside scope | `LoopbackDerive_RequiresTenSixteenZeroSlash24Scope` | Renamed/rewritten for `10/8` |
| Fixture narrative (`OspfFleetFixtureMatrixTests`) | “sole 10.16.0.0/24 address” | Updated wording; behaviour unchanged for non-10 extras |

**Unrelated `10.16.*` references** (management `/24` derivation, backhaul `/30`s, site templates, lab IPs) were **not** changed.

---

## Exact Loopback eligibility

A candidate must satisfy **all** of:

1. Assigned on a supported Loopback interface name (exact, case-insensitive): `lo`, `Loopback`, `Loopback-OSPF`.
2. Inventory-eligible (enabled; disabled/unknown fail closed / excluded).
3. Parseable IPv4 host `/32` (explicit `/32` or RouterOS bare host form normalised to `/32`).
4. Host belongs to **`10.0.0.0/8`**.

Rejected: non-`/32`, non-`10/8`, missing interface association, bridge/VLAN/transport/physical interfaces, substring names like `bridge-loopback` / `lo0`.

Outside-`10/8` Loopback addresses are ignored for identity (not deleted). Multiple distinct in-scope hosts without unique corroborating OSPF router-id → fail closed.

---

## Router-ID reconciliation

- Planner calls `OspfLoopbackRouterId.Derive(eligible.IpAddresses, instance?.RouterId)`.
- Live identity resolver corroborates with `plan.RouterId`.
- Corroborator must be a **literal dotted-quad IPv4** matching exactly one in-scope Loopback host.
- Symbolic `/routing/id` names are resolved separately (`ResolveLegacyRouterIdValue`) for comparison/notes; they do not invent a preferred subnet.
- Final applied/verified OSPFv2 router-id remains the **literal Loopback IP**.
- Instance name remains **`default-v2`**.

Preserved: when a single Loopback is selected and the instance router-id text differs, planning still notes normalisation to the Loopback literal (existing behaviour).

---

## Validation

| Run | Result |
|---|---|
| Focused Loopback / identity / Rustenburg | **61 passed** |
| Non-live OSPF (`Upgrade.Ospf` excluding Live traits) | **569 passed** |
| Full non-live Release suite (default live exclusions) | **1319 passed** (clean re-run) |
| Earlier full-suite attempt | **1318 passed / 1 failed** — `ImportedCsvResultRetryIntegrationTests.SuccessfulUpgrade_RecordsStartAtUpgradeBoundary` (timing flake; **passed** on isolated retry) |
| Release build | **Succeeded** (0 warnings / 0 errors) |
| Windows Native AOT | **Succeeded** (`release/win-x64/MikrotikWorker.exe`, ~26.6 MB) |
| Linux Native AOT | **Succeeded** (`release/linux-x64/MikrotikWorker`, ~23.7 MB; Zig/AotAnywhere) |

---

## Files changed

| File | Reason |
|---|---|
| `src/.../Ospf/OspfLoopbackRouterId.cs` | Scope `10.0.0.0/8`; messages/docs |
| `src/.../Ospf/OspfModels.cs` | Comment scope |
| `src/.../Ospf/OspfLiveIdentityResolver.cs` | Comment scope |
| `tests/.../OspfLoopbackRouterIdTests.cs` | Range/interface/corroboration coverage |
| `tests/.../OspfProductionCompatibilityPolicyTests.cs` | Policy regression renamed for `10/8` |
| `tests/.../OspfFleetFixtureMatrixTests.cs` | Narrative update |
| `docs/backup-and-ospf.md` | Authoritative policy |
| `docs/standards/OSPF-CONSOLIDATION-CLEANUP-REMEDIATION.md` | Policy line |
| `docs/standards/OSPF-LOOPBACK-TEN-SLASH-EIGHT-DISCOVERY.md` | This report |

---

## Remaining limitations

- Completeness still depends on `/ip/address` interface association; missing/ambiguous Loopback association fails closed (by design).
- Cryptographic address integrity is not applicable; eligibility is inventory + CIDR + interface name.
- Management-prefix and backhaul logic remain on their existing `10.16.*` site/backhaul conventions — intentionally unchanged.

## Safety

- No live RouterBoard operations  
- No commits or pushes  
- No OSPF planner redesign; no new config flags  
