# August 31 Garmin weight round-trip

On 2026-10-13 user ran the confirmed one-set test followed by the separate restore on Garmin activity 24180037460. The first operation changed Chest Press set index 2 from 22 to 21 kg; exercise-set readback reported ChangedOneWeight. Garmin added summaryDTO.totalSets=36 to activity metadata. No other set differences were observed.

The restore command returned readback state Original and printed RESTORE VERIFIED. The user independently checked Garmin Connect and observed that Chest Press returned to 22 kg.

This demonstrates update and restoration for this particular set and activity, not all historical sessions. Complete activity metadata is not byte-identical because Garmin introduced totalSets=36. No bulk sync was performed.
