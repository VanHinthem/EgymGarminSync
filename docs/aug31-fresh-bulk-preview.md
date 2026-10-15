# August 31 fresh snapshot and complete weight-only review

After the successful isolated Chest Press update/restore, Garmin Connect showed the original 22 kg again. The server additionally generated activity metadata summaryDTO.totalSets=36; old activity summary snapshots need not be byte-identical. No bulk correction has been performed.

New **GET-only** command for a fresh review:

```powershell
git pull
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --refresh-preview-aug31
```

The command captures a **new** local activity.json and exerciseSets.json with a SHA-256 manifest, then checks the newest snapshot and prints the Aug31 14-set weight-only preview. It does not call PUT/POST, does not mutate any existing snapshots, and does not run the existing single-set live-test command. It stops if the fresh snapshot or structural preview fails. The expected scope is 14 changed weights from seven mapped exercises (two sets per exercise); Lat Pulldown remains unchanged. This is *only* a review.

Caution: the EGYM weights are currently the date-specific validated historical observations embedded in the read-only preflight; this command does not re-fetch EGYM. Per-set differences on other dates need their own implementation. A fresh review is not authorization for a 36-record replacement write; the next bulk-capable update must independently compare its live state to the selected verified snapshot, obtain precise approval, and verify all resulting set and summary data.
