# Standards and historical reports

These documents are **point-in-time** design, remediation, or audit write-ups. They retain historical findings and the decisions made at the time they were authored.

**Do not treat them as the sole current operator manual.** For present-day behaviour, prefer:

| Topic | Current guide |
|---|---|
| Package layouts and staging | [../firmware.md](../firmware.md) |
| Scheduler transport retries | [../retries.md](../retries.md) |
| Upgrade stage safety | [../routeros-upgrade.md](../routeros-upgrade.md) |
| OSPF migration and address conventions | [../backup-and-ospf.md](../backup-and-ospf.md) |
| Documentation map | [../README.md](../README.md) |

## Inventory

| Document | Kind | Context |
|---|---|---|
| [INDIVIDUAL-PACKAGE-LAYOUT-IMPLEMENTATION.md](INDIVIDUAL-PACKAGE-LAYOUT-IMPLEMENTATION.md) | Remediation | Combined vs individual package resolution (2026-11-17) |
| [INDIVIDUAL-PACKAGE-LAYOUT-INDEPENDENT-AUDIT.md](INDIVIDUAL-PACKAGE-LAYOUT-INDEPENDENT-AUDIT.md) | Historical audit | Independent review of package-layout support |
| [TARGET-RELEASE-PACKAGE-SELECTION-FINALISATION.md](TARGET-RELEASE-PACKAGE-SELECTION-FINALISATION.md) | Design finalisation | Target-catalogue-driven package selection |
| [FIRMWARE-TRANSPORT-SCHEDULER-RETRY.md](FIRMWARE-TRANSPORT-SCHEDULER-RETRY.md) | Remediation | Scheduler retries for transient `/tool/fetch` failures |
| [FIRMWARE-TRANSPORT-RETRY-SAFETY-AUDIT.md](FIRMWARE-TRANSPORT-RETRY-SAFETY-AUDIT.md) | Historical audit | Staging reuse and stage-boundary safety |
| [OSPF-MIGRATION-COMPLETENESS-FORENSIC-AUDIT.md](OSPF-MIGRATION-COMPLETENESS-FORENSIC-AUDIT.md) | Historical audit | OSPF migration completeness forensics |
| [OSPF-CONSOLIDATION-CLEANUP-REMEDIATION.md](OSPF-CONSOLIDATION-CLEANUP-REMEDIATION.md) | Remediation | Consolidation source-template cleanup |
| [OSPF-LOOPBACK-TEN-SLASH-EIGHT-DISCOVERY.md](OSPF-LOOPBACK-TEN-SLASH-EIGHT-DISCOVERY.md) | Remediation | Loopback scope widened to `10.0.0.0/8` |
| [OSPF-HARD-CODED-IP-RANGE-AUDIT.md](OSPF-HARD-CODED-IP-RANGE-AUDIT.md) | Historical audit | Hard-coded IP ranges and intentional address-plan conventions |

Where a standards report and a subsystem guide disagree, **trust the code** and update the subsystem guide; keep the standards report’s historical findings intact and labelled.
