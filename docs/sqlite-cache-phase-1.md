# Local SQLite cache — implementation phase 1

The Windows-local ASP.NET Core web API now initializes an EF Core SQLite file at
`%LOCALAPPDATA%\EgymGarminSync\sync.db`.

The database model contains:
- `CachedSession`: source, source identifier, UTC timestamp, raw source JSON, checksum, last-fetch time and summary.
- `SyncCheckpoint`: last **completed** import time and source count.
- `GarminSnapshot`: append-only data model for future original/pre-update/post-update activity+exercise-set snapshots, including hashes.

Current endpoints:
- `GET /api/cache/status` returns counts/checkpoints.
- `GET /api/cache/sessions?source=EGYM|Garmin` returns local session summaries.
- `POST /api/cache/import-initial` fetches 180 days of EGYM workouts and recent 100 Garmin activities, then persists them in one SQLite transaction. No Garmin PUT is issued.

Important limitations: **This is not incremental synchronization yet.** The checkpoint is informational and does not imply the upstream API supports modified-since. Garmin's first import saves list metadata only, not the full activity exercise sets. The `GarminSnapshot` table exists but no snapshot is captured during import; it must be populated from actual GETs before any web-based Garmin write. EGYM currently uses a raw JSON SHA-256 content identity because no immutable upstream workout ID has been verified. A revised workout may therefore appear as an additional cached entry, not a safe update. Before making the web UI use the cache as its primary source, resolve stable EGYM identity, inspect upstream incremental query support, and implement the rolling refresh window plus delayed arrival reconciliation.

`Database.EnsureCreated` supports first local development startup only; add migrations before long-term schema evolution. SQLite is local and contains personal fitness metadata; don't commit the DB, expose it to the network or persist credentials in it.

The existing web session dropdowns and comparison still use the original live read paths. Garmin writing remains disabled.


## Rolling sync prototype (phase 2)
`POST /api/cache/sync` re-reads EGYM workouts from 7 days before the previous successful EGYM checkpoint through today, and Garmin's latest 100 activities, then retains activities within the same window. All updates and checkpoints are committed in a single SQLite transaction; Garmin writes remain disabled. `POST /api/cache/import-initial` remains available for a 180-day full refresh.

EGYM records are now keyed provisionally by normalized UTC completion timestamp rather than a hash of their raw JSON, so changes to exercise details do not automatically create a different key. Duplicate completion timestamps in the same import abort the operation. Legacy hash-keyed rows that correspond unambiguously to a fetched timestamp are removed during import. This **is not a verified stable upstream ID**: a revised completion timestamp, missing timezone or older changes can still require reconciliation. Do not treat it as a definitive identity for future Garmin writes.

Garmin still exposes only list metadata in the cache. The first page of 100 activities is **not guaranteed** to cover the seven-day window for high-volume accounts, and neither API currently provides a proven modified-since feed. Treat this endpoint as a rolling refresh prototype, not complete incremental synchronization. Full set-data caching, pagination verification and immutable pre-write snapshots remain separate future work.


## Garmin full exercise sets backfill (phase 3, GET-only)
`POST /api/cache/garmin-sets/import?skip=0&take=5` fetches up to five Garmin EGYM activities from the recent-100 activity discovery list and stores the complete, unmodified `exerciseSets` JSON in a separate mutable `GarminSets` table, including its SHA-256 and fetch time. Use successive batches (skip 5, 10, etc.) and inspect `GET /api/cache/status` (`cachedGarminSets`). `GET /api/cache/garmin-sets/{activityId}` returns the cached payload only, without contacting Garmin. Import requires pre-existing Garmin session metadata in SQLite.

The first successful fetch also creates an append-only `GarminSnapshot` of kind `FirstObserved` with metadata and exact sets JSON and checksums. Subsequent imports may update the mutable cache but never overwrite this first-observed snapshot. This does NOT mean original Garmin values prior to historic modifications: the verified August 31 correction already happened. Snapshotting immediately before future PUTs still needs separate development and validation.

Existing databases are extended non-destructively with `CREATE TABLE IF NOT EXISTS GarminSets` on API start, because the initial development database was created using EF `EnsureCreated`. Formal EF migrations are still needed before production. The import currently fetches the Garmin activity list once per batch, so this is bounded backfill rather than true incremental or fully automatic synchronization. Page ordering may change if new activities appear between batches. On individual failure, earlier committed batch items stay cached; rerun the failed batch. The web comparison is not yet switched to SQLite.

## SQLite-first web UI (phase 4)
On page load the React UI reads GET /api/cache/sessions?source=EGYM and source=Garmin and GET /api/cache/status, without calling the external providers. GET /api/cache/compare?egymId=...&garminId=... compares stored EGYM raw JSON with the cached Garmin exerciseSets raw JSON. It retains per-exercise identity checks and recorded Garmin set order. The **Synchroniseren** button executes POST /api/cache/sync and reloads cached lists. Existing GET-only comparison and Garmin preview endpoints are retained; the preview intentionally validates against *fresh* Garmin values rather than trusting a potentially stale cache. Actual Garmin writes remain disabled.

Limitations: rolling sync currently updates session metadata only; it does NOT automatically refresh existing Garmin exercise-set cache entries. The existing batches endpoint is still required for set backfill/refresh. EGYM identity remains provisional and backend matching suggestion uses conservative UI heuristics until centralized matching is implemented.

## Rolling sync now refreshes Garmin sets
For every Garmin EGYM activity discovered inside the rolling overlap period, `/api/cache/sync` fetches its full exerciseSets payload, validates that an array is present and compares its SHA-256 with the mutable `GarminSets` cache. A new activity gets an initial `FirstObserved` snapshot only if none exists. Existing snapshots are never changed. Fetches complete before the SQLite transaction begins, and failures do not advance checkpoints or change cached sets. A successful response includes `garminSetsRefreshed` (number fetched, not necessarily changed). The Garmin discovery cap of 100 and the 7-day overlap still apply; edits to older workouts require explicit refresh/backfill. No Garmin PUT is enabled.


