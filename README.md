# EGYM Garmin Sync

Self-hosted web app to compare EGYM circuit sessions and Garmin strength-training records, review weight changes, and selectively synchronize them.

## Scope
- Read recent EGYM workouts and Garmin activities / exercise sets.
- Match exercises by identity, never by order. EGYM circuit has two rounds; station order varies.
- Support two user-created Garmin workout templates for different training methods.
- Keep cycling warm-up / finishing cardio, repetitions, set counts and all other unrelated Garmin data unchanged.
- Show differences, allow manual corrections and require explicit approval before any Garmin writes.
- Optionally update a recorded activity, a workout template or both.
- Back up original payloads before replace-all updates, audit writes, and verify server state.
- Never commit credentials or authentication tokens.

## Proposed stack
ASP.NET Core (.NET 10), React and TypeScript; later Docker deployment on a NAS. Python repositories are reference implementations only, **not runtime dependencies**.

## Stages
1. Read-only API exploration and real JSON payload inspection.
2. Exercise identity mapping and training-method detection.
3. Web comparison UI with dry-run previews.
4. Approved, backed-up Garmin updates.
5. Optional automatic polling; no unattended writes by default.

## References
- [Unofficial EGYM API client](https://github.com/marcelpoelstra/python-egym)
- [Unofficial Garmin Connect API client](https://github.com/cyberjunky/python-garminconnect)

See [functional requirements](docs/requirements.md). Both APIs are unofficial and must be validated against real account data.
