# Disposable Garmin test activity snapshot (GET-only)

Use a **separately created disposable Garmin strength activity**, not a historical EGYM workout. Identify its Garmin Connect activity ID. The snapshot command does not prove that an arbitrary ID is disposable; user must verify that.

After CI is green:

```powershell
git pull
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --snapshot-test-activity <ID>
```

Safeguards: accepts one positive numeric activity ID, rejects the two known historical example IDs, requires the ID to appear in the recent 100 Garmin activities, and rejects activities whose name contains 'EGYM' (case-insensitive). **Other historical activities might still pass these guards**: only select a deliberately created test entry. All requests are GET-only. The command reuses the local backup snapshot logic and writes a uniquely named directory under `%LOCALAPPDATA%\EgymGarminSync\backups` containing activity JSON, exercise sets JSON and SHA-256 manifest. No mutation, restore or deletion endpoint is implemented.

A snapshot is not proof that rollback is possible. Before any write trial, inspect set structure and prove the complete restoration pathway, and obtain explicit user approval for the exact disposable activity and modification.
