# Functional requirements (draft)

## Inputs
- Latest EGYM completed workouts and their exercises/sets
- Garmin recorded strength activities, exercise sets and two manually created workout templates

## Training assumptions to validate against real payloads
- Warm-up cycling and finishing cardio on Garmin
- EGYM circuit: each exercise twice per visit, but exercise order varies
- Training method changes after six visits; two Garmin workout variants
- EGYM training load can depend on machine strength measurements
- Eccentric sessions may use different positive/negative loads

## Safety rules
1. Start read-only, inspect payloads before any mutation.
2. Exercise mapping is identity-based; unknown/ambiguous exercises block auto-matching.
3. Never use exercise order as the linking key.
4. Preserve all Garmin fields except explicitly approved weight fields.
5. Garmin workout updates and activity exercise-set updates must first be backed up and run through a preview.
6. Require confirmation for every write; verify remote state afterwards.
7. Log decisions and errors, but never credentials or sensitive tokens.
8. Display unmapped / missing training data rather than guessing.
9. Support re-running the sync without unnecessary duplicate updates.
10. Keep cardio and cycling untouched.

## Open questions
- EGYM response format and unit semantics for the two circuit rounds
- Whether training mode and future planned weight can be distinguished
- How sets with different positive and negative loads map to Garmin's single weight
- Exact mapping of the user's Garmin exercise identifiers and EGYM activity identifiers
- Whether registered Garmin sets contain sufficient identity to match without ordering
- Garmin workout template selection logic and historical activity pairing by time
