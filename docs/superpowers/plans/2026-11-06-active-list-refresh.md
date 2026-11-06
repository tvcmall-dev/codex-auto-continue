# Active List Refresh Optimization

**Goal:** Remove development-framework wording from README, open the executing filter by default, and show newly executing conversations promptly.

**Diagnosis:** The reader emits millisecond-precision user-message evidence before second-precision turn-start evidence. The recovery engine rejects the latter as older, leaving the conversation unknown until actual output arrives. Original SQLite queries over 120 conversations take about 21-25 ms locally, so query execution is not the primary cause. Initial baselining also deliberately suppresses fresh events, hiding conversations already running until their next output.

**Approach:** Align new-turn start evidence with its same-turn user-message timestamp without advancing beyond coalesced output. Compute a display-only running snapshot from an in-progress turn with nonempty model output or a completed tool within two minutes; keep it non-fresh, expire it on subsequent reads, and never use it to confirm pending delivery. Retain historical-failure baselining and all dispatch guards.

- [x] Remove README framework references and rename screenshots while preserving valid links.
- [x] Make initial filter, heading, highlight and empty state match the executing filter.
- [x] Reproduce timestamp-order and initial-display failures in deterministic reader tests.
- [x] Fix evidence alignment and recent-activity snapshots without extra per-thread queries.
- [x] Verify stale/future data, pending continuation and coalesced execution safeguards.
- [x] Measure UI snapshot-copy overhead and change it only if evidence warrants.
- [x] Reuse one UI snapshot for details and coalesce duplicate refresh callbacks; validate clone counts and filter behavior.
- [x] Run behavioral checks, inspect the executing view and package 0.2.1-preview.
- [x] Publish verified artifacts.

**Validation:** Use isolated databases and synthetic sessions. Do not stop or replace the user's running monitor during verification. Preserve the existing requirement that recovery confirmation needs actual execution evidence.

**UI measurements before the change:** With 120 synthetic sessions and 100 events each, UpdateView performs three full-session clones and takes about 505 ms. Each details tick performs another full clone and takes about 168 ms. A normal poll causes two view renders, so repeating JSON copies can occupy the UI for about one second per poll.

**UI verification after the change:** One view render takes one snapshot (about 165 ms in the same fixture); a details tick takes zero snapshots (about 0.05 ms). A normal refresh and background Changed bursts each coalesce to one render. Pending callbacks take no snapshot after closure. Executing/all/attention filters, empty state and search are verified without showing windows or taking the user's application mutex.

**Live read-only check:** A first read of 120 actual desktop conversations took 92 ms, immediately exposed one recently running conversation and emitted zero fresh events, preserving the startup recovery baseline.

**Final checks:** Build has zero warnings/errors and all 10 behavioral suites pass. Packaged-EXE isolated UI verification passes all filter, snapshot, queue and close checks; its 120-session/100-event fixture takes about 163 ms per full view and 0.05 ms per details tick. Empty assistant placeholders cannot hide recent completed tools or model output, including behind 128 placeholders; their fallback snapshots never emit fresh recovery evidence. Streaming SQLite tests verify early termination and statement disposal. Independent review finds no remaining P1/P2. The user's existing process remains running; the full updater process fixture was not repeated because it requires the same application mutex.

**EXE SHA-256:** `8facff4a4ec8e188f94175e51cbc58dcb873b3f8804596939dead9fda3b1ad16`.

**Release:** Published [v0.2.1-preview](https://github.com/tvcmall-dev/codex-auto-continue/releases/tag/v0.2.1-preview) from source commit `f5db4bcc5d0947aa7f79b5140447c9e3ac5c909f`. EXE/ZIP/checksum-list upload digests match local files. The production updater selected this version from 0.2.0-preview and downloaded/validated the tested EXE; the public checksum list has two correctly formatted lines matching both artifacts.
