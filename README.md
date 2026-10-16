# EGYM Garmin Sync

Self-hosted application for comparing completed **EGYM strength-circuit training** with recorded **Garmin Connect strength activities**, and selectively correcting Garmin's **recorded exercise-set weights and repetitions**. Runs as a .NET 10 API and React/TypeScript UI in one Docker container, with SQLite for local history, snapshots and the write journal.

> **Personal-use software, not an official EGYM or Garmin product.** Both services use unofficial APIs; upstream changes may break synchronization. No automatic Garmin writes.

## What it does

1. **Synchronize** recent completed EGYM sessions and Garmin activities. The first synchronization initializes the database *and imports Garmin exercise sets*; later runs refresh recent activity data.
2. **Match sessions** by training method (EGYM positive/negative versus the corresponding Garmin name) and completion time. A Garmin activity must be a unique match within the supported window; ambiguous sessions are not guessed.
3. **Compare sets** by known exercise identity, not exercise order. The UI shows kg/repetition differences and lets you navigate workouts needing attention. The two circuit rounds are matched separately; cardio is not edited.
4. **Review changes** to Garmin's *recorded activity*, select the fields to adopt from EGYM and confirm in the UI.
5. **Safely write to Garmin** only after an explicit **Ja, opslaan** choice: create a backup/snapshot, verify live preconditions and whole-payload hashes, record a write journal, perform a guarded single PUT and read back the full remote activity. Successful verification refreshes the local comparison automatically. **Nee, annuleren** cancels a still-unused preparation.

Unknown exercises, ambiguous mappings or an unexpected Garmin payload require manual attention. A network failure after a possible PUT is **Uncertain**, never silently retried: inspect and reconcile the journal before another write. Older Garmin activities can fall outside the latest-100 discovery window. The initial import normally looks back 180 days; incremental synchronization rechecks recent activity data.

**Not supported:** updating planned Garmin workout templates, automatically correcting workouts without confirmation, or treating exercise order as a reliable exercise ID.

## Deploy with Docker (Linux NAS)

Requirements: Docker Engine with Compose, outbound HTTPS for both APIs, a persistent volume and a Garmin account *without an unimplemented MFA challenge*. Images for `linux/amd64` and `linux/arm64` are published by GitHub Actions on pushes to `main`.

```sh
cp docker-compose.example.yml docker-compose.yml
cp .env.example .env
openssl rand -base64 32
```

Set the four account variables in `.env` and place the generated **Base64** key in `GARMIN_TOKEN_KEY`. Keep this key unchanged across upgrades; losing it makes the encrypted Garmin token cache unreadable. Keep `.env` out of version control and limit access to it.

```sh
docker compose pull
docker compose up -d
docker compose ps
```

Open the UI via the address/port you expose in Compose or through a trusted local reverse proxy. The example deliberately binds **127.0.0.1:5187** on the Docker host; from another machine this port is unavailable without changing the binding or using a proxy. For example, on a trusted LAN-only host, use `192.168.x.x:5187:5187` instead, substituting your host IP.

**Security: the current API has no application-level authentication. Do not expose it directly on the public internet.** Serve only on a trusted local network, or enforce authentication and HTTPS at an upstream reverse proxy. Whoever can access the API may initiate synchronization and, if writing is enabled, interact with sensitive write endpoints. Protect the SQLite volume and all backups as private user data.

Click **Synchroniseren**: a fresh database bootstraps itself; Garmin exercise sets are included. No manual import command is normally needed. Status and troubleshooting:

```sh
curl -fsS http://127.0.0.1:5187/api/health
docker compose logs --tail=100 egym-garmin-sync
```

Use the address configured on your host if you changed the loopback binding. On failed upstream import the server reports the failing phase without logging credentials or token contents.

### Persistence, upgrades and backups

The `./data:/data` bind mount holds SQLite (`sync.db`), immutable Garmin snapshots, write journal and the encrypted Linux OAuth token cache (`data/Garmin`). Back up **the entire data directory and the separately stored encryption key**, preferably with the container stopped for a consistent SQLite backup. Do not delete the mount when redeploying.

`ghcr.io/vanhinthem/egymgarminsync:latest` tracks `main`. For predictable upgrades, pin the image to a `vX.Y.Z` release tag where available. GHCR may require authentication when packages are private.

### Enabling writes deliberately

The example Compose file disables writes. On Linux, enabling them requires **both** `GARMIN_WRITE_ENABLED=1` and `GARMIN_LINUX_WRITE_ENABLED=1` in the container configuration, *after* you have checked that read-only synchronization and the guarded verification flow work. These switches are deployment controls, **not a substitute for endpoint authentication**. Every actual write still needs an explicit per-activity UI confirmation; never auto-retry an uncertain result.

Windows development uses the original per-user DPAPI Garmin token cache; Linux uses a separately keyed AES-256-GCM token cache. Windows token files cannot be copied into the Linux cache.

## Publishing a release

After the `main` CI and Docker build are green, open **Actions → Publish Docker image → Run workflow**, select `main` and enter `v1.0.0` (or a later `vX.Y.Z`) in **release_version**. The workflow publishes the versioned GHCR image and then creates the GitHub Release for that exact commit. Do not rerun an existing release version; bump the version for subsequent releases. Until that workflow has succeeded, `v1.0.0` is **not** an available image tag.

## Local development

Install .NET SDK 10 and Node.js 22 or newer supported by the frontend. Configure `EGYM_EMAIL`, `EGYM_PASSWORD`, `GARMIN_EMAIL` and `GARMIN_PASSWORD` in the backend's *own environment* (do not commit them).

```powershell
dotnet run --project src/EgymGarminSync.Web
```

In a second terminal:

```sh
cd web
npm install
npm run dev
```

The Vite development UI is served at `http://127.0.0.1:5173` and proxies `/api` to the local .NET backend on port 5187. Writes default to disabled. For local Windows-only write testing, explicitly set `GARMIN_WRITE_ENABLED=1` **in the backend process** after verifying your intended changes.

## Repository guide

| Location | Responsibility |
| --- | --- |
| `src/EgymGarminSync.Web/` | ASP.NET Core API, import/cache, SQLite, guarded write workflow and recovery |
| `src/EgymGarminSync.Egym/` | EGYM client |
| `src/EgymGarminSync.Matching/` | Exercise/set matching, validation and preflight logic |
| `src/EgymGarminSync.Garmin.Cli/` | Garmin transport, token caching and diagnostic CLI experiments |
| `src/EgymGarminSync.Cli/` | Early EGYM exploration CLI |
| `web/` | React/TypeScript review UI |
| `tests/` | Matching and EGYM regression tests |
| `.github/workflows/ci.yml` | .NET, UI and guarded-write tests |
| `.github/workflows/docker.yml` | GHCR image publication |

For implementation safety context, see [Garmin update contract](docs/garmin-exercise-set-update-contract.md) and [backup/recovery design](docs/garmin-backup-recovery-design.md). Historic experiments can be retrieved from Git history; they are not deployment instructions.

## Key safeguards

- Never commit passwords, token caches, personal workout exports, snapshots or `.env`.
- No Garmin write without a fresh backup, explicit approval, single guarded PUT and verified readback.
- An uncertain write is **not** a safe invitation to retry. Preserve journal and backups.
- Do not manually modify SQLite write-journal states or overwrite the encrypted token cache.
- Verify behavior in a non-production scenario before changing write logic.

License: no separate license is presently declared; refer to repository ownership before redistributing.
