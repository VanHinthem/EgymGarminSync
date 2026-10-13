# User-authorized historical live trial scope — 2026-10-13

The user explicitly permits using the **2026-08-31 Garmin activity 24180037460** for a live mutation trial, despite residual restoration risk. This supersedes the earlier requirement that all trials use only disposable activities. It does **not** mean that a write has occurred, and it is not blanket authorization to rewrite every set or activity.

Proposed minimal round-trip trial: change **one Chest Press strength set** from Garmin 22 kg to the EGYM-history 21 kg, GET/readback all sets and activity metadata, then restore that exact set to 22 kg and verify. Confirm exact proposed set identity (one of two Chest Press records), actual update endpoint and replacement payload semantics, and the user-approved scope before running an actionable command. The endpoint may replace **all 36 sets** even when only one weight changes; therefore preserve every record and field and detect any unexpected server-side normalization.

Existing safeguards: original activity JSON/exerciseSets snapshot verified locally with SHA-256; separate original FIT export; offline structural weight-only preflight passed. Neither artifact guarantees full restoration. No Garmin write method has been implemented or executed at this time.

Stop immediately upon ambiguous set identity, checksum mismatch, unexpected GET state, unsupported method/payload, failed server response or unintended readback differences. Never attempt repeated speculative writes. Do not alter heart rate, calories, bike segments, REST records, timestamps or templates.
