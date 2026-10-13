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

## Historical session candidates — first live validation

The Garmin CLI now offers `--history-match`, which uses **GET only** to retrieve EGYM workouts for 180 days and the 100 most recent Garmin activities. It converts EGYM local completion times using each workout's `timezone`, normalizes Garmin `startTimeGMT` to UTC, and runs `WorkoutSessionMatcher` over the full two-sided candidate sets at once.

```powershell
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --history-match
```

The output contains `Matched`, `MethodConflict`, `Ambiguous`, `NoCandidate` and `UnsupportedMethod` plus a summary. It intentionally **does not fetch all Garmin exercise sets** or claim that matched sessions have verified reps and weights. That will be a separate small slice once date/method pairing is checked. Garmin's latest-100-activities listing and EGYM's 180-day period have different coverage, so `NoCandidate` can indicate an unlisted Garmin activity rather than a missing workout. Incomplete or invalid EGYM timestamp data is excluded explicitly. The candidate window remains an engineering-only 0–120 minutes. No write paths exist.

## Historical session matching verified live — 2026-10-13

User executed `--history-match` on Windows. EGYM returned **28/28 normalized sessions** in its 180-day query; the Garmin recent-100 activities list contained **29 named EGYM activities**. The conservative two-sided, forward-only matcher reported:

- `Matched=27` (27 of 28 EGYM sessions, 96.4%).
- `MethodConflict=1` for 2026-06-24: EGYM `NegativeRepBased`, Garmin `EGYM Positief`, activity `23360196698`, elapsed `00:42:03.206`.
- `Ambiguous=0`, `NoCandidate=0`, `UnsupportedMethod=0`.
- The observed elapsed Garmin-start-to-EGYM-completion intervals range approximately 42–49 minutes in these 28 sessions. This is an elapsed event difference, not a clock offset.

These are **session candidate** results, not per-activity exercise/rep/weight validations. The extra Garmin EGYM activity cannot be classified as an unmatched workout without comparing the different query scopes. The 24 June conflict must remain review-only, not an automatic method correction. No writes occurred.

## Historical exercise-set comparison (new, pending live validation)

The integrated CLI now has a bounded GET-only historical comparison:

```powershell
# Inspect the five most recent matched sessions first
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --history-compare 5

# Once the first run is verified, inspect up to 27 matched sessions
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --history-compare 27
```

The command fetches the 180-day EGYM history once, obtains the latest 100 Garmin activities, runs two-sided session matching, and fetches Garmin exercise-set records for up to the requested number of **Matched** activities only. Method conflicts, ambiguities and no-candidate sessions are printed but excluded from recorded-set comparisons. Each matched session prints structural completeness, separate repetition disagreement counts, weight comparison status and per-exercise differences. Invalid records fail closed; if EGYM weights vary between rounds, the current per-exercise weight aggregate is not produced.

This version is read-only and does not change templates, activity exercise sets, cycling or rests. Weight differences remain provisional given unverified resistance semantics. A historical run may uncover incomplete sessions or different Garmin shapes not present in the original two samples; review output before broadening.

## First five historical exercise-set comparisons — live 2026-10-13

User ran `--history-compare 5`: historical session candidates EGYM=28, Garmin=29, matched=27, method conflicts=1. **Five matched sessions inspected**, `structural errors=0`, and all five reported `complete=True`, `weightReviewable=True`; each had seven exercises with differing weights. These are read-only observations, not update approvals.

| Date | Repetition mismatches | Weight differences | Notes |
|---|---:|---:|---|
| 2026-08-31 | 0 | 7 | Negative: all reps equal |
| 2026-08-19 | 1 | 7 | Leg Curl (332): EGYM [15,15], Garmin [15,16] |
| 2026-08-17 | 0 | 7 | Negative: all reps equal |
| 2026-08-07 | 2 | 7 | Seated Row (334), Leg Curl (332): EGYM [20,19], Garmin [20,20] |
| 2026-08-05 | 1 | 7 | Leg Press (581): EGYM [20,20], Garmin [0,20] |

