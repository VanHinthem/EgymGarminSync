# Garmin Connect read-only findings — 2026-10-12

Verified with real Garmin Connect data via the .NET read-only CLI. No write requests were made.

## Existing workout templates
- **EGYM Positief:** workout ID `1464321438`
- **EGYM Negatief:** workout ID `1464319904`
- Both contain one segment with four top-level workout steps, including first and last `INDOOR_BIKE` steps and an inner `RepeatGroupDTO`.
- Inner repeat group has `numberOfIterations = 2` and 16 child steps: eight resistance exercises alternating with eight rest steps. It is therefore **not** 16 different resistance exercises.
- Both variants currently use the same exercise categories and identities but different reps and weights (observed positive 20 reps, negative 15 reps, in the strength steps).
- The standard third-party typed `GarminWorkoutStep` lacks nested `workoutSteps`; retain raw JSON to preserve repeat-group children.
- Garmin uses `category` and sometimes `exerciseName` to identify exercises. Some `exerciseName` values are null; map using the pair, and reject ambiguous mappings. Workout `stepId` is unique to a template, not a cross-template exercise key.

## Registered strength activities
- Negative example: activity ID `24180037460`, workout on 2026-08-31.
- Positive example: activity ID `23883291887`, workout on 2026-08-07.
- GET `/activity-service/activity/{id}/exerciseSets` returns `exerciseSets`. The third-party `GetActivityExerciseSets` method instead requests `/activity-service/activity/{id}`, the **wrong endpoint** for our use.
- Both sampled activities have 36 records including `ACTIVE` and `REST` sets. Some `ACTIVE` entries represent bicycle sessions; do not treat all ACTIVE entries as strength exercise sets.
- Sets contain `repetitionCount`, `weight`, `setType`, `startTime`, and an `exercises` array; the latter may repeat the same named exercise several times and must **not** be interpreted as multiple set records.
- In these samples, activity `weight` appears in grams: `33000.0` for Lat Pulldown corresponds to `33.0` in the Garmin template and EGYM source. Confirm with more pairs before writing.

## Demonstrated mismatch
- Negative Chest Press: Garmin registered `22000.0` (likely 22 kg), while EGYM logged 21.0 for 2026-08-31. This is a candidate for an eventual **reviewed correction**, not an authorized update.
- Also distinguish **template weights** (planned settings) from **recorded activity weights**, as they can diverge independently.
- The same activity can contain a different exercise order from EGYM. Match by stable exercise identity, never by array index.

## Garmin template strength step pairs and sampled planned weights
| Garmin category | Garmin exerciseName | EGYM exercise | EGYM activityId | Pos planned | Neg planned |
|---|---|---|---:|---:|---:|
| BENCH_PRESS | null | Chest Press | 333 | 27 | 22 |
| ROW | SEATED_CABLE_ROW | Seated Row | 334 | 36 | 31 |
| SQUAT | LEG_PRESS | Leg Press | 581 | 103 | 89 |
| CRUNCH | WEIGHTED_THORACIC_CRUNCHES_ON_FOAM_ROLLER | Abdominal Crunch | 330 | 30 | 24 |
| PULL_UP | LAT_PULLDOWN | Lat Pulldown | 335 | 36 | 33 |
| CRUNCH | WEIGHTED_LEG_EXTENSIONS | Leg Extension | 329 | 23 | 20 |
| HYPEREXTENSION | WEIGHTED_STATIC_BACK_EXTENSION | Back Extension | 331 | 51 | 43 |
| LEG_CURL | WEIGHTED_LEG_CURL | Leg Curl | 332 | 27 | 22 |

This is an **observed candidate mapping**, not yet validated across all sessions.

## Pending
- Verify unit semantics in Garmin source and EGYM negative resistance data before Garmin writes.
- Print all sets in the compact CLI (the former debug output truncated arrays after 25 items).
- Validate cross-system workout matching by local dates/times, and address missing or extra sets.
- Any eventual Garmin PUT must retain unchanged REST, cycling, nested group and unrelated metadata, make backups, show a full diff and require explicit approval.
