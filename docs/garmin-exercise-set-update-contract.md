# Garmin recorded strength-set update API — implementation research (2026-10-13)

**Research only. No write request made.** The user has authorized a narrow future live trial on their August 31 Garmin activity, not an unreviewed bulk rewrite.

## Evidence from independent third-party implementations

- `python-garminconnect` exposes `set_activity_exercise_sets(activity_id, payload)`; `garminconnect-js` exposes `setActivityExerciseSets(activityId, payload)`. Both describe a **replace-all** operation on the recorded activity's exercise sets rather than editing one item in isolation.
- A Go wrapper (`tamcore/garmin-mcp`) documents that the Python implementation issues **PUT** for the entire `exerciseSets` document and validates the server readback.
- `garminconnect-js` reports actual live requests require `activityId` at the document root, on each set and on each nested exercise, plus each set's `setType` and `startTime`. An incomplete payload can receive 400 or 500 responses. These requirements are from unofficial implementations, not a Garmin-supported public contract.
- Existing read endpoint verified in our project: `GET /activity-service/activity/{id}/exerciseSets`. Update endpoint needs exact source-code confirmation before being implemented in our .NET adapter. **Do not treat a guess at URL or method as validated.**

Sources:
- https://github.com/cyberjunky/python-garminconnect
- https://github.com/DynamicsNinja/garminconnect-js/blob/main/AGENTS.md
- https://pkg.go.dev/github.com/tamcore/garmin-mcp/internal/garmin/api
- https://github.com/Taxuspt/garmin_mcp

## Aug 31 one-set trial

Known Garmin activity: `24180037460`; its original full set collection is 36 records, backed up locally and compared offline. The planned trial changes **one** Chest Press set (currently 22 kg) to 21 kg, confirms server readback and tries to restore 22 kg. This still submits a replacement document containing all 36 sets if that API is used.

The validated local snapshot has Chest Press indices **2 and 18**; intended target for first trial is index 2 only, but index alone is insufficient evidence of identity. Require recorded set ID / stable identifiers and fail closed on mismatch.

## Required validation before a network write

1. Inspect exact third-party implementation source for verified URI/method and confirm the .NET HTTP client supports authenticated raw PUT without discarding fields.
2. Validate and preserve all 36 full original set records and every nested exercise; do **not** remove REST or INDOOR_BIKE entries, reorder sets, synthesize missing required values or reserialize omitting unknown fields.
3. Compare a fresh GET against the backed-up original before writing. If Garmin has changed since backup, abort.
4. Confirm a one-weight-only candidate change and review the exact outgoing payload field diff. Capture an independent backup immediately before the write.
5. One write attempt only, then immediate GET and deep structural compare of activity and exercise sets. Fail on unexpected differences.
6. Restoration (separate write) must be independently reviewed and followed by fresh GET comparisons. HTTP status alone cannot establish restoration.
7. Garmin's unofficial contract can change. If an exact reversible implementation cannot be established, retain the offline preview and make no write.

**Current outcome:** a plausible PUT/replace-all integration has been identified, but a compatible and fully safe 36-set round-trip has not yet been verified in this codebase. No Garmin data mutated during this research.
