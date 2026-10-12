# EGYM API exploration — verified findings

Observed 2026-10-12 using the .NET 10 read-only client on branch `feature/1-egym-readonly-client`. Source: user's actual console outputs; values below are **observations, not simulated fixtures**. No credentials or tokens were included.

## Connectivity

- Email-based Netpulse gym discovery succeeded after allowing HTTPS `Branding.plist` download URLs with query parameters.
- Authentication, MWA bearer-token retrieval and GET requests succeeded.
- The first **30-day** search returned 0 workouts and 0 strength measurements. This is not an API failure.
- A **365-day** search returned 82 workouts and 116 strength measurement entries.
- A **90-day** search returned 8 workouts and 8 strength measurement entries.
- Responses from `get_workouts` are not guaranteed chronological; sort on `completedAt` descending in UI.
- Correlation between `workouts` and `strengthMeasurements` is **not yet proven**; equal 90-day counts are not evidence of one-to-one identity.

## API endpoints confirmed operational

| Method | Endpoint | Observed shape |
|---|---|---|
| GET | `/workouts/v1.0/workouts` | Array of workouts with `exercises` |
| GET | `/measurements/v1.0/strength` | Object containing `strengthMeasurements` array |

These are unofficial EGYM member endpoints and could change.

## Training modes observed in 90-day response

| Date (as returned; timezone not independently verified) | Method |
|---|---|
| 2026-08-31 | `NEGATIVE_REPBASED` |
| 2026-08-19 | `NEGATIVE_REPBASED` |
| 2026-08-17 | `NEGATIVE_REPBASED` |
| 2026-08-07 | `REGULAR_REPBASED` |
| 2026-08-05 | `REGULAR_REPBASED` |
| 2026-07-20 | `REGULAR_REPBASED` |
| 2026-07-17 | `REGULAR_REPBASED` |
| 2026-07-15 | `REGULAR_REPBASED` |

Inference: these methods distinguish the user's two workout variants. Actual Garmin template identifiers/mapping remain to be verified. The inferred training-method change occurred **between August 7 and August 17**, not necessarily on either date.

## Latest returned workout

- `completedAt`: `2026-08-31T08:18:57.454`
- 10 exercise entries: two `Bike Ergometer` entries (activity ID `31`) enclosing eight strength machine entries.
- The eight strength entries each contain two sets, each with `numberOfReps = 15` and equal reported `weight` between sets.
- For all 16 recorded strength sets, `trainingMethod = NEGATIVE_REPBASED` and `sideMode = BOTH`.
- Bike entries: first has five set records, last has three; strength-related values (`weight`, `numberOfReps`, `trainingMethod`) appear null/empty.
- 10 exercise entries does **not** mean 10 strength exercises; eight are strength and two are bike segments.

| Exercise label | EGYM `activity.activityId` | Sets | Reps/set | Reported `weight` per set |
|---|---:|---:|---:|---:|
| EGYM Seated Row | 334 | 2 | 15 | 32.0 |
| EGYM Chest Press | 333 | 2 | 15 | 21.0 |
| EGYM Leg Curl | 332 | 2 | 15 | 23.0 |
| EGYM Back Extension | 331 | 2 | 15 | 44.0 |
| EGYM Leg Extension | 329 | 2 | 15 | 22.0 |
| EGYM Lat Pulldown | 335 | 2 | 15 | 33.0 |
| EGYM Abdominal Crunch | 330 | 2 | 15 | 30.0 |
| EGYM Leg Press | 581 | 2 | 15 | 95.0 |

**Weight units:** the API was called with `measurementSystem=METRIC`, and values are likely kilograms, but the returned set field shown in the output did not include a unit. Confirm exact resistance semantics, especially for eccentric/negative training, before writing to Garmin.

## Relevant JSON structure

Workout: `exercises`, `completedAt`, `timezone`, `templateGroupId`, `workoutPlanName`, `workoutPlanImageUrl`, `workoutPlanGroupType`.

Exercise: `id`, `label`, `activity`, `sets`, `editable`, `deletable`, `completedAt`, `timezone`, `sourceType`, `source`, `origin`, `kiloCalories`, `points`, `hrmData`, `matrixSprint8`, `partnerData`, `summary`, `netpulseWorkoutExerciseId`, `netpulseWorkoutId`.

Nested `activity`: includes `activityId`, localized `name`, `category`, and many metadata fields. **Never log the complete `activity` object**; it is extensive and produces noisy output.

Set: `setType`, `numberOfReps`, `weight`, `duration`, `distance`, `heartRate`, `speed`, `incline`, `recoveryTime`, `trainingMethod`, `sideMode`.

## Confirmed vs assumptions / unresolved

**Confirmed:** read-only access; historical workout retrieval; identifiable exercise IDs; two strength sets in the sampled negative session; numeric weight values; two observed training methods; bike endpoints are identifiable.

**Not confirmed:**
- Whether `weight` represents positive resistance, negative resistance, a reference load, or a conversion of the machine's effective load in `NEGATIVE_REPBASED`. Never assume this is directly Garmin-compatible.
- Whether the eight `activityId` values stay identical across all sessions and software upgrades (use mapping with verification).
- Exercise/set correspondence between Garmin and EGYM, especially when equipment order changes.
- What `workoutPlanName`, `workoutPlanGroupType` and `templateGroupId` contain on actual data.
- Whether the number of EGYM circuit rounds and the Garmin set count always agree for incomplete sessions.
- Which workout method maps to which of the **two Garmin user-created templates**; verify from Garmin.
- Whether the strength-measurement endpoint adds information relevant for sync beyond workouts.
- Feasibility and details of updating Garmin recorded activity sets and workout templates; both APIs are unofficial.

## Next work

1. Keep issue #1 open until build/test validation and acceptance criteria are evaluated; the current client has no automated tests yet.
2. For issue #2, retrieve Garmin workouts and registered strength activity/sets **read-only**.
3. Design exercise mapping by EGYM `activityId` and Garmin exercise identity; never match by variable equipment order.
4. Before writes, agree the interpretation of eccentric weight and show all decisions in a user-approved preview.
5. Keep warm-up and finishing cycling untouched, and preserve complete Garmin records on any eventual replace-all updates.
