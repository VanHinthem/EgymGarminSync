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
