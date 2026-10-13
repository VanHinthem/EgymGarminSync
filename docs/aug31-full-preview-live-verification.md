# August 31 fresh 14-set preview verified — 2026-10-13

User ran `--refresh-preview-aug31` successfully after the one-set live update and restore trial for Garmin activity `24180037460`.

New local snapshot:
- `activity.json`: 3808 bytes, SHA-256 `B1C79FD118B852B53AA2AD81BAECBCC84E79224457F1543D9F81ED97FE13B0E1`
- `exerciseSets.json`: 32852 bytes, SHA-256 `86E410CA599B4178AF66867438E61A0B9D62026F508D9E6AA30E405D70A332B8`

The exercise-set SHA-256 matches the pre-trial original snapshot exactly. The activity JSON hash differs; the prior read-only metadata diff identified Garmin's addition of `summaryDTO.totalSets=36`. Both fresh files passed independent manifest SHA-256 verification.

Offline review succeeded: 14 changed set weights over 7 exercises, identical two-set targets:
- EGYM 329 Leg Extension: Garmin 20 -> EGYM 22 kg; set indices 12, 28.
- EGYM 330 Abdominal Crunch: 24 -> 30 kg; indices 8, 24.
- EGYM 331 Back Extension: 43 -> 44 kg; indices 14, 30.
- EGYM 332 Leg Curl: 22 -> 23 kg; indices 16, 32.
- EGYM 333 Chest Press: 22 -> 21 kg; indices 2, 18.
- EGYM 334 Seated Row: 31 -> 32 kg; indices 4, 20.
- EGYM 581 Leg Press: 89 -> 95 kg; indices 6, 22.
- EGYM 335 Lat Pulldown remains unchanged at 33 kg.

Validator confirmed no proposed non-weight changes, and the proposal remained in memory. No Garmin PUT/POST occurred in this run. This is *not* approval to perform the 14-set update; require explicit scope approval and safe fresh-state guard before implementing/running it.
