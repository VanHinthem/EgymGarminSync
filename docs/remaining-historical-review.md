# Remaining historical sessions: read-only review

The complete 2026-08-31 Garmin activity 24180037460 was corrected and independently verified. The remaining-session review deliberately excludes that activity by ID rather than inferring its state from dates or workout names.

Run after CI passes:

```powershell
git pull
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj -- --history-remaining
```

This invokes the existing EGYM/Garmin 180-day comparison and inspects up to 30 matched sessions, skipping the corrected August 31 activity. It reads source sets and Garmin exercise sets but does not modify either service. The comparison explicitly surfaces method conflicts, unmatched sessions, incomplete circuits, rep discrepancies, extra Garmin records and differing EGYM weights between the two sets. It does not infer an unproven mapping for varying EGYM set weights and must not automatically create corrections from these reports.

The historical Garmin activity enumeration still uses the latest 100 activities; counts may be lower than the previously observed 27 matched sessions if the recent activity window shifts. A failed EGYM login or malformed session should not be interpreted as no differences. Do not commit credentials, API JSON or console logs with sensitive content.
