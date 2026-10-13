# Local Garmin activity snapshot CLI

Command (Windows, after `git pull`):

```powershell
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --backup-aug31
```

This is a GET-only, **non-restorable-until-proven** snapshot for Garmin activity `24180037460`. It reads two existing Garmin JSON endpoints: the whole activity and its recorded exercise sets. It saves `activity.json`, `exerciseSets.json`, and `manifest.json` under `%LOCALAPPDATA%\EgymGarminSync\backups\<unique capture folder>`. The manifest records file lengths, SHA-256 checksums and the caveat below. Files are re-read and hashes verified. If a fetch or verification fails, the incomplete capture directory is deleted. It neither stores tokens nor writes to Garmin.

**Limits:** the unofficial Garmin client deserializes the response into `JsonElement` first, so saved `GetRawText()` is a JSON snapshot, not certified byte-identical HTTP response bytes. A Garmin FIT export and full restoration/re-import have **not** been achieved, nor have workout templates been captured by this initial command. A successful run is **not** permission to edit an activity. These local files contain personal workout data; keep them private and out of Git.

Status: implementation committed; CI and live user Windows test pending.