Additional **2026-08-05** anomaly: Seated Row (334) EGYM 40 kg vs Garmin 27 kg (`delta=+13 kg`), whereas on 2026-08-07 Garmin Row was 36 kg. The zero-rep Garmin set is structurally present but semantically suspicious; set completeness must remain separate from repetition validity. A `0` repetition count should be shown explicitly, not coerced to `20` or dismissed as a missing set.

The 2026-06-24 method conflict was correctly printed and omitted from all exercise comparisons. No Garmin or EGYM writes were performed.

Next validation should run `--history-compare 27` after CI review; watch for per-set variable weights, incomplete records and API errors before contemplating synchronization.

## Full historical exercise comparison — live 2026-10-13

The user ran `--history-compare 27`. Summary: `EGYM=28`, `Garmin=29` in their respective query scopes, `matched=27`, `conflicts=1`; `Inspected=27`, `structural errors=0`, `no data written`.

Of the 27 **matched** sessions inspected:
- **24** reported `complete=True` and `weightReviewable=True`.
- **2** reported `complete=False`, `weightReviewable=False`: **2026-06-15** (Garmin `23259077933`) and **2026-05-04** (Garmin `22762471069`). The current CLI does not print the underlying completeness issues; investigate before interpreting these as missing workouts or assuming what is absent.
- **1** session, **2026-07-08** (Garmin `23519342631`), reported *EGYM weights vary between sets; requires per-set review*. Its current historical comparison skips the overall weight/repetition report rather than aggregating conflicting source weights.
- Across the **24 fully compared sessions**, there were **111 exercise-level weight differences**, and **6 exercise-level repetition mismatches across 5 sessions**. A differing exercise-level repetition result may involve one set (not necessarily both).
- Repetition mismatches:
  - Aug 19, Leg Curl `332`: EGYM `[15,15]` vs Garmin `[15,16]`
  - Aug 7, Seated Row `334` and Leg Curl `332`: each EGYM `[20,19]` vs Garmin `[20,20]`
  - Aug 5, Leg Press `581`: EGYM `[20,20]` vs Garmin `[0,20]`
  - Jul 1, Seated Row `334`: EGYM `[15,15]` vs Garmin `[15,16]`
  - Jun 8, Abdominal Crunch `330`: EGYM `[20,20]` vs Garmin `[20,0]`
- Two recorded Garmin **zero-rep sets** (Aug 5 and Jun 8) must be reviewed as set-level anomalies; they are not evidence the set is absent.
- Six dates had **zero** weight differences in the full comparisons: Apr 20, Apr 29, May 6, May 8, May 11, and (check list) no: **May 4 is incomplete and therefore not comparable**. Actually, the recorded zero-difference complete dates are **Apr 20, Apr 29, May 6, May 8, May 11**. These are five sessions, not six.
- The Garmin positive and negative recorded weights often remain identical across several consecutive activities while EGYM values change; this is suggestive of stale planned weights but not proof.
- The June 24 `MethodConflict` remains excluded from exercise comparisons.
- All findings are strictly **observations**. Neither resistance semantics nor any Garmin correction is authorized. No writes occurred.

Next: print detailed per-exercise set counts/repetition data for the two incomplete sessions, and preserve **individual EGYM set weights** for July 8 rather than discarding the entire session. Then improve a bounded review report before considering write design.

## Exception diagnostics — pending CI and Windows validation

The historical comparison now prints detailed **raw per-set** EGYM and Garmin strength data for (a) EGYM exercises with different weights between sets, (b) incomplete circuit reports, and (c) any Garmin ACTIVE strength set with zero repetitions. The output includes structural issue side, mapped exercise ID, expected/actual set count and reason; for each EGYM set it shows its position, reps and weight; and for each Garmin ACTIVE strength record its raw index, exercise identity, reps and unconverted raw weight (grams as observed in earlier tests).

