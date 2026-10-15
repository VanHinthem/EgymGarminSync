# August 31 full strength-weight correction — gated live commands

Approved scope (2026-10-13): correct 14 recorded Garmin set weights across 7 EGYM exercises on the existing 2026-08-31 activity **24180037460**. Leave Lat Pulldown, repetitions, cardio, REST sets, heart rate, calories, timestamps and templates unchanged. No other activity approved.

The already verified post-roundtrip snapshot is identified by SHA-256:
- activity.json B1C79FD118B852B53AA2AD81BAECBCC84E79224457F1543D9F81ED97FE13B0E1
- exerciseSets.json 86E410CA599B4178AF66867438E61A0B9D62026F508D9E6AA30E405D70A332B8

Two commands (DO NOT run before green CI and review):
- `--apply-weights-aug31`: check exact backup and live Garmin state, print all 14 diffs, require interactive `APPLY 24180037460`, re-check server state immediately, issue one full 36-set PUT, verify full sets and activity metadata via GET.
- `--restore-weights-aug31`: only when live sets match the exact 14-weight change, require `RESTORE 24180037460`, then send original 36-set weights and read back.

The verifier tolerates only Garmin's already observed addition of `summaryDTO.totalSets=36` and request-only echoed activityId values. Other differences cause a stop; no automatic retry, no automatic restore, and no blind writes after uncertain failures. Both commands rely on an unofficial Garmin replace-all endpoint and a narrowly validated known sample. The original FIT export is kept by the user. Backup data remain local.

This commit implements the live-capable commands but **does not itself execute them**. Green CI, user review and the interactive confirmation are required before an actual Garmin mutation.