## Pre-write preparation (no Garmin writes)
`POST /api/sync/preview` still validates selected field edits against a fresh Garmin GET without saving a backup. New `POST /api/sync/prepare` accepts the same `{garminId,edits:[{setIndex,field,expected,value}]}` contract, confirms the selected Garmin activity is in recent EGYM activities, validates *every selected field* against the freshly fetched exerciseSets JSON, and stores an append-only `Preflight` snapshot with SHA-256 values. The response contains a snapshot ID and before/proposed SHA-256 digests; no proposed payload is sent to Garmin or persisted as a restore source. The snapshot includes the fresh exact exerciseSets response and locally cached **list metadata**, not an independently fetched full Garmin activity payload; this limitation matters for later full restoration. The UI exposes an explicit **Preflight-backup maken** action after selecting draft edits.

`GET /api/sync/restore-plan/{activityId}` returns only metadata, checks whether each stored snapshot still hashes correctly and whether its sets hash matches the currently cached sets. `canRestore=false` by design. A real pre-write backup must be captured again immediately before a future PUT; this earlier preflight does not guarantee the activity remains unchanged. Future restoration requires validation of the Garmin replace-all schema, fresh GET, explicit confirmation and server readback. The web `/api/sync` remains hard-disabled.

## Offline update rehearsal
`POST /api/sync/rehearse` accepts `{"snapshotId":30,"edits":[{"setIndex":2,"field":"repetitionCount","expected":12,"value":11}]}` (replace all example identifiers and values with real ones from the selected preflight and preview). It NEVER calls Garmin. It reads a `Preflight` snapshot, verifies checksums of metadata and complete exercise-sets JSON, enforces the existing draft-field validation and full-object no-collateral-change invariant, and returns hashes of the original and proposed payload without exposing the workout JSON. It reports a recovery source is present, **not** that restore can be executed. Failed integrity checks abort with conflict, and the database is not mutated. Use `GET /api/sync/restore-plan/{garminId}` to check individual snapshots and their stored hash integrity. Garmin POST/PUT and actual restore remain disabled.

This is an offline rehearsal of both weight and repetition changes, **not a Garmin API write-contract validation**. Before enabling writing, separately validate the full Garmin PUT payload schema, capture an immediately-before-write snapshot, enforce optimistic concurrency with fresh reads, and verify server readback and recovery behavior.

## Offline Garmin replace-all contract validation
The offline `POST /api/sync/rehearse` now additionally builds the *normalized 36-set replacement payload* with `GarminTrialPayloadNormalizer.Normalize` and passes it through `GarminExerciseSetsWriteContract.Validate`. It returns `replaceAllContractValid` and a SHA-256 digest of the normalized payload, without exposing the raw JSON. This reuses the structure proven by the previous controlled **weight-only** August 31 update. It does **not** establish that Garmin accepts or persists repetitionCount modifications: no Garmin PUT, subsequent readback or real restore is performed. The normalization currently requires exactly 36 sets and will intentionally refuse differently structured activities. Before a general write feature, review that hard-coded contract and validate the full activity metadata backup, transaction coordination and live rollback strategy.

## Full activity backup and offline recovery validation
The explicit `POST /api/sync/prepare` now reads both fresh Garmin resources (`/activity-service/activity/{id}` and `/activity-service/activity/{id}/exerciseSets`), validates the target activity ID and normalized complete replacement contract, then saves a new append-only `FullPreflight` snapshot of both raw responses and hashes. Earlier `Preflight` backups contain session-list metadata, not full activity JSON; they remain unchanged and distinguishable. This is a preparation-time backup, NOT the final immediately-before-PUT backup; any future write handler must re-read, validate and save a new snapshot just before issuing a PUT.

Offline `POST /api/sync/rehearse` supports existing `Preflight` and new `FullPreflight` snapshots, and independently validates a normalized replacement and a normalized restore payload derived from immutable snapshot data. Response includes `restorePayloadContractValid`, `restorePayloadSha256`, and `fullActivityBackup`. `GET /api/sync/restore-plan/{id}` exposes `fullActivityBackup` and checksum status for each snapshot. It does not claim recovery will work with the undocumented Garmin API. No writes or restore endpoints are enabled, so **a real post-PUT readback and live rollback are still untested and unimplemented**.

## Single-repetition live-trial CLI (implementation, NOT yet approved for execution)
The CLI now contains an isolated `--live-trial-one-repetition` action hardcoded to Garmin activity `24032098016`, set index `2`, repetitionCount `15 -> 14`. It discovers the specific EGYM activity, downloads full fresh activity metadata and exercise-sets GETs, creates a unique disk backup folder under `%LOCALAPPDATA%\\EgymGarminSync\\live-trials` containing activity.json, exerciseSets.json, proposed.json and manifest.json, verifies their hashes, and prints the backup location. **A Garmin PUT is possible only after typing exactly `APPLY REPS 24032098016` in the console.** Immediately after that confirmation, it re-fetches metadata and all sets and rejects changes or hash mismatches before issuing at most one full replace-all PUT. It never automatically retries. Readback then verifies all sets and activity metadata; unexpected results stop without another write. The existing web write endpoint stays disabled.

**DO NOT RUN this command until the user separately approves a live repetition write.** The implementation itself is not evidence that Garmin accepts repetition changes, and Garmin restore is not implemented or guaranteed. After uncertain PUT outcome, do not rerun: inspect Garmin and preserve backup. The existing 31 August corrected activity is outside the hardcoded target.
