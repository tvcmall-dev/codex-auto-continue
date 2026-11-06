# Windows Desktop Implementation Plan

> **For agentic workers:** Use subagent-driven-development or executing-plans to implement this plan task-by-task.

**Goal:** Deliver a native dark net48 WPF monitor with real local Codex observations, conservative continuation, persistent settings, webhook notifications, and a portable EXE.

**Architecture:** Desktop-owned SQLite projections are read-only observations, never a separately started app-server. Only fresh confirmed terminal failures can become eligible; queue delivery remains pending until actual subsequent execution evidence. Core policy stays separate from WPF, storage, and transport. Production startup contains no fictional sessions.

**Tech Stack:** C# 7.3, WPF, .NET Framework 4.8, Windows SQLite (`winsqlite3.dll` on Windows 10/11), DPAPI, HTTPS Webhooks, existing authenticated Codex CLI.

## Tasks

- [x] Establish CLI/backend capabilities and exact local event schemas. Validate desktop observations separately from continuation transport; show a precise connection status.
- [x] Implement `Core/RecoveryEngine.cs` and deterministic console tests: internal retries, stale failures, interruption, approval, newer task, recovery evidence, bounded incident attempts, pending/restart protection, and all language modes. Run tests before and after implementation.
- [x] Implement `Infrastructure/SqliteDatabase.cs`, `StateStore.cs`, `SettingsStore.cs`: parameterized SQLite, bounded history, atomic DPAPI-protected settings, no Codex database mutation. Test persistence and credential protection.
- [x] Implement `Integration/LocalCodexReader.cs` and `CliSender.cs`: read desktop projection, watch fresh turn/item changes, validate thread UUID, use safe Windows argument quoting, treat resume warnings and uncertain delivery as pending. Test with synthetic databases/process fixtures.
- [x] Implement `Notifications/WebhookClient.cs` and bounded queue: provider payloads/signatures, business error parsing, HTTPS validation, no redirects, sanitized diagnostic output, three-attempt budget and deduplication. Test against deterministic handlers rather than real recipients.
- [x] Translate approved dashboard to native `MainWindow.xaml` plus settings window, state vector illustrations, filters, inspector, monitoring controls, tray, language and notification settings. Confirm production/demo modes are clearly distinguished.
- [x] Integrate in `ApplicationService.cs`: serialize polling and sending, persist dispatch intent first, recheck state before sending, cancel unsafe retries, update UI on Dispatcher, finite independent notification processing.
- [x] Build Release x64, run all console tests and desktop smoke tests, inspect dark UI, package portable EXE and ZIP, document installation, connection limits and reproducible build. Publish source and artifacts after verification.

## Acceptance

Build command: `dotnet build src/CodexAutoContinue/CodexAutoContinue.csproj -c Release`.
Tests: `dotnet run --project tests/CodexAutoContinue.Tests/CodexAutoContinue.Tests.csproj -c Release` (net48 console harness with nonzero failure exit).
Do not claim live continuation verified without observing same-conversation new execution. If no desktop control endpoint exists, report it directly and retain pending delivery safely.

## Implementation Record

Real local read acceptance passed: 120 desktop-owned primary conversations and zero fresh historical replay at baseline. Current desktop app-server is stdio-only and its default control socket is unavailable. No same-conversation live resume was performed or verified; this is an explicit preview-release limitation rather than a completed live-connection gate.

SQLite implementation uses Windows 10/11 built-in `winsqlite3.dll` instead of distributing a NuGet native SQLite dependency. The product remains native net48 with no external runtime packages. WPF release build and real desktop smoke launch passed; deterministic tests cover restart/source changes, timestamp precision, message fingerprints, canceled notification delivery, and exact DingTalk/Feishu HMAC signatures.

Final validation: six suites passed with zero failures, including 89 core recovery checks. Packaged and standalone EXE smoke launches exited zero; the standalone launch used no adjacent DLL or configuration. Native accessibility inspection showed the real 120-session data source and settings controls. No real conversation message or external webhook notification was sent during verification.
