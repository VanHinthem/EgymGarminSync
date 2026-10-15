# August 31 single-set live trial: guarded preparation

Implementation date: 2026-10-13. **No Garmin write endpoint is called or enabled.**

`--trial-preflight-aug31` checks the saved August 31 Garmin JSON snapshot's manifest and SHA-256 hashes; fetches current Garmin activity metadata and all recorded sets using GET; and rejects the trial if either resource differs structurally from the saved snapshot. It then verifies 36 sets, exactly two Chest Press ACTIVE records at indices 2 and 18, each with original weight 22000 (Garmin raw units). It constructs an in-memory candidate changing **index 2 only** to 21000, then performs a full-tree difference check allowing only that one weight field. No JSON replacement file is saved, no PUT/POST occurs, and there is no restore code.

On Windows (after green CI and `git pull`):

```powershell
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --trial-preflight-aug31
```

If live Garmin metadata or sets have changed since backup, the command must refuse. Any future write requires an exact confirmed PUT endpoint/payload contract, independently reviewed full 36-set payload, fresh immutable backup, and a carefully controlled round-trip with immediate readback. The user authorized a narrow live trial on this historical activity but the present command is still **GET-only** and cannot modify Garmin.

## Live Windows preflight verified — 2026-10-13

User executed `--trial-preflight-aug31` successfully. Local backup manifest and SHA-256 checks passed, and a fresh Garmin GET confirmed the saved activity metadata and all 36 exercise-set records matched the current server response. The single proposed change was Chest Press set index 2, `22 -> 21 kg`. The in-memory proposal passed full structural comparison, with no other field changes. This was read-only: **no PUT, POST, restoration, or Garmin mutation**. Next phase remains explicit endpoint/payload verification and controlled execution/readback/rollback of the user-approved single-set trial.
