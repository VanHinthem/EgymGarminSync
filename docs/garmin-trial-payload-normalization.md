# Aug31 Garmin update payload normalization — read-only finding

On 2026-10-13 the live read-only preflight for Garmin activity 24180037460 returned exactly 36 sets (18 ACTIVE, 18 REST) and 54 nested exercise entries. The root activityId was present and correct; all 36 set-level activityIds and all 54 nested-exercise activityIds were **absent**, not conflicting. All 36 startTime fields were present and parseable. The existing JSON snapshot matched live Garmin before constructing a single Chest Press weight-only change, index 2, 22000 to 21000 raw Garmin weight units.

Implemented pure offline `GarminTrialPayloadNormalizer.Normalize`: only adds missing activityId values from the verified root to the 36 sets and 54 nested entries, refuses conflicting existing IDs, requires validator acceptance, then strips those additions from a temporary copy and checks all other data is structurally identical to the weight-only candidate. Original snapshot remains untouched. Added unit tests.

**This verifies the inferred update-payload shape locally, not that Garmin accepts the request or restores original server state. No PUT has been performed.** Next: green CI and live GET-only normalized shape check; only after those results consider a separately gated, user-authorized write-and-rollback trial.
