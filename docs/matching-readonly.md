# EGYM ↔ Garmin matching — read-only (phase 3)

## Initial slice

This branch starts with a pure comparison module, without new login attempts or API writes. It works on already-normalized exercise data and returns explicit comparison outcomes.

- Exercise identity is mapped by EGYM `activity.activityId` to Garmin `(category, exerciseName)`; workout order is not considered.
- Garmin recorded set weight is **assumed to be grams** and converted to kilograms via `/ 1000`, consistent with observed examples. This assumption still requires formal validation before writes.
- All Garmin strength sets for an exercise must share a single non-null recorded weight; conflicting weights are reported as ambiguous.
- Unknown exercises and missing sets are reported explicitly, never guessed.
- `IsActiveStrength` must be supplied by the ingestion layer to exclude REST and INDOOR_BIKE records. No inference from `ACTIVE` alone.
- The current mapping is account-specific and provisional.
- This slice compares only weights. It does **not** decide which Garmin activity belongs to which EGYM workout. That session matching must be implemented and tested separately before combining histories.
- No template modification, activity update, exercise count adjustment, or cardio alteration is performed.

## Next slices

1. Add unit tests and CI for the comparison module.
2. Normalize EGYM and Garmin data from the existing read-only clients.
3. Match workout sessions using timestamps, method and confidence constraints, never just index/position.
4. Produce user-reviewable weight diffs for activity history and templates, keeping every write disabled.

## Session matching slice

`WorkoutSessionMatcher` is a pure, read-only candidate matcher; no new API requests.

- Inputs must already be normalized to UTC. EGYM `completedAt` and Garmin session start are distinct event types.
- Require matching training variant (`REGULAR_REPBASED` → positive, `NEGATIVE_REPBASED` → negative).
- Use an initial **engineering-only** four-hour UTC timestamp tolerance. This is **not** a measured EGYM–Garmin timing offset. It must be calibrated against live samples and their actual timestamps before production matching.
- Match only unique one-to-one candidates; multiple sessions within the window produce `Ambiguous` without choosing the closest one.
- Unknown methods, unmatched dates, duplicate IDs or non-UTC timestamps fail explicitly.
- No automatic link on calendar date alone, and no implicit template/activity association.
- Next: obtain actual Garmin start timestamps alongside EGYM completion timestamps, normalize the timezones, and verify confidence before enabling cross-system comparisons.
