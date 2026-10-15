# Garmin historical strength restoration feasibility — 2026-10-13

**Research only. No Garmin writes or deletion.**

## Findings

1. Garmin officially supports exporting an existing activity in its **original format**, usually FIT, from Garmin Connect Web. This provides a stronger archive of the device-generated activity than the current JSON snapshot, but does not establish that all subsequent edits to Garmin Connect exercise sets are included. Source: https://support.garmin.com/en-US/?faq=W1TvTPW8JZ6LfJSfK512Q8
2. An unofficial library documents `set_activity_exercise_sets` for updating **existing recorded strength activities** and warns that this operation **replaces all sets**, including watch-detected sets. We must preserve all 36 original Aug31 exercise-set entries, not just 16 strength entries. Source: https://github.com/Taxuspt/garmin_mcp
3. Another unofficial Garmin library documents a strict payload including `activityId` on root/sets/exercises, `setType` and `startTime`. These details are third-party reports, not a stable supported Garmin contract. Source: https://github.com/DynamicsNinja/garminconnect-js/blob/main/AGENTS.md
4. Reports exist of Garmin FIT re-import losing strength set details, so an exported FIT file must not be called a verified rollback for Garmin Connect edited exercise sets. Source: https://forums.garmin.com/apps-software/mobile-apps-web/f/garmin-connect-web/406059/copying-existing-records

## Safe decision

- Continue read-only; do not submit any write, including an experimental identical-content PUT.
- Ask user to **export original** August 31 strength activity in Garmin Connect Web via activity detail > gear > Export Original. Keep the downloaded file locally out of Git; optionally inspect structure without sharing personal data.
- Design set-level diff that changes **only weight** and retains exactly the original ACTIVE, REST and INDOOR_BIKE set records, order and metadata.
- Verify compatible update semantics and test a restore using a disposable/test activity **rather than mutating a valuable historical activity**, if feasible. Even a round-trip restore test is no proof of full Garmin-server restoration of related aggregates.
- Hard stop remains until we demonstrate an acceptable rollback mechanism, get explicit approval, and ensure live readback/diff checks.
- Current `--backup-aug31` only captured JSON via JsonElement, checksummed on local disk; it is not FIT export or a proved restoration plan.
