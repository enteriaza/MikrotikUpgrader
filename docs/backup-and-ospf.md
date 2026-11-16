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

### Backup vs OSPF planning (authoritative)

**No OSPF configuration mutation may occur before a centrally verified backup exists.**

Ordering on the production upgrade path:

1. Read-only OSPF capture and plan validation (`CaptureAndValidateAsync`).
2. When the device will enter the RouterOS 7 / OSPF apply path and a verified backup is required, create and centrally verify the backup.
3. Only then run OSPF apply/verify (`ApplyAndVerifyAsync`).

A plan-validation rejection (for example ambiguous management addressing before remediation) terminates the job **without** creating a backup. That is intentional: planning is read-only discovery and does not mutate OSPF. The mandatory safety rule is that **mutation** cannot bypass backup verification — not that every failed planning attempt must produce a backup artefact.

Successful plan → verified backup → OSPF mutation remains the only authorised production mutation sequence.

## OSPF migration

OSPF planning/apply/verify lives under `src/MikrotikWorker/Upgrade/Ospf/`.

- Plans are captured and validated fail-closed when the upgrade path requires migration.
- Apply/verify occurs after RouterOS 7 verification on the applicable path (and after centrally verified backup).
- Optional root setting `RedistributeSummaryviaStatics` installs distance-250 blackhole statics and enables static redistribution when `true`, after successful OSPF verification and before RouterBOARD assessment. The shipped `appsettings.json` currently sets this to `true`; confirm the intended value for each environment.
- This workaround must not be described as producing Type-3 area-range summaries; Type-5 external LSA content is not independently inspected.

Lab-only OSPF helpers (`OspfLabTarget`, recovery runners, Tommy harnesses) are for controlled lab/tests and are gated separately from production CSV upgrades. Every mutating OSPF entry point — including `TommyOspfApplyOnlyHarness` and `OspfOnlyLabRecoveryRunner` — must create a centrally downloaded backup, verify integrity/central status, confirm it belongs to the intended router, and only then begin the first OSPF mutation. A local-only backup does not satisfy the gate.

### Target-state vs legacy-state

Migration exists to move **legacy** configurations into the **target** fleet architecture. Existing routers are not required to already match target naming, router-id literals, template inventories, or preferred costs before mutation.

| Category | Examples | Behaviour |
|---|---|---|
| Hard safety blockers | Missing/ambiguous loopback identity; no unique backbone adjacency; true management `/24` ambiguity; conflicting active unmanaged template; failed ensure/verify; management-plane continuity break | Abort before or during mutation |
| Recoverable legacy deviations | Hostname/`main`/symbolic router-id; instance named `default`; source backhaul cost `11`; non-conflicting unknown templates | Normalise through the migration plan |
| Warnings / preserved configuration | Unrelated passive templates; legacy naming notes; cost normalisation notes | Logged; do not independently block |

### Router ID

- Authoritative identity is the dedicated Loopback `/32` in `10.16.0.0/24` (supported names: `lo`, `Loopback`, `Loopback-OSPF`).
- Target OSPF instance `router-id` is the **literal IPv4 address** from that loopback (for example `10.16.0.4`), never a hostname or symbolic name.
- Legacy values (`main`, hostnames, `/routing/id` references) are migration inputs. They do not block planning.
- Apply sets the literal loopback IP on the OSPFv2 instance and verifies it from fresh live inventory.
- `/routing/id` cleanup is **conservative**: obsolete symbolic entries are retained when a complete non-OSPF reference inventory (for example BGP consumers) cannot be established. Prefer leaving an unused entry over deleting a potentially referenced object.

### OSPFv2 instance naming

- Fleet standard name after migration: `default-v2`.
- Live discovery may bind a sole OSPFv2 instance (or the unique backbone-owning instance) even when it is still named `default`.
- Apply **renames** that OSPFv2 instance to `default-v2`, rebinds its areas by prior `instance=` ownership (addressed by area `.id`), and final verification requires the live name to be `default-v2`.
- Area rebind selects only rows whose original `instance=` equals the previous OSPFv2 instance name. Matching OSPFv3 area names or area-ids never cause OSPFv3 mutation.
- After rebind, affected area rows are read back and must show `instance=default-v2`.
- OSPFv3 instances are **never** selected as the OSPFv2 migration target.
- Multiple ambiguous OSPFv2 instances remain a hard stop.

### OSPFv3 removal (mandatory)

- **All OSPFv3 configuration is removed as part of the OSPF migration. This is mandatory and unconditional.**
- OSPFv3 removal is not optional and is not controlled by a plan opt-in that can leave OSPFv3 in place.
- The OSPFv2 migration target is identified independently; OSPFv3 instances are removal targets only.
- After the verified central backup gate, apply validates the OSPFv3 removal selection, then removes OSPFv3 (templates → ranges → areas → instances) **before** OSPFv2 area/template reconciliation.
- Fresh inventory must show OSPFv3 completely absent (including orphaned/ambiguous leftovers) before OSPFv2 area/template reconciliation continues. Final verification repeats that residual check and fails if any OSPFv3 object remains.
- Inventory completeness is a prerequisite for proving OSPFv3 removal. An empty or incomplete inventory (missing/failed/null instance, area, template, or range collection) is **never** treated as successful removal.

