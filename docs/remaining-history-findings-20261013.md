# Remaining historical comparison — findings 2026-10-13

User executed GET-only `--history-remaining`. Output: EGYM sessions=28, Garmin EGYM activities=29, matched=27, method conflicts=1. August 31 activity 24180037460 already corrected and excluded; 26 sessions inspected; structural errors=0; no Garmin writes.

Among the 26 inspected sessions, 20 have nonzero reported exercise-level weight differences, 6 have none, for 117 reported differing exercise comparisons (not 117 individual sets). Breakdown of the 20:
- 13 have no reported rep discrepancy and no extra Garmin records.
- 5 have rep discrepancies but no extra record, excluding July 8.
- 1 (July 8) has varying EGYM set weights and rep discrepancy; it is a separate special case.
- 1 (June 15) has an extra Garmin UNKNOWN/zero-rep record.

Important edge cases:
- 2026-07-08 (23519342631): EGYM machine 329 Leg Extension has 29 and 28 kg per set; 335 Lat Pulldown 39 and 40 kg per set. EGYM Leg Press reps [19,20] vs Garmin [20,20]. The current per-exercise uniform weight matcher MUST NOT be used to auto-correct this activity.
- 2026-08-05 (23858095280): Garmin first Leg Press strength set has 0 reps while EGYM has 20; preserve rep data during any weight-only updates.
- 2026-06-08 (23176881588): Garmin second Abdominal Crunch strength set has 0 reps; preserve rep data.
- 2026-06-15 (23259077933): Garmin extra UNKNOWN active record, reps 0, no recorded weight; preserve unmatched record and require explicit structural gate.
- 2026-05-04 (22762471069): no weight differences but one extra UNKNOWN record and Chest Press rep discrepancy; no weight correction needed.
- 2026-06-24: MethodConflict; excluded from all proposed corrections.

The cleanest next starting point is 2026-08-17 (Garmin 24006250902): 7 exercise-level weight differences, no rep discrepancies and no extra Garmin record. Still require fresh raw GET, local SHA-256 backup, exact two-set identity matching, full weight-only diff, explicit approval and post-write comparison before any PUT. No multi-activity bulk corrections should run on the basis of this aggregate report alone.
