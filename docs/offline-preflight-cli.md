# Offline August 31 snapshot preflight command

After green CI, on Windows run:

```powershell
git pull
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --offline-preflight-aug31
```

This command **does not authenticate with Garmin or contact the network**. It selects the newest local activity-24180037460 backup folder, reads its manifest and verifies the byte lengths and SHA-256 of `activity.json` and `exerciseSets.json`. It loads the saved 36-set JSON and uses the date-specific observed EGYM weights to construct and validate an **in-memory only** proposal of exactly 14 weight-field adjustments. It reports the original set indices and old/new kilograms for review; other fields and sets are structurally verified unchanged. It does not save a modified JSON file or call Garmin write endpoints.

Known limitations: EGYM weight targets are a fixed observation for this *one* known session, not a general sync rule; the Garmin weight unit assumption is based on observed conversion. The original FIT file is a separate user-held backup. Neither JSON snapshot nor FIT export is a proven complete restoration path. No Garmin writes are authorized.

## Verified Windows execution — 2026-10-13

The user successfully ran `--offline-preflight-aug31` against the locally saved Garmin activity `24180037460` snapshot. Both saved JSON files passed manifest SHA-256 checks. The offline preflight reported **14 changed set weights across seven exercises**, with the expected two indexed sets for each machine: 329 [12,28], 330 [8,24], 331 [14,30], 332 [16,32], 333 [2,18], 334 [4,20], 581 [6,22]. The structural validator reported no non-weight changes, and the candidate JSON remained in memory. No Garmin network calls or writes were made. A full restoration path remains unverified.
