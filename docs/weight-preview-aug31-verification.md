# Verified historical weight preview — 2026-10-13

The user successfully executed `--weight-preview-aug31` locally against Garmin activity `24180037460`. The read-only match is complete; all 8 exercises have matching repetitions [15,15]. The preview suggests 7 differences, with no writes.

| EGYM machine | Garmin kg | EGYM kg |
|---|---:|---:|
| 329 Leg Extension | 20 | 22 |
| 330 Abdominal Crunch | 24 | 30 |
| 331 Back Extension | 43 | 44 |
| 332 Leg Curl | 22 | 23 |
| 333 Chest Press | 22 | 21 |
| 334 Seated Row | 31 | 32 |
| 581 Leg Press | 89 | 95 |

Lat Pulldown (335) matches at 33 kg and is omitted from candidates.

This is NOT WRITE-READY. Negative-method resistance semantics, full backup/restore and explicit authorization are required before editing Garmin data. Repetitions, cardio, HR, calories, times and templates are excluded from changes.
