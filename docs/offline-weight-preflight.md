# Offline Garmin weight-only preflight implementation

Date: 2026-10-13. Branch `feature/3-readonly-matching`.

Implemented `GarminWeightOnlyPreflight.Build(originalJson, eightTargets)` as a pure .NET method. It accepts local Garmin `exerciseSets.json` content, validates expected Aug31 layout (36 records, 2 recognized ACTIVE strength sets for each of 8 exercise identities), creates an in-memory proposed JSON copy and restricts modifications to the `weight` field of mapped strength sets. For the historical fixture: 14 changed set weights across seven exercises; both Lat Pulldown sets remain unchanged. `ValidateOnlyApprovedWeightsChanged` compares the JSON trees, blocking changes to repetitions, bike segments, rests, metadata and unrelated fields.

Unit tests exercise exact expected difference count, unchanged nonweight data, rejection of edits to repetitions and cycling, missing sets and missing original weight.

**Limits:** Current targets are per-machine uniform weights (August 31 fixture), not varying per-set values (e.g. July 8). Unit tests use constructed input, not the user's private actual backup. This component does **not** read the user's disk backup, validate its manifest SHA-256, compare against live Garmin or send HTTP requests. Existing snapshot is not a validated restore mechanism; there is no write authorization. CI and local test outcomes must be checked separately.
