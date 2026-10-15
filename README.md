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

**Linux Garmin authentication:** the container uses an AES-256-GCM encrypted
OAuth token cache in the persistent `./data/Garmin` directory. Generate a
persistent 32-byte key (Base64) and supply it as `GARMIN_TOKEN_KEY`:

```sh
openssl rand -base64 32
```

Keep this key in a secrets manager or a protected local environment file,
never in Git. Do not change it between deployments: an incorrect key refuses
to load the token cache (no silent reset). Windows continues to use DPAPI.

Live Garmin writes remain disabled by default. Linux additionally requires
both `GARMIN_WRITE_ENABLED=1` and `GARMIN_LINUX_WRITE_ENABLED=1`, along with
the existing explicit per-write approval and full readback checks. **Do not
enable live writes on NAS until a read-only login/import and isolated
verification have passed on the NAS itself.** The Windows DPAPI token file
cannot be migrated directly. Use a new login with your credentials instead.

The GitHub package may initially be private; configure NAS GHCR authentication
if the registry denies an anonymous pull.
