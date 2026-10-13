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


## Windows authentication persistence (issue #2)

- The read-only Garmin CLI uses `WindowsGarminTokenCache` through the library's `ITokenCache` constructor overload.
- OAuth1 and OAuth2 tokens are persisted under `%LOCALAPPDATA%\\EgymGarminSync\\Garmin\\<SHA-256-of-normalized-email>.dpapi`. The file is encrypted with Windows DPAPI `CurrentUser`; it only decrypts under the same Windows user context. Do **not** copy it to GitHub or between machines.
- OAuth2 expiry is stored as an absolute UTC timestamp, with a 60-second expiry buffer. When expired, the Garmin library can exchange a cached OAuth1 token to obtain OAuth2 without a full SSO login.
- The library can still require SSO after a fresh install or if OAuth1 is revoked. No cache can recover a token from a previous run that did not persist it.
- After `OAuth1TicketNotFound`, the CLI writes a local 30-minute login cooldown marker and exits early on subsequent runs. This avoids repeated SSO attempts; it does not bypass Garmin's own restrictions.
- No Garmin writes are added. This branch is still read-only.
- Windows DPAPI is Windows-specific. The cross-platform CI build compiles the client, but **live authentication and DPAPI roundtrips must be tested on Windows**.
- Password remains in the current process environment; remove it after running with `Remove-Item Env:GARMIN_PASSWORD` if desired. Never print or commit credentials.
- If credentials are needed again later, open a fresh shell and supply them interactively. Do not delete the token cache merely to troubleshoot a temporary authentication block.

### Verification on Windows

After CI passes, run once and check for `Using Windows DPAPI encrypted Garmin OAuth token cache.` and success at Step 1. On a following run under the same Windows account, the cache should be reused. A 30-minute local cooldown after an OAuth1-ticket rejection is intentional; do not repeatedly retry while it is active.
