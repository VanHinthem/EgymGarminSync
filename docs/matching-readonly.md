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

## Mapped-set exception handling verified live — 2026-10-13

After the mapped-set comparison and per-set weight review changes, user reran `--history-compare 27`: `EGYM=28`, `Garmin=29`, `matched=27`, `conflicts=1`, `Inspected=27`, `structural errors=0`, no writes.

- **2026-07-08**, Garmin `23519342631`: previously skipped because EGYM set weights differed. New read-only diagnostic prints per-set values: Leg Extension `329` EGYM [29,28] kg, Garmin [23,23] kg; Lat Pulldown `335` EGYM [39,40] kg, Garmin [36,36] kg. Overall **seven** per-exercise `SET-WEIGHT` differences and one Leg Press repetition mismatch EGYM [19,20] versus Garmin [20,20]. `mappedSetsComplete=True`, `repsEqual=False`. Set weights are treated as unordered multisets, not mapped physical first-to-first.
- **2026-06-15**, Garmin `23259077933`: `complete=False` still truthfully reports the extra Garmin `UNKNOWN/(none)` ACTIVE set (index 36, zero reps, missing weight); known 16 strength sets are all present. `repsEqual=True`, `weightReviewable=True`, **six** known exercise-level weight differences.
- **2026-05-04**, Garmin `22762471069`: extra Garmin `UNKNOWN/(none)` active set also remains visible and `complete=False`; `weightReviewable=True`, `weightDifferences=0`. A previously hidden repetition mismatch now appears: Chest Press `333` EGYM [15,15] vs Garmin [20,15].
- The actual zero-repetition readings **within known strength exercises** on Aug 5 Leg Press and Jun 8 Abdominal Crunch are still shown separately. A zero-rep `UNKNOWN` extra is a different kind of anomaly.
- Thus **eight known-exercise repetition mismatches** have been observed over the 27 matched sessions: six in the originally fully compared 24, plus the newly observable July 8 and May 4 mismatches. The July 8 weights are counted at set-review level, not as scalar exercise-weight aggregates.
- CLI outputs `[29,0,28,0]` for two kg values under Dutch numeric locale (comma serves both as decimal delimiter and list separator). A future presentation improvement should format weight lists unambiguously, e.g. `[29.0 kg; 28.0 kg]`. The raw values themselves are correctly preserved.
- No update permission follows from `weightReviewable=True`; resistance semantics remain unverified. All work is read-only.

Next consideration: finalize a concise status model separating `ExpectedMappedSetsComplete`, `ExtraUnknownRecords`, `RepetitionDifferences` and `PerSetWeightDifferences`; improve output labels before backup/diff/write design.

## Consolidated read-only training review summary — pending CI/live check

A new pure `TrainingReviewSummary.Build` returns four independent review dimensions for each matched historical session: `ExpectedMappedSetsComplete`, `ExtraGarminRecords`, `RepetitionDifferences`, and `ExerciseWeightDifferences` (the latter compares the two actual per-set weights as an unordered multiset). The historical CLI now prints a `SUMMARY` row per matched session in addition to existing details; all values remain observations only. Unknown comparison coverage is represented by `null`, not zero. An extra unknown Garmin ACTIVE set does not imply missing expected mapped sets. Three tests cover extra UNKNOWN with a real mapped repetition difference, missing expected set, and variable EGYM set weights.

The output still uses existing full-detail diagnostics and does not provide or enable a Garmin write path. Validate on CI then run `--history-compare 27` to confirm expected values on Jul 8, Jun 15 and May 4.

## Consolidated history summary verified live — 2026-10-13

User ran `--history-compare 27` with consolidated `SUMMARY` lines. Results across **27** matched exercises-set histories: expected mapped strength sets complete on **27/27**; exactly **2** extra Garmin UNKNOWN records (2026-06-15 and 2026-05-04); **8** exercise-level repetition differences across **7** sessions; **124** exercise-level set-weight differences (comparison of two-set values per exercise, not 124 individual edited set records); 0 diagnostic errors; no writes. The additional EGYM session on 2026-06-24 remains method-conflicted and excluded.

