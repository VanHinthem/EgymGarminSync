# Garmin read-only discovery — issue #2

This is an initial experiment to verify authentication, list workout templates and recent activities. **No Garmin write methods are invoked**.

```powershell
$env:GARMIN_EMAIL = "you@example.com"
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj
```

The experiment currently prompts for a password in the terminal and supports an MFA code. Prefer a private terminal; the password input is **visible** in this initial version, so do not screen-share or copy its contents into reports. Token persistence is not configured.

Library under evaluation: [Melser/net-garminconnect](https://github.com/Melser/net-garminconnect). It is an **unofficial** API client. NuGet package/API compatibility needs CI verification, and workout templates plus activity exercise sets still need real-data validation. The reference library may not support reading exercise sets; if not, extend the adapter with a narrowly scoped read-only call, avoiding write endpoints.

Next: inspect the two Garmin workout templates' step identifiers and recent strength activity exercise sets, define redacted schemas, and map against EGYM IDs. Do not modify any historical activity or template until explicit user confirmation and reliable backups.
