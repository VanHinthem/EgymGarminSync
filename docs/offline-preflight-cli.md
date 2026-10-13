# Offline August 31 snapshot preflight command

After green CI, on Windows run:

```powershell
git pull
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --offline-preflight-aug31
```

This command **does not authenticate with Garmin or contact the network**. It selects the newest local activity-24180037460 backup folder, reads its manifest and verifies the byte lengths and SHA-256 of `activity.json` and `exerciseSets.json`. It loads the saved 36-set JSON and uses the date-specific observed EGYM weights to construct and validate an **in-memory only** proposal of exactly 14 weight-field adjustments. It reports the original set indices and old/new kilograms for review; other fields and sets are structurally verified unchanged. It does not save a modified JSON file or call Garmin write endpoints.

Known limitations: EGYM weight targets are a fixed observation for this *one* known session, not a general sync rule; the Garmin weight unit assumption is based on observed conversion. The original FIT file is a separate user-held backup. Neither JSON snapshot nor FIT export is a proven complete restoration path. No Garmin writes are authorized.