Verified exceptions:
- Jul 8: expected sets complete, zero extras, one Leg Press repetition discrepancy, seven exercises with set-weight differences; EGYM Leg Extension [29,28] kg and Lat Pulldown [39,40] kg retained.
- Jun 15: expected sets complete, one extra Garmin UNKNOWN, zero repetition differences, six exercise-level weight differences.
- May 4: expected sets complete, one extra Garmin UNKNOWN, one Chest Press repetition discrepancy, zero weight differences.
- Aug 5 and Jun 8: zero repetitions inside known mapped Garmin strength sets remain flagged.

Output improvement: summary lines now prefix the local workout date and Garmin activity ID, so each row is unambiguous in long logs. This presentation change is pending CI verification.

All weight semantics and safe Garmin write operations remain unverified and not authorized.

## Cycling data discovery — new read-only diagnostic (pending CI and live test)

The Garmin CLI has a focused `--cycling-aug31` command for 2026-08-31, Garmin activity `24180037460`. It retrieves one day's EGYM workout and Garmin recorded activity exercise sets with **GET requests only**. EGYM `activityId=31` exercise entries (observed two segments) are printed separately with the raw values of duration, distance, speed, heart rate, calories and other candidate scalar fields on each nested set. Garmin `INDOOR_BIKE` ACTIVE records (expected two) are inspected for corresponding recorded values and safely bounded scalar field names. Structured HRM/summary data is described by shape only; do not log raw sensor arrays, identity data or tokens.

```powershell
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --cycling-aug31
```

Neither the availability nor the **units/semantics** of EGYM cycling values are confirmed merely because fields exist in its schema. Likewise Garmin strength activity `exerciseSets` might not support writing additional cardio metrics. Interpret the live sample before designing segment matching, deciding whether to enrich existing bike sets, or considering another activity representation. No write operations and no changes to strength matching.

## EGYM cycling vs Garmin bike records — live 2026-08-31

User successfully ran `--cycling-aug31` using the GET-only diagnostic. The EGYM workout contains two `activityId=31` Bike Ergometer exercise entries, and Garmin activity `24180037460` has two `INDOOR_BIKE` ACTIVE set records.

**EGYM API entry order differs from chronological order**:
- EGYM bike entry #1, `completedAt=2026-08-31T08:18:57.454` local Europe/Amsterdam (06:18:57 UTC), 5 time-based sets. Durations `7,5,4,1,1485`, sum **1502** raw units (likely seconds); distances `30,30,30,10,11010`, sum **11110** raw units (likely meters); `kiloCalories=178.85613001250064`.
- EGYM bike entry #2, `completedAt=2026-08-31T07:34:53.591` Amsterdam local (05:34:53 UTC), 3 time-based sets. Durations `7,3,523`, sum **533**; distances `40,20,3810`, sum **3870**; `kiloCalories=61.125221768749995`.
- All observed set-level `heartRate=0`, and `hrmData` empty. Set `speed` values ~6.25–7.55, **units unknown**, not yet compatible with distance/duration arithmetic without conversion.
- EGYM `summary` exposes names `totalDuration,totalDistance,verticalDistance,avgPace,avgHeartRate,maxHeartRate,avgSpeed,avgIncline`, but values were not printed, so source-level summary reconciliation is pending.

**Garmin workout activity**:
- Bike record index 0, `startTime=2026-08-31T05:34:53.0` UTC, `duration=537.995` seconds (~8:58).
- Bike record index 34, `startTime=2026-08-31T06:18:56.0` UTC, `duration=1500.0` seconds (25:00).
- Garmin's bike exercise-set objects expose `duration`, timestamps and other strength-related optional scalar field names but **no observed distance, speed or calories values** in the sampled raw exercise-set records.
- Timestamp/elapsed comparisons strongly support chronological correspondence EGYM entry #2 ↔ Garmin index 0 (~533 vs 537.995 sec) and EGYM entry #1 ↔ Garmin index 34 (~1502 vs 1500 sec). Do **not** pair by EGYM API array position.

**Interpretation:** EGYM supplies potentially useful cycling duration, distance and calorie fields. This does not establish an endpoint/schema for injecting these metrics into an existing Garmin strength activity, nor that Garmin Connect will display them. Preserve the no-write policy. Next read-only slice: print bounded `summary` scalars and clarify distance/speed units; inspect Garmin activity-level summary if useful, with no writes.

## Cycling summary reconciliation diagnostic (pending CI/live validation)