This is a diagnostic **not** an assumption that physical set order matches across systems, nor a solution yet for differing set weights. Expected examples are Jul 8 (variable EGYM weights), Jun 15 and May 4 (incomplete circuits), and Aug 5 and Jun 8 (zero Garmin reps). Run `--history-compare 27` after CI succeeds and inspect these anomalies before implementing per-set reconciliation. No writes.

## Exception diagnostics verified live — 2026-10-13

The user ran `--history-compare 27` after adding per-set exception logging; 27 matched sessions inspected with zero diagnostic exceptions and no writes.

- **2026-07-08** Garmin `23519342631`: all eight EGYM strength machines show two sets in the raw printout. EGYM Leg Extension (`329`) has 29 kg then 28 kg, each 20 reps; Lat Pulldown (`335`) has 39 kg then 40 kg, each 20 reps. Leg Press (`581`) has 19 then 20 repetitions at 115 kg. All 16 Garmin expected machine records visibly have 20 reps; Garmin per-exercise recorded weights are unchanged within each machine. The current per-exercise weight aggregation refuses both EGYM variable loads and therefore skips the entire session: refactor to preserve per-set loads and still independently review repetitions.
- **2026-06-15** Garmin `23259077933`: all 16 expected ACTIVE strength set records are visibly present, plus Garmin record index 36 `UNKNOWN/(none)`, 0 reps, absent weight. Completeness status false reflects this *extra* unexpected set, not demonstrated missing expected sets. EGYM reps are 20/20 for each expected exercise; visible expected Garmin sets are also 20/20.
- **2026-05-04** Garmin `22762471069`: all 16 expected ACTIVE strength sets are visibly present plus the same extra `UNKNOWN/(none)` record index 36 (0 reps, no weight). Additionally, Garmin BENCH_PRESS first set has 20 reps and second 15; EGYM Chest Press (`333`) is 15/15. This is a real identifiable strength-set repetition mismatch concealed by the present completeness gate.
- **2026-08-05** Garmin `23858095280`: expected Leg Press first ACTIVE strength set (raw record 6) has 0 reps versus EGYM 20, with second set 20 on both systems. The 0 reps are not just an UNKNOWN extra.
- **2026-06-08** Garmin `23176881588`: expected Abdominal Crunch second ACTIVE strength set (raw record 24) has 0 reps versus EGYM 20.
- The extra UNKNOWN Garmin record might be a non-strength placeholder or a genuine unexpected strength item. We cannot infer its semantics from the displayed fields. Keep it explicit and do not delete/ignore it automatically. Some other records show a genuine zero within a mapped expected exercise: handle those separately.

**Next slice recommended:** distinguish expected machine set coverage from unexpected additional active records, show reps for confidently mapped sets even when extras exist, and store/compare weights for each source set without assuming Garmin/EGYM set order is correlated. All operations stay read-only.

## Read-only mapped-set exception handling (pending CI / live validation)

This slice separates `CompletenessReport.IsComplete` (strict: any unexpected Garmin ACTIVE record is still an issue) from the ability to review all **expected** exercise identities and their repetition and weight values. An extra Garmin `UNKNOWN` record stays visible as `UnexpectedExercise`, but if no expected set is missing or duplicated, the mapped-exercise comparisons may proceed. In particular, May 4 should show BENCH_PRESS 15/15 EGYM vs 20/15 Garmin, and June 15 should show the extra unknown without reporting missing expected strength sets. Unit tests cover the extra-UNKNOWN case.

For Jul 8 variable EGYM resistance, a separate read-only `SetWeightReviewMatcher` compares each machine's actual two EGYM weights to the two Garmin recorded weights as **unordered multisets**, without claiming cross-system set order identity. Missing Garmin sets or different values are not equal; varied weights are never collapsed to an average or a single arbitrary value. The historical diagnostic prints `SET-WEIGHT` differences and independent mapped-set repetition comparisons even when the existing per-exercise aggregate cannot be built. Three new tests cover reverse order, July 8 variable weights and a missing set.

No Garmin write endpoint, token handling, or workout template data is changed. Verify CI then rerun the full historical diagnostic to confirm both May 4 and July 8 output.
