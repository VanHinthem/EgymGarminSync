# EGYM negative training: resistance semantics research

Research date: 2026-10-13. Scope: historical strength-weight correction only; no Garmin writes.

## Confirmed from official EGYM sources

EGYM's negative (eccentric-enhanced) training applies **higher resistance during the eccentric movement** than during the concentric movement, automatically changing load at the turning point. EGYM's example for chest press: 51 kg concentric, 85 kg eccentric, against a 101 kg maximum strength.

Sources:
- https://knowledge.egym.com/nl/manuals-and-compliance-documentation/smart-strength-series-3-owner-s-manual/training-software-and-methods.html
- https://uk.egym.com/en-gb/blog/egym-negative-training-method
- https://sites.google.com/egym.de/egymus/home/egym-workouts/onboarding-guide/egym/training-methods

## Unresolved EGYM API semantics

For 2026-08-31, EGYM `NEGATIVE_REPBASED` set `weight` for Chest Press is 21.0 under a METRIC request; Garmin recorded 22000 (apparently grams). The EGYM API response exposes one numeric `weight` per set, not an explicitly labeled concentric/eccentric pair. Thus **we cannot presently determine which phase, if either, that EGYM field corresponds to**. The published training method describes the equipment behavior but does not define this specific API field.

Do not infer concentric or eccentric loads, a percentage multiplier, or a safe Garmin replacement value from these facts alone. Keep the 7 observed Aug31 differences in read-only preview, explicitly non-write-ready.

## Validation required before historical changes

1. Identify official API field semantics or inspect a paired real-world EGYM machine/app view that labels the training method's displayed resistance.
2. Validate a negative session plus a regular positive session without inferring an eccentric factor from one exercise.
3. Confirm Garmin activity set weight unit and the intended single-weight representation, then implement an independently reviewable diff and full backup/restore gate.
4. No writes or activity modifications without explicit user approval.
