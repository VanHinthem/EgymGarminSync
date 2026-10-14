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