The existing GET-only `--cycling-aug31` command now prints raw scalar values within EGYM bike `summary` (including `totalDuration`, `totalDistance`, `avgSpeed`, `avgPace`, heart-rate and incline values when present) and calculates independent raw sums of the nested set `duration` and `distance` values. Missing or nonnumeric set values are explicitly reported as unavailable instead of silently counted as zero. Values remain in source units: no unsupported speed or distance conversions. Compare summary fields with set sums and the already-observed Garmin bike set durations before researching Garmin activity-level enrichment. No writes.

## Cycling EGYM summary fields validated live — 2026-08-31

The user reran `--cycling-aug31` and obtained actual EGYM summary numbers for both segments:

| Chronological segment | EGYM duration | EGYM distance | EGYM avgSpeed | EGYM kcal | Garmin matching record | Garmin duration |
|---|---:|---:|---:|---:|---|---:|
| Earlier bike, EGYM API entry #2 | 533 | 3870.0 | 7.26078799249531 | 61.125221768749995 | index 0, start 05:34:53 UTC | 537.995 |
| Later bike, EGYM API entry #1 | 1502 | 11110.0 | 7.396804260985353 | 178.85613001250064 | index 34, start 06:18:56 UTC | 1500.0 |

Set durations and distances sum to each segment's EGYM summary totals (only machine rounding differences). The ratios `3870 / 533 ≈ 7.26` and `11110 / 1502 ≈ 7.40` are consistent with EGYM recording distance in meters, duration in seconds and average speed in m/s (**strong inference**, not a formal API unit declaration). Derived speeds ≈26.1 and 26.6 km/h. All EGYM set `heartRate` values observed as zero, and summary `avgHeartRate` / `maxHeartRate` fields empty, so **no usable source heart-rate measurements** demonstrated.

Correlate bike entries with Garmin using timestamps and duration, not API list order. The Garmin exerciseSets records show durations but did not expose distance/speed/calorie fields. The feasibility of enriching the same Garmin strength activity with those metrics remains unproven; further read-only investigation should check Garmin **activity-level summary/detail schemas** and whether metrics could be represented without replacing sensor FIT data, never assume arbitrary writeable JSON fields. No writes.

## Garmin cycling enrichment: activity-level GET inspection (pending CI/live validation)

The `--cycling-aug31` diagnostic now additionally performs a GET of `/activity-service/activity/24180037460` to inspect selected activity-level total duration, distance, speed and calorie values. It prints only relevant fields and candidate property names (not full JSON, heart-rate values, owner metadata or credentials), and labels the result **whole activity**, not an individual bike interval. Failure to retrieve or recognize the response does not imply fields can/cannot be written. User confirms Garmin already captures heart rate well, so **heart rate is excluded from synchronization and must remain untouched**. Continue read-only research to determine whether individual indoor-bike segment enrichment is actually representable in Garmin Connect; avoid creating duplicate activity or calorie totals.

## Garmin activity summary verified live — 2026-08-31

User reran `--cycling-aug31`. Garmin GET `/activity-service/activity/24180037460` returned `summaryDTO`: `duration=4236.322` (~70m36s), `distance=0.0`, `averageSpeed=0.0`, `calories=635.0`, `startTimeGMT=2026-08-31T05:34:53.0`, `startTimeLocal=2026-08-31T07:34:53.0`.

By comparison, two EGYM cycling segments together account for `533+1502=2035` seconds (33m55s), `3870+11110=14980` likely meters (14.98 km stationary cycling), and `61.125221768749995+178.85613001250064≈239.98` EGYM kilocalories. These are **bike-only** numbers; Garmin summary measures the entire strength activity. Do not add EGYM 239.98 kcal to Garmin 635 kcal; Garmin already measures activity calorie expenditure and heart rate, and double counting is a risk. Keep Garmin heart rate, current calorie total, timing and strength sets unchanged.

The Garmin `exerciseSets` bike records have duration but no apparent per-segment distance/speed/calorie properties, and overall activity summary distance is zero. Garmin's documented Connect editing capabilities do not establish that adding distance to a strength activity is supported. Garmin states distance may be unavailable for gym/fitness activity types that do not record it, and edits to activity details do not necessarily modify the exported activity file. Do not try arbitrary JSON fields or FIT rewriting without a separately verified, reversible workflow. No Garmin writes authorized.

## Cycling enrichment scope decision — 2026-10-13