### Backhaul classification

- Prefer active PTP backbone templates/interfaces correlated to a local `/30`.
- Bridged / VLAN / wireless logical interfaces are accepted when Full neighbour evidence uniquely identifies the adjacency.
- Physical Ethernet PTP is not required when the OSPF adjacency is on a valid logical interface.
- Zero or multiple equally evidenced candidates remain a hard stop. Selection is never “first returned”.

### Backhaul cost

- Fleet target cost is always `10`.
- Source costs such as `11` are recorded, normalised during ensure, and verified as `10` after all backhaul ensures complete.
- Mid-mutation backbone asserts check presence / non-passive / enabled / neighbour continuity without requiring every peer backhaul to already be cost `10`.

### Disabled objects (mandatory global rule)

**Disabled RouterOS configuration objects are excluded from all OSPF migration decisions, inference, validation, reconciliation and corroborating evidence.**

- Raw inventory may retain disabled objects for audit diagnostics (for example `Excluded disabled: Bridge-VLAN-197_MGMT=10.16.6.62/27`).
- Eligible inventory — used by every planner, resolver, validator and corroboration path — contains only demonstrably enabled objects.
- Explicitly disabled objects never contribute to candidates, counts, ambiguity, collisions, preservation or deletion evidence.
- Missing or unrecognised `disabled` state fails closed (object is not eligible); it is never silently treated as enabled.
- Dynamic objects remain governed by existing dynamic-object policy and are not confused with disabled.

### Site management `/24`

- Never derive management candidates from `/30` addresses.
- Exclude disabled `/ip/address` rows entirely before deriving prefixes; a disabled `10.16.6.62/27` must never produce a `10.16.6.0/24` candidate.
- Prefer `*_MGMT` interface evidence and MGMT area-range corroboration; collapse `/26`/`/27` management subdivisions into their common `/24`.
- **Same-management-interface expansion:** when an established `*_MGMT` interface carries additional enabled subnets whose `/24` differs from the existing aggregate (for example `10.16.103.254/24` beside subdivided `10.16.3.0/24` on `Bridge-VLAN-99_MGMT`), accept each distinct `/24` as a required management prefix. Do not reject them as ambiguous and do not merge them into a broader supernet.
- **No unrestricted transitive merging:** a shared prefix on two management interfaces must not bridge every other prefix on those interfaces into one domain. Domains are the inclusion-maximal per-interface prefix sets; multiple distinct maximals fail closed unless uniquely corroborated.
- **Authoritative set:** `DerivedManagementCidrs` is the full required management prefix set used for area-ranges, site matchers, verification and supersession. Scalar `DerivedManagementCidr` is the primary aggregate only (compatibility / diagnostics) and must not be the sole membership boundary.
- Multi-prefix supersession treats every derived management `/24` equally when classifying obsolete more-specific templates.
- Genuine ambiguity remains fail-closed when distinct management `/24` domains come from unrelated management interfaces without direct same-interface evidence.
- Diagnostics distinguish existing aggregates, accepted expansion, excluded disabled/transport rows, and genuine ambiguity.
- Address comments (for example `ODU DHCP MGMT Extra`) are operator provenance; inventory address rows do not drive acceptance via comment text.

### Template cleanup

- Delete only positively identified migration-managed / known-legacy superseded templates.
- Same-area templates under a planned consolidated `interfaces=all` matcher are obsolete when every live network is an **exact member** of, or a **strictly more-specific** subnet of, that matcher's consolidated prefixes — provenance is not required for that path.
- Preserve unrelated unmanaged templates (for example passive `interfaces=dynamic` public prefixes that are not consolidation members) when they do not conflict with planned roles.
- An unknown **active** template that overlaps a planned backhaul/site interface remains a hard blocker.
- Final inventory may contain planned-protected rows plus preserved unmanaged rows.

### Verification

Verification re-reads live RouterOS state. Success requires the resulting **eligible** OSPFv2 configuration (literal router-id on the OSPFv2 instance named `default-v2`, areas, templates, cost `10`, preserved unmanaged rows such as non-conflicting passive `interfaces=dynamic` public prefixes, and **no residual OSPFv3 instances/areas/templates/ranges or orphaned leftovers**) — not merely that API commands were accepted. Explicitly disabled OSPFv2 templates are excluded from equality and inventory-count comparison; unknown/malformed administrative state fails closed. There is no automatic resume/replay after process restart.

## Related

- [RouterOS upgrade](routeros-upgrade.md)
- [Configuration](configuration.md)
