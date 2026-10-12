# Garmin read-only discovery — issue #2

This is an initial experiment to verify authentication, list workout templates and recent activities. **No Garmin write methods are invoked**.

```powershell
$env:GARMIN_EMAIL = "you@example.com"
$secure = Read-Host "Garmin password" -AsSecureString
$env:GARMIN_PASSWORD = [System.Net.NetworkCredential]::new("", $secure).Password
dotnet run --project src/EgymGarminSync.Garmin.Cli/EgymGarminSync.Garmin.Cli.csproj
```

Credentials are read from the current shell environment. Never commit or share them. MFA handling and token persistence need live validation.

Library under evaluation: [sealbro/dotnet.garmin.connect](https://github.com/Melser/net-garminconnect). It is an **unofficial** API client. NuGet version `Unofficial.Garmin.Connect` 0.10.0 is published; compilation needs CI verification, and workout templates plus activity exercise sets still need real-data validation. The reference library may not support reading exercise sets; if not, extend the adapter with a narrowly scoped read-only call, avoiding write endpoints.

Next: inspect the two Garmin workout templates' step identifiers and recent strength activity exercise sets, define redacted schemas, and map against EGYM IDs. Do not modify any historical activity or template until explicit user confirmation and reliable backups.