User-approved **design scope only**: consider adding **EGYM stationary-bike distance** to a matching existing Garmin strength activity. Do **not** sync cycling speed, calories or heart rate; do not modify Garmin's existing durations, strength sets or HR data. On 2026-08-31 the two EGYM bike blocks total **14,980 m** (3,870 + 11,110 m), while the Garmin full-activity summary reports `distance=0`. Proposed read-only preview: `distance: 0 m -> 14,980 m`, explicitly marked **not write-ready**.

Critical unresolved detail: if Garmin calculates average speed from full strength-activity duration (~4,236 s / 70m36s), 14.98 km would imply ~12.7 km/h rather than the bike-only 33m55s average (~26.5 km/h). We must determine which Garmin fields are genuinely supported/editable for this activity type and whether speed is recomputed, before considering changes. Treat whole-activity distance as a different concept from two bike-segment distances. No write method, backup, or user-authorized activity mutation exists yet. This decision is not authorization to modify Garmin data.

## Garmin distance edit feasibility — external documentation review, 2026-10-13

Garmin's official guide "How Do I Edit an Activity in Garmin Connect?" documents activity edits in Garmin Connect Web and App, but does **not** guarantee that total distance is editable for a recorded Strength Training activity. It explicitly notes Connect edits do **not** alter an exported original activity file. Source: https://support.garmin.com/en-GB/?faq=aNGg6LTaoT4X7fyCnIuQW6&productID=73207&tab=topics&topicTag=region_activities

Garmin's official activity-trimming guidance explicitly lists **Strength Training** among activity types unsupported for trimming. Trimming is not the same as editing a total-distance summary, so this is a limitation signal rather than proof that total-distance editing is unavailable. Source: https://support.garmin.com/en-US/?faq=y6fK2dqAd34u9R5x54cxH8

Implications for issue #3:
- Keep proposed bike-distance change `0 -> 14980 meters` as a **blocked review-only candidate**, not an executable change.
- Do not infer Garmin averageSpeed will recompute from bike-only duration: this is a whole Strength Training activity spanning 4236 seconds, while bike-only EGYM duration is 2035 seconds.
- Do not use arbitrary Garmin `summaryDTO.distance` edits or FIT replacement/duplicate activities without verifying update support, backup/restore, consistency of Garmin UI and exports, and explicit user authorization.
- Scope remains distance only. Never change heart rate, calories, time or exercise sets for this feature.
- Next possible non-destructive check: inspect the actual Garmin Connect Web **Edit Activity** UI for activity `24180037460` to see whether distance is offered; looking without pressing Save does not modify the activity. If absent, keep blocked and consider a separate informational preview instead.

## Garmin Connect Web edit UI — user check 2026-10-13

User opened editing controls for their 2026-08-31 Garmin strength activity and confirmed **no Distance field was visible**. This is a direct user UI observation, not proof that no undocumented backend mutation is possible. Do not assume whole-activity distance can be edited in the supported Garmin Connect UI, and do not implement an undocumented summaryDTO mutation. Keep EGYM bike distance (14,980 m) as informational, **not syncable**, in a future read-only preview. No separate cycling activities or changes to Garmin heart rate/calories. Strength-set correction design remains separately gated by backups, full diff and explicit authorization.

## Confirmed priorities and future recording strategy — 2026-10-13

User clarification:
1. **Primary project priority:** correct historical recorded Garmin **strength exercise weights** against actual EGYM sets. Preserve existing historical activities and all unrelated Garmin data (including cardio sets, timestamps, heart rate and calories). Repetition differences remain a separate reviewable dimension, not an implicit authorization to change reps.
2. **Future training strategy:** record cycling or other cardio **separately from EGYM strength** on Garmin, as appropriate distinct activity types. Do not try to enrich future strength activities by inserting cycling distance. This recording workflow is conceptually distinct from historical strength-data correction.
3. **Historical cycling:** leave existing cycling segments intact; no retroactive splitting, duplicate cardio activities or distance edits. The EGYM cycling research is useful background, but distance sync is explicitly deprioritized.
4. **Write gate unchanged:** build a dry-run preview of old/new historical exercise weights (and separately flag repetition anomalies), validate source semantics including negative/eccentric mode, create/verifiably restore full raw Garmin backup, account for version/concurrency and unsupported API risk, and get explicit per-scope user approval before any Garmin mutation.

This supersedes the earlier hypothetical bike-distance write candidate as a development priority. No Garmin write method has been authorized.
