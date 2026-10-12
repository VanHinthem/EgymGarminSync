# First EGYM read-only slice

Run from the repository root with .NET 10 SDK installed:

```bash
dotnet run --project src/EgymGarminSync.Cli
```

Set `EGYM_EMAIL`, `EGYM_PASSWORD`, `EGYM_BASE_URL` in the **process environment** first; never commit or paste credentials. `EGYM_BASE_URL` is the HTTPS Netpulse gym base URL. Automatic gym URL discovery from EGYM's Branding.plist is **not implemented** in this first slice. Obtain the URL locally through authorized means.

The command requests the past 30 days of workouts and prints only their completion timestamps and exercise counts.

## Limitations / follow-up
- The unofficial API is unverified with the owner's gym account; authentication and mobile-app compatibility require a live test.
- Validate exercise payloads, set weights, units, circuit rounds and the mode representation on real data.
- Add user-authorized gym discovery, retry for expired Netpulse sessions, tests with mocked HTTP, and richer diagnostics once the first connection is proven.
- Do **not** enable Garmin writes during API exploration.
