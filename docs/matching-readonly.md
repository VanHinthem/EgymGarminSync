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
- Use an initial **engineering-only** two-hour forward-only (Garmin start → EGYM completion) UTC timestamp tolerance. This is **not** a measured EGYM–Garmin timing offset. It must be calibrated against live samples and their actual timestamps before production matching.
- Match only unique one-to-one candidates; multiple sessions within the window produce `Ambiguous` without choosing the closest one.
- Unknown methods, unmatched dates, duplicate IDs or non-UTC timestamps fail explicitly.
- No automatic link on calendar date alone, and no implicit template/activity association.
- Next: obtain actual Garmin start timestamps alongside EGYM completion timestamps, normalize the timezones, and verify confidence before enabling cross-system comparisons.

## Live timing diagnostic commands

Run these from the feature branch after CI is green, using already configured EGYM/Garmin credentials:

```powershell
dotnet run --project src/EgymGarminSync.Cli/EgymGarminSync.Cli.csproj -- --matching-times 180
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --matching-times
```

The EGYM diagnostic displays raw `completedAt`, `timezone`, and strength training methods; its timestamps are **not automatically interpreted as UTC**. The Garmin diagnostic lists the relevant activities with `startTimeGMT` labelled as UTC and `duration` seconds. Neither diagnostic links workouts yet, and both are GET-only. Compare multiple known days, including 2026-08-31 and 2026-08-07, before calibrating the session tolerance. Do not infer a clock offset from one sample or assume completion equals Garmin start.

## Live timing observations — 2026-10-13

User ran both live read-only time diagnostics. EGYM returned 28 workouts for the past 180 days; Garmin returned 29 named EGYM activities in its most recent 100 activities. These are different search scopes, so the count discrepancy is not proof of missing synchronization.

For the August examples below, EGYM `completedAt` is a wall-clock value with `timezone=Europe/Amsterdam` (CEST, UTC+02). Garmin `startTimeGMT` is UTC.

| Date | Garmin UTC start | EGYM local completion | Elapsed Garmin start → EGYM completion |
|---|---|---|---|
| 2026-08-31 | 05:34:53 | 08:18:57.454 | 44m 04.454s |
| 2026-08-19 | 05:39:36 | 08:23:58.940 | 44m 22.940s |
| 2026-08-17 | 05:36:16 | 08:22:40.456 | 46m 24.456s |
| 2026-08-07 | 05:36:47 | 08:22:44.590 | 45m 57.590s |
| 2026-08-05 | 05:35:01 | 08:21:48.342 | 46m 47.342s |

These elapsed values are **not a clock offset**. Garmin's sampled recorded durations are approximately 70 minutes, so the EGYM completion event occurs well before the Garmin activity end. Date/method and validated event semantics are essential for matching.

**Exception:** 2026-06-24 EGYM reports `NEGATIVE_REPBASED`, but the Garmin activity with a compatible time has name `EGYM Positief` (ID `23360196698`). This is a real cross-system *method label mismatch*. The conservative matcher must report it as not matched or conflicting, never silently force-match by date.

The initial four-hour tolerance was reduced to a provisional two-hour forward-only candidate window. This is not a validated production threshold. A single nearby activity with the wrong method is reported as `MethodConflict` (June 24 example), while multiple nearby sessions remain `Ambiguous`. Neither status authorizes automatic updates.

No data was changed in either system.

## Session matching refinement — 2026-10-13

- EGYM completion must be at or after Garmin activity start; reversed timestamps never match.
- The provisional default candidate window is now 0–120 minutes rather than an undirected ±4 hours. This accommodates the observed ~44–47-minute elapsed times but remains an engineering parameter, not a guarantee.
- Method disagreement for a unique timestamp candidate returns `MethodConflict` with candidate activity ID, for review only. It must not be treated as a confirmed link for a weight update.
- If two Garmin activities are in the window (or two EGYM sessions share a Garmin candidate), the result stays `Ambiguous` even when one of their names appears compatible.
- Tests include the real June 24 timestamps and incompatible variants, using fixture values rather than live credentials.
- Incomplete session detection still requires exercise/set completeness inspection after candidate association. A time-only match does not imply a complete workout.
