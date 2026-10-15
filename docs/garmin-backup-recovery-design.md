# Garmin historical strength correction — backup and recovery design

Status: **design only**, 2026-10-13. No write commands authorized or implemented.

## Objective
Preserve the complete original Garmin activity before changing any historical strength weight. Garmin/EGYM app-displayed resistance values are the proposed target; EGYM weight-phase semantics and Garmin single-weight meaning remain caveats.

## Minimum read-only capture per Garmin activity ID
- Raw JSON response: `GET /activity-service/activity/{id}` (activity metadata, summary, Garmin IDs and timestamps).
- Raw JSON response: `GET /activity-service/activity/{id}/exerciseSets` (every ACTIVE and REST set, bicycle portions, all nested exercise metadata and exact original numeric values).
- Raw associated workout template response where available, including nested repeat-group steps; archive **without modifying** the template.
- Original Garmin FIT activity export if a supported download is available; keep the original bytes intact. Do not call a JSON snapshot an equivalent to a FIT file.
- Capture request timestamps, activity ID, content lengths, SHA-256 digests, and capture result/status in a local manifest. Avoid access tokens, passwords or personal data in logs or Git.

## Local storage and integrity
Keep backups **on the user's Windows machine**, outside the Git repository, in an access-restricted directory. Do not upload training/health data or authentication secrets to GitHub. Store raw response bytes as delivered (not parsed/re-serialized for backup) and verify file hashes after writing and on a separate read. Clearly label any endpoint without an export as missing instead of treating the backup as complete.

## Required pre-write safeguards
1. Capture all available original objects before editing and verify their integrity from disk.
2. Compare freshly fetched Garmin activity state against backup immediately before a proposed write; abort on divergence.
3. Identify target sets unambiguously and display **set-level** old-to-new weight in Garmin units; no index-based matching.
4. Require a complete diff proving no intended changes to heart rate, calories, repetitions, timings, cycling or REST records.
5. Confirm documented/validated supported update and *recovery* path, including a controlled test that restores the original state; a raw GET/JSON backup does **not** guarantee it can be re-imported or restored.
6. Ask for explicit approval of exact activity and candidate changes before each initial write.
7. Re-read Garmin after any write to verify actual persistence and detect unintended changes. Abort all further changes if the re-read differs unexpectedly.

## Current blockers
- Official/supported write/recovery mechanism for an already recorded strength activity has **not** been established.
- FIT export availability and completeness have **not** been verified.
- The raw `exerciseSets` endpoint yields 36 records for 2026-08-31, including 16 strength ACTIVE sets, two cycling ACTIVE entries and REST records. A replacement must preserve all of them.
- Existing weight-only preview presents one aggregate weight per exercise; it does not yet represent varying per-set weights such as observed July 8.
- EGYM app and API have been cross-checked for Seated Row, Chest Press and Leg Curl on the negative session, but the concentric/eccentric semantic remains undocumented.

## Next implementation slice (GET-only)
A narrowly scoped local `backup-activity` command that fetches the two raw Garmin endpoints and writes a timestamped immutable local snapshot, manifest and SHA-256 checks. Do not use the command name or UI to imply that restoration is available until separately demonstrated. Export FIT only if a safe supported download is confirmed. Keep Garmin API requests GET-only.
