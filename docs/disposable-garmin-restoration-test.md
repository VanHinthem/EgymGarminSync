# Garmin strength-set restoration trial — isolated test activity

Status: **test design only**, 2026-10-13. No Garmin writes authorized.

## Purpose and boundaries

Verify whether recorded strength exercise sets can be updated **and restored** without compromising the real historical Garmin activity `24180037460`. Do not use any real historical activity as the trial target. A copied FIT import is not automatically disposable, and import may not preserve Garmin strength set metadata.

## Eligibility of a disposable test activity

- Explicitly created for testing in Garmin Connect and confirmed by the user to be disposable.
- Contains at least one ACTIVE strength set with a numeric weight; ideally includes a REST set so preservation can be tested.
- Has a known Garmin activity ID different from any historical EGYM activity.
- User explicitly approves use of that **specific** ID for the trial. Approval of this design does not approve a write.
- Establish whether Garmin supports deleting the trial activity if cleanup is needed; deletion is separately destructive and requires explicit confirmation.

## Stage A — read-only baseline (safe to implement first)

1. GET activity metadata and full exercise sets using the existing endpoints.
2. Capture local JSON snapshots with manifest hashes and, if available, original FIT export.
3. Store a full before-state including record counts, set order, exercise identities, repetition counts, timestamps, bike/REST sets and unknown fields.
4. Check for ambiguous set identity, unsupported server fields or missing values. Abort if incomplete.
5. Produce a candidate diff altering the `weight` of exactly one identified test strength set; keep all other fields unchanged.
6. Re-fetch immediately before any future mutation and fail closed if the server state differs from the backed-up snapshot.

## Stage B — mutation (blocked; separate future approval)

1. Research the exact Garmin exercise-set replacement contract: HTTP method, endpoint, required shape, whether the entire `exerciseSets` collection is replaced, and response behavior. No write requests until verified.
2. Obtain user's explicit approval for one narrowly specified test activity ID, one exact weight change, and the rollback plan.
3. Update only that disposable activity if supported; immediately GET both activity metadata and exercise sets again.
4. Verify that the intended weight changed and **nothing else** changed, including REST, set order, durations, timestamps and metadata. Abort if additional differences emerge.

## Stage C — restoration (requires independently validated mechanism)

1. Reapply the exact original set payload from the untouched verified local snapshot using a confirmed supported mechanism.
2. GET both resources and compare with the original snapshot field by field. Repeated full-collection replacement may still affect derived Garmin data; inspect what is exposed.
3. Treat any failure to restore precisely as a test failure; never extrapolate success from HTTP 200 alone.
4. Do not automatically delete the test activity, since cleanup is a separate Garmin write.

## Exit criteria for progressing beyond a disposable activity

- A complete, repeatable round trip has been independently demonstrated with verified before/after/restore diffs and original artifact hashes.
- No unexpected changes to unrelated sets, activity summary or metadata.
- Clear limitations about Garmin's unsupported/unofficial API and behavior on other activity types.
- Fresh backup, exact reviewed weight-only diff, user approval and a readback gate **for each historical activity**.
- If a safe restoration path remains unproven, historical write operations remain disabled.

## Immediate next engineering slice

Implement a **parameterized, GET-only test-activity snapshot and dry-run inspection** that rejects the actual historical EGYM activity IDs. Do not wire a mutating HTTP method or an automated deletion action.
