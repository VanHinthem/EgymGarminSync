# Historical weight-only set change: preflight contract

Status: design and acceptance checklist, 2026-10-13. **No Garmin writes.**

## Data available

For Aug 31 activity `24180037460`, Windows `--backup-aug31` produced two validated local JSON snapshots and the user separately exported an original FIT file. The JSON manifest includes SHA-256 hashes. Neither artifact has been shown to be restorable in Garmin Connect.

There are 36 recorded Garmin exercise-set records: 16 strength ACTIVE records (two each for eight machines), 2 cycling ACTIVE records and 18 REST records. Seven of eight machine-level weights differ from EGYM app history; 15 repetitions in every mapped strength set agree.

## Proposed change preparation, entirely offline

1. Load the **saved** `exerciseSets.json` locally; verify its SHA-256 against `manifest.json` before processing. Require the target Garmin activity ID and expected schema.
2. Resolve each Garmin strength set through the **category/name key** mapping, never through its position in the array. Require exactly two identifiable records for each of the eight expected machines and all 36 original records in this sample.
3. Use EGYM app-history values as the target. Only show candidate `weight` replacements on applicable ACTIVE strength records, converting kilograms to Garmin's *apparently* gram-based representation. Require explicit confirmation of Garmin units before any use beyond dry run.
4. Produce a **field-level diff** for each of the 16 strength records, not merely seven aggregate exercise totals. Retain original set order, repetitions, start times, set types, exercise identifiers, REST, cycling, HR and all unrecognized fields.
5. Parse old/new JSON and structurally compare the complete trees, ignoring only exact `weight` paths of approved target ACTIVE strength records. Reject missing, added, reordered or changed entries, duplicate mappings, unexpected target values or any diff outside allowed paths.
6. Require the resulting diff to be consistent with precisely 14 changed strength set weights (seven exercises × two sets) for this particular session, and two unchanged Lat Pulldown set weights. Do not apply this assumption to other dates; July 8 EGYM per-set weights varied.
7. Refuse to proceed if the live Garmin state differs from the locally saved snapshot, even if the training date and activity ID still match.

## Backup/restore validation remains a separate hard gate

A valid diff is **not** authorization to call PUT/POST. The current GET-only API client has no validated restore method; Garmin FIT re-import may not preserve strength sets or activity identity. Research the exact replacement API semantics and validate a reversible trial against a disposable training activity with explicit approval, if feasible. Preserve full original raw set state and inspect server-side readback. If a safe rollback cannot be demonstrated, do not modify the historical activity.

**Nothing in this document approves Garmin mutation.**
