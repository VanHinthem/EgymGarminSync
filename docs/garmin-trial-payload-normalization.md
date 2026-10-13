# Aug31 Garmin update payload normalization — read-only finding

On 2026-10-13 the live read-only preflight for Garmin activity 24180037460 returned exactly 36 sets (18 ACTIVE, 18 REST) and 54 nested exercise entries. The root activityId was present and correct; all 36 set-level activityIds and all 54 nested-exercise activityIds were **absent**, not conflicting. All 36 startTime fields were present and parseable. The existing JSON snapshot matched live Garmin before constructing a single Chest Press weight-only change, index 2, 22000 to 21000 raw Garmin weight units.

Implemented pure offline `GarminTrialPayloadNormalizer.Normalize`: only adds missing activityId values from the verified root to the 36 sets and 54 nested entries, refuses conflicting existing IDs, requires validator acceptance, then strips those additions from a temporary copy and checks all other data is structurally identical to the weight-only candidate. Original snapshot remains untouched. Added unit tests.

**This verifies the inferred update-payload shape locally, not that Garmin accepts the request or restores original server state. No PUT has been performed.** Next: green CI and live GET-only normalized shape check; only after those results consider a separately gated, user-authorized write-and-rollback trial.

## Verified Windows run — 2026-10-13

User ran `--trial-preflight-aug31` against live Garmin activity 24180037460. Fresh GETs matched both backed-up activity metadata and 36 set records; candidate was Chest Press set index 2, 22 to 21 kg. Diagnostic inventory reported root activityId valid, 36 missing set activityIds, 54 missing nested exercise activityIds, zero mismatched IDs and zero missing or invalid set startTimes. The offline normalizer reported `Offline update-shape normalization PASSED`, keeping the backup intact and preserving all unrelated values. No PUT/POST or restoration was attempted; actual Garmin acceptance remains unproven.
