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

## Read-only circuit completeness validation

The independent `StrengthSessionCompleteness.Check` checks a caller-specified set of EGYM machine IDs and expected set count per machine. The verified full-circuit sample contains eight mapped strength machines with two sets each. REST and INDOOR_BIKE records are excluded via the explicit `IsActiveStrength` flag.

The checker reports missing/duplicate EGYM exercises, missing/extra Garmin strength sets, unmapped IDs, and unexpected exercises. It does not modify workouts or claim that all sessions are complete. Ingestion must still derive `IsActiveStrength` reliably from Garmin's recorded set type and exercise category.

**Important:** These are structural checks only. Same set count does not prove exact set-to-set correspondence, equal reps, or valid resistance units. Incomplete and conflicting sessions must never authorize an automatic write.

## Real API JSON structural diagnostics (next validation)

`CircuitJsonNormalizer` now interprets the actual EGYM `exercises[].activity.activityId / sets[]` shape and Garmin `exerciseSets[]` shape. Garmin REST and bike records are omitted; multiple identical names in one set are treated as one set. Mixed identities, missing arrays and unknown set types fail closed.

Both existing read-only CLIs expose a diagnostic. **These check each source separately; there is not yet a combined cross-system report.**

```powershell
dotnet run --project src/EgymGarminSync.Cli/EgymGarminSync.Cli.csproj -- --circuit-check 180
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --circuit-check
```

EGYM checks the most recent twelve workouts for eight mapped machines with exactly two sets each. Garmin checks two previously verified sample activity IDs for sixteen strength set records. Neither command modifies Garmin, and neither authorizes linking mismatched methods, incomplete sessions or weight updates. Both need local CI and live output verification.

## Live structural verification — 2026-10-13

The user executed both `--circuit-check` commands on Windows with real, read-only API data.

- EGYM returned 28 workouts for the 180-day query. The CLI evaluated the latest 12; **all 12** had eight recognized strength machines, exactly two set records per machine, no missing machines and no unexpected strength exercises.
- Garmin inspected the previously identified **negative** activity `24180037460` and **positive** activity `23883291887`. Each returned 36 set records, 18 ACTIVE and 18 REST. The Garmin normalizer extracted 16 strength records in each activity: eight known machine identities with two sets each. The two indoor-bike records were excluded from strength counts.
- These observations validate *structural completeness for the sampled records*, not every historical training or the correctness of resistance-weight semantics.
- Matching by exercise identifier rather than recorded exercise order remains mandatory. Full end-to-end pairing and live weight-difference reporting are not yet implemented.
- No Garmin or EGYM records were changed.

## Combined review-only report (fixture-validated)

`ReadOnlyComparisonReport.Build` combines an already-established session match, structural completeness and identity-based weight comparison. It **refuses to produce weights** if the session is ambiguous, has a method conflict, or is incomplete. It also requires one EGYM weight per expected exercise and rejects unknown or conflicting Garmin weight readings.

The 2026-08-31 fixture uses the previously observed EGYM and Garmin negative workout values, yielding eight comparisons (seven differences, one equal). These are **unit-test fixtures sourced from separate observed API output**, not proof of an end-to-end integrated live report. Cross-system units and negative resistance meanings remain unverified: displayed differences are for review, never a recommendation to overwrite recorded Garmin sets.

Next: connect normalized live read-only API data to the combined report and validate session identity before interpreting weight differences.

## Live combined diagnostic — initial single-session slice

The Garmin read-only CLI now includes a narrowly scoped integration for the **2026-08-31** EGYM negative workout and known Garmin activity `24180037460`:

```powershell
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --compare-aug31
```

This command requires both `GARMIN_EMAIL`/`GARMIN_PASSWORD` and `EGYM_EMAIL`/`EGYM_PASSWORD`, plus optional `EGYM_BASE_URL`. It retrieves EGYM workouts for one date and Garmin recent activity metadata and recorded sets via **GET only**. It requires exactly one EGYM workout for the day, converts its gym-local completion timestamp with the returned `timezone`, uses the verified Garmin activity ID, checks method and forward-only timing, validates both circuits, then prints a read-only difference per mapped machine.

No state changes or update endpoints exist on this path. A successful printed difference is **not approval** to update Garmin, because EGYM negative resistance semantics and cross-system units are not conclusively validated.

The CLI is intentionally restricted to this observed activity for the first live test. CI compilation and a Windows live run are still required; verify output before generalizing to automatic multi-session matching.

## First integrated live comparison passed — 2026-10-13

The user successfully ran `--compare-aug31` on Windows against both EGYM and Garmin. The read-only session matcher returned `Matched` for Garmin activity `24180037460` with start-to-EGYM-completion elapsed `00:44:04.454`. Both sides passed structural completeness, and the gated report returned `reviewable=True`.

