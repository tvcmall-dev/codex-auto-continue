# Automatic Update Implementation Plan

**Goal:** Check this public project's GitHub releases, automatically download verified newer EXEs and restart when monitoring and the UI are idle, as selected by the user.

**Architecture:** A fixed-origin release client handles SemVer/channel selection, bounded HTTPS download and SHA-256 verification. A separate copy of the current EXE waits for the old process, replaces only the installation EXE atomically, retains a backup and requires a startup receipt from the new process. Failed startup rolls back and suppresses automatic reinstallation of that version for 24 hours. Application data and Codex data remain outside replacement.

**Policy:** Startup and six-hour automatic checks; automatic idle installation enabled by default. No restart during message dispatch, polling, notification delivery, manual webhook tests, settings editing or any enabled scheduled automatic continuation. No admin elevation. Include preview releases by default while this app ships previews; users can choose stable only. Network failure never stops monitoring. Existing 0.1.0 users must install this first update-capable release manually once.

- [x] Add settings and update UI, status, progress, manual check and restart controls.
- [x] Test then implement SemVer, trusted release selection/download, redirects, digest validation and malformed metadata handling.
- [x] Test then implement idle/quiesce coordination, startup/failure status and bounded automatic retry behavior.
- [x] Test then implement separate-process replacement, health receipt and rollback without touching settings/monitor.db.
- [x] Verify actual staged-process upgrade and rollback using disposable fixture installations; run existing suite and packaged EXE smoke checks.
- [x] Version package metadata and document behavior.
- [x] Publish the update-capable preview and verify source/artifact hashes.

**Verification:** Release build has zero warnings/errors; all 10 behavioral suites pass (89 recovery checks and 59 release-client checks). The final packaged EXE passes smoke startup and the real separate-process success/rollback fixtures, including unchanged settings/SQLite data and the 24-hour suppression boundary. Independent review's mutex-ownership and staged-payload pairing findings have regression coverage and are fixed. EXE SHA-256: `0ce89ee03bae972aaf6a2e8f0c6453c3402100d9f201c9170385fa9e4a165e1d`.

**Release:** Published [v0.2.0-preview](https://github.com/tvcmall-dev/codex-auto-continue/releases/tag/v0.2.0-preview) from commit `5f6fb4ab4280888c0b37d57130911ad7539b2363`. Uploaded EXE, ZIP and checksum-list asset sizes/digests match local files. The production release client independently selected the public update from 0.1.0-preview, downloaded it and verified its SHA-256, x64 identity and product version against the tested package.
