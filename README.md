# EGYM Garmin Sync

Self-hosted web app to compare EGYM circuit sessions and Garmin strength-training records, review weight changes, and selectively synchronize them.

## Scope
- Read recent EGYM workouts and Garmin activities / exercise sets.
- Match exercises by identity, never by order. EGYM circuit has two rounds; station order varies.
- Support two user-created Garmin workout templates for different training methods.
- Keep cycling warm-up / finishing cardio, repetitions, set counts and all other unrelated Garmin data unchanged.
- Show differences, allow manual corrections and require explicit approval before any Garmin writes.
- Optionally update a recorded activity, a workout template or both.
- Back up original payloads before replace-all updates, audit writes, and verify server state.
- Never commit credentials or authentication tokens.

## Proposed stack
ASP.NET Core (.NET 10), React and TypeScript; later Docker deployment on a NAS. Python repositories are reference implementations only, **not runtime dependencies**.

## Stages
1. Read-only API exploration and real JSON payload inspection.
2. Exercise identity mapping and training-method detection.
3. Web comparison UI with dry-run previews.
4. Approved, backed-up Garmin updates.
5. Optional automatic polling; no unattended writes by default.

## References
- [Unofficial EGYM API client](https://github.com/marcelpoelstra/python-egym)
- [Unofficial Garmin Connect API client](https://github.com/cyberjunky/python-garminconnect)

See [functional requirements](docs/requirements.md). Both APIs are unofficial and must be validated against real account data.

## Docker / GHCR (experimental Linux packaging)

Every push to `main` builds and publishes multi-architecture images to
`ghcr.io/vanhinthem/egymgarminsync:latest` and `sha-<commit>` using
[Publish Docker image](.github/workflows/docker.yml).

Copy `docker-compose.example.yml` to `docker-compose.yml`, set
`EGYM_EMAIL`, `EGYM_PASSWORD`, `GARMIN_EMAIL` and `GARMIN_PASSWORD`
in the deployment environment, then run `docker compose up -d`.
The example binds HTTP to host loopback `127.0.0.1:5187` for a local
reverse proxy. Store persistent SQLite data in `./data`; never discard
or overwrite that directory during upgrades.

**Not yet ready for NAS production sync:** Garmin OAuth caching currently
uses **Windows DPAPI** and the import/write paths require Windows. The
Linux container can serve its web UI, health endpoint and persisted cached
data, but cannot sign in to Garmin or perform the normal import/write workflow
until a secure Linux-compatible token cache and platform guards are implemented.
Do not copy a Windows `.dpapi` token cache into Linux. Writes stay disabled
in the Linux build. The image and compose example are deployment scaffolding,
not a declaration that Garmin synchronization works on Linux yet.

The GitHub package may initially be private; configure NAS GHCR authentication
if the registry denies an anonymous pull.
