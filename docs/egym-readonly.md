# First EGYM read-only slice

Run from the repository root with .NET 10 SDK installed:

```bash
dotnet run --project src/EgymGarminSync.Cli
```

Set `EGYM_EMAIL` and `EGYM_PASSWORD` in the **process environment** first; never commit or paste credentials. `EGYM_BASE_URL` is now optional: when absent, the client discovers the backend through EGYM's user and gym configuration endpoints and parses `Branding.plist` (XML or binary) using `plist-cil`. You can override discovery with `EGYM_BASE_URL`.

The command requests the past 30 days of workouts and prints only their completion timestamps and exercise counts.

## Limitations / follow-up
- The unofficial API is unverified with the owner's gym account; authentication and mobile-app compatibility require a live test.
- Validate exercise payloads, set weights, units, circuit rounds and the mode representation on real data.
- Add user-authorized gym discovery, retry for expired Netpulse sessions, tests with mocked HTTP, and richer diagnostics once the first connection is proven.
- Do **not** enable Garmin writes during API exploration.

## Verified findings

See [EGYM API findings](egym-api-findings.md) for real-world observations from October 12, 2026, including the distinction between confirmed payload facts and unresolved Garmin mapping/weight semantics.
