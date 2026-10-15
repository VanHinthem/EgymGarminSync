# First live Garmin single-set trial and restore commands

Added 2026-10-13. **No live write was executed by development.** These are *destructive-capable* commands; never run them merely to inspect data.

- `--live-test-aug31` prepares one Chest Press ACTIVE set (saved index 2) from 22000 to 21000 raw Garmin weight units and sends a **complete 36-set replace-all PUT** only after the exact interactive phrase `CONFIRM 24180037460`.
- `--live-restore-aug31` is a separate command that prepares the full original 36 sets from the immutable snapshot, and likewise requires interactive confirmation.

Both commands locate the original snapshot by the independently observed specific SHA-256 values of `activity.json` and `exerciseSets.json`, check the manifest and file contents, require Garmin's live set state to equal the expected before-state, compare activity metadata, normalize required activityIds, and validate the outgoing shape before prompting. Following PUT they GET sets and activity metadata, with fail-closed classification. No automatic retry or automatic rollback is attempted, because server state may be uncertain after errors.

**Limitations:**
- PUT replaces all 36 Garmin sets and relies on an unofficial endpoint and request format. Garmin may normalize metadata, and a valid HTTP response is not proof of safe full restoration.
- If the first PUT unexpectedly changes anything else, the restore command may correctly refuse: do not try blind retries or force original JSON without investigation.
- The OAuth-capable dependency may retry on 401/403. Re-read before any retry after an ambiguous failure.
- This trial does **not** authorize any other session changes or bulk weight synchronization.
- Local snapshots and the user-held FIT export remain essential; neither guarantees restoration.
- CI and an explicit review of the first test output are required before attempting a live mutation.

User granted explicit permission earlier to use activity 24180037460 as a live test despite residual restoration risk, but each command still prompts for active consent before its PUT.