| EGYM activity ID | Exercise | EGYM observed kg | Garmin observed kg | EGYM minus Garmin kg |
|---:|---|---:|---:|---:|
| 329 | Leg Extension | 22 | 20 | +2 |
| 330 | Abdominal Crunch | 30 | 24 | +6 |
| 331 | Back Extension | 44 | 43 | +1 |
| 332 | Leg Curl | 23 | 22 | +1 |
| 333 | Chest Press | 21 | 22 | -1 |
| 334 | Seated Row | 32 | 31 | +1 |
| 335 | Lat Pulldown | 33 | 33 | 0 |
| 581 | Leg Press | 95 | 89 | +6 |

Seven exercise weights differed; one was equal. All values are **observations only**, assuming the Garmin raw weight-to-kg conversion. Negative EGYM resistance semantics are not yet verified and this output is not approval for writes. No mutation API was used.

## Positive workout comparison (2026-08-07)

The same integrated GET-only comparison is now reusable for the known positive workout on **2026-08-07**, Garmin activity `23883291887`. No weights or exercise identities have been changed, and no write endpoints are called.

```powershell
# From feature/3-readonly-matching after CI succeeds:
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --compare-aug07
```

This reuses the existing session matching, structural checks and weight comparison rather than creating a second comparison implementation. The previously verified negative workout continues to use `--compare-aug31`. Live positive comparison results must be examined before any broader historical matching.

## Positive live comparison verified — 2026-10-13

The user successfully executed `--compare-aug07` against both live read-only APIs. Session `2026-08-07` matched Garmin activity `23883291887` with start-to-EGYM-completion elapsed `00:45:57.590`, structural completeness `True` and `reviewable=True`.

| EGYM activity ID | Exercise | EGYM observed kg | Garmin observed kg | EGYM − Garmin kg |
|---:|---|---:|---:|---:|
| 329 | Leg Extension | 28 | 23 | +5 |
| 330 | Abdominal Crunch | 35 | 30 | +5 |
| 331 | Back Extension | 50 | 51 | -1 |
| 332 | Leg Curl | 29 | 27 | +2 |
| 333 | Chest Press | 27 | 27 | 0 |
| 334 | Seated Row | 40 | 36 | +4 |
| 335 | Lat Pulldown | 40 | 36 | +4 |
| 581 | Leg Press | 115 | 103 | +12 |

Seven differences and one equal reading, despite structurally complete sets. Observations only; this does **not** establish that Garmin values should be overwritten. Exact unit and machine-load semantics still require verification, including for positive workouts. No writes occurred.

## Per-set repetition comparison (2026-10-13)

Live combined diagnostics for `--compare-aug07` and `--compare-aug31` now also read EGYM `sets[].numberOfReps` and Garmin `exerciseSets[].repetitionCount`, independently of weight comparisons. Expected typical schemes are 20 reps per set for regular/positive workouts and 15 for negative workouts, but actual observed values remain authoritative; a mismatch is shown rather than coerced to the template.

The report shows two repetition values per machine for each system and the result `Equal`, `Different`, or `MissingOrInvalidSets`. Because per-set identity/order correspondence is not established, repetitions are compared as multisets per exercise, so swapped set ordering does not produce a false difference. This does **not** prove which specific Garmin set maps to which EGYM set if their counts vary. Bike/REST are excluded. Five tests cover both schemes, discrepancy, reversed set order, and absent sets. CI and live runs still require validation. No write operations are introduced.

## Live per-set repetitions verified — 2026-10-13

The user ran both combined read-only diagnostics after repetition support was added:

- **2026-08-07, positive** (Garmin activity `23883291887`): session matched; circuit structurally complete; **6 of 8 exercises** have identical repetition counts. EGYM Leg Curl (`activityId=332`) reported `[20,19]` versus Garmin `[20,20]`; EGYM Seated Row (`activityId=334`) reported `[20,19]` versus Garmin `[20,20]`. All other strength exercises reported `[20,20]` in both systems. These are **39 versus 40 reps per exercise** on those two machines. Weights: seven differences, one equal.
- **2026-08-31, negative** (Garmin activity `24180037460`): session matched; circuit structurally complete; **all 8 exercises** reported `[15,15]` in both systems. Weights: seven differences, one equal.

**Interpretation:** structural completeness (eight machines, two sets each) does not imply correct repetitions. Treat weights, repetition differences and structural completeness as separate report dimensions. The prototype's `reviewable=True` currently reflects the weight-comparison gate and **does not mean repetition counts agree or Garmin writes are approved**. No writes occurred.

## Integrated repetition review status

The combined read-only report now holds `Repetitions` and `RepetitionsAllEqual` independently from `HasReviewableDifferences` (weight comparison status). Live output labels these separately as `weightReviewable` and `repetitionsAllEqual`, so a positive session with two 19-rep second sets does not appear fully matching just because the circuit is structurally complete.

Missing repetition inputs do not imply equality. Unit tests cover the observed Aug 7 mismatches and Aug 31 equality. This is for human review only, not an authorization to write.
