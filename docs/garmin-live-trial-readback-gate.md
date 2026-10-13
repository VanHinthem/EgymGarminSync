# Garmin single-set trial — server readback gate (2026-10-13)

A new pure `GarminOneSetReadback.Classify(originalJson, liveJson)` classifies observed Garmin server sets as:
- `Original`: unchanged relative to the protected local snapshot;
- `ChangedOneWeight`: only Chest Press at saved index 2 differs, raw 22000 -> 21000;
- `Unexpected`: any other change (REST, cycling, repetitions, weight, metadata, order or count) blocks further automated action.

The comparator permits Garmin to echo request-only `activityId` on each set/nested exercise by stripping those additions in a **temporary comparison copy**. The original snapshot is not altered. Fixture tests check accepted and rejected states. This is purely offline logic: no Garmin PUT was called.

Caution: Readback comparison needs review against a real server response after an authorized test, because the Garmin endpoint may normalize fields other than activity IDs. It is not proof of reversibility. The low-level PUT transport remains disconnected from CLI commands; making live changes requires orchestration with mandatory backup integrity check, fresh state, explicit confirmation, one network PUT, readback and separately authorized restore. **No user test command has been enabled yet.**
