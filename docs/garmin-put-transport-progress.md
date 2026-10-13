# Garmin exerciseSets PUT transport — implementation status

Date: 2026-10-13. Branch `feature/3-readonly-matching`.

The exact unofficial Garmin Connect update endpoint was independently confirmed from the live `python-garminconnect` implementation:
`PUT /activity-service/activity/{activityId}/exerciseSets`. Its documented semantics **replace the complete set collection**, not just a changed item. Source: https://github.com/cyberjunky/python-garminconnect/blob/master/garminconnect/__init__.py (`set_activity_exercise_sets`).

The project's .NET dependency `Unofficial.Garmin.Connect` 0.10.0 provides `GarminConnectContext.MakeHttpPut<TBody>(url, body)`, using the existing Garmin OAuth context; source: https://github.com/sealbro/dotnet.garmin.connect/blob/main/Garmin.Connect/GarminConnectContext.cs.

An isolated internal `GarminExerciseSetsReplaceTransport` was added with the verified endpoint and a single authenticated PUT call. **No CLI command references this method; it cannot currently be executed through our program. No Garmin mutation has occurred.** This transport alone is *not* a safe update workflow; the library has internal auth-refresh retries.

Before exposing the first live command, enforce:
- Valid immutable snapshot manifest/hash and preserved original 36 sets, activity metadata, and user-held FIT export;
- fresh GET equals backup immediately before sending;
- fully normalized request with the verified root/set/exercise activity IDs;
- sole authorized field modification: Chest Press set index 2, raw Garmin weight 22000 to 21000;
- complete server GET/readback with full structural diff after change;
- separately callable restore to the original 22000 with the same fresh-state protections and post-restore verification;
- strict one-time confirmation and no uncontrolled repeat attempts on ambiguous errors.

**Status:** low-level implementation committed, CI result not yet checked, and live command intentionally not enabled.
