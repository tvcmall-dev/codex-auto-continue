# Codex Auto Continue: Windows Desktop Design Proposal

Date: 2026-11-06

Status: proposed. The Windows/WPF technology choice is requested by the user.
The interaction preview and the live-integration acceptance gate remain subject
to review. This document does not assert that desktop connectivity works yet.

## Objective

Build a Windows application that shows the actual status of multiple local
Codex desktop conversations and continues eligible conversations after network
or usage-limit failures. A continuation must append to the existing conversation.

The application must distinguish a normal pause, an internal Codex retry, a
terminal failure, a queued continuation, and confirmed recovery.

## Scope

The first version targets Windows 10/11 x64 and .NET Framework 4.8 with a native
WPF interface. It monitors local primary conversations belonging to the current
Windows user. Historical and completed conversations are available separately
and are not counted as running. Remote hosts and subagent monitoring are deferred.

The main application runs in the interactive user session and can minimize to a
system tray. An explicit exit stops monitoring; closing to tray keeps it running.
Logon startup is optional. The application does not require a Python or Node.js
runtime for production operation.

## Approaches Considered

1. **WPF plus the existing Codex backend and optional hooks (recommended).**
   Provides native UI, structured events, and an independent retry scheduler.
   Requires proving access to the desktop application's actual backend.
2. **A plugin using hooks only.** Easy to distribute locally, but per-request
   failure hooks are not final turn state, and finishing an asynchronous hook
   cannot start a new turn. Insufficient for reliable multi-session recovery.
3. **WPF using Windows UI Automation.** A possible separately reviewed fallback
   if the current desktop backend is inaccessible. It depends on window state,
   accessibility support, and UI changes, so it is not the default adapter.

## Components

| Component | Responsibility |
| --- | --- |
| Desktop | WPF MVVM views, session list, inspector, tray, and settings |
| Core | Session state machine, failure classification, retry eligibility and timing |
| Codex adapter | Backend discovery, JSON-RPC reads/events, and CLI message delivery |
| Hook relay | Optional short-lived net48 console process that receives hook JSON on stdin |
| Persistence | SQLite storage for monitoring preferences, pending recovery, and event history |

The desktop application owns the scheduler. Hooks submit small events and exit
promptly. A relay can forward events over a local named pipe; its input cannot
directly execute arbitrary commands. All view changes occur through the WPF
Dispatcher. The monitor and CLI process calls run outside the UI thread.

The production solution should keep Core independent of WPF, SQLite, and Codex
transport details so state transitions can be tested with deterministic events.

## Live Connection Acceptance Gate

The first implementation milestone must demonstrate, with an explicitly
selected test conversation:

1. Identify the backend already used by the desktop application.
2. Read multiple desktop conversations and their current runtime state.
3. Observe a final failed turn and distinguish it from a retrying request.
4. Send one continuation to the same conversation ID.
5. Observe a new turn and subsequent normal execution in that conversation.

The development machine has a Codex CLI that includes `queue --resume` and
app-server tooling, but the default local control-socket version query failed.
This does not establish that the desktop backend is unavailable, and it does
not establish that it is accessible. An independent app-server must not be
substituted as evidence of the desktop application's runtime state.

If backend connection or current state cannot be verified, display connection
unavailable or unknown. Do not make automatic continuation decisions from stale
history, inactivity, or a single failed model request.

## Evidence and Event Sources

- `thread/list` pages saved conversations and can include runtime state from
  the connected backend. Saved conversation membership is not execution state.
- `thread/loaded/list` reports loaded IDs; loaded does not mean running.
- `thread/read` reads metadata without resuming a conversation.
- `thread/status/changed` updates loaded conversation runtime state.
- `turn/completed` with `turn.status = failed` establishes terminal failure.
- An `error` event with `willRetry = true` means Codex is still retrying.
- `PostModelRequest` reports individual attempt outcomes. Its output is
  diagnostic only and does not itself establish terminal failure.
- `Interrupt` records user interruption and prevents automatic continuation
  for that generation of work.

Optional hooks are shipped as local Codex lifecycle configuration. The user
reviews and trusts those hooks in Codex. Global or existing hook configuration
must be preserved during any future installation flow.

## Session State and Illustrations

| State | Visual treatment | Automatic continuation |
| --- | --- | --- |
| Running | Green orbit or moving signal | No |
| Codex retrying internally | Blue rotating path | No |
| Waiting for approval or user input | Blue pause/hand illustration | No |
| Network failure, retry scheduled | Orange broken connection and countdown | When eligible |
| Usage limit, waiting for reset | Amber hourglass | After the relevant limit clears |
| Continuation queued | Teal outgoing signal | Do not send again |
| Awaiting recovery confirmation | Teal reconnect illustration | Do not send again |
| Recovery confirmed | Green check, then running illustration | No |
| Failed, needs attention | Red fault illustration | No, unless reclassified and explicitly retried |
| User stopped or monitor paused | Gray pause | No |
| Completed | Muted green completion | No |
| Connection unavailable or unknown | Neutral disconnected illustration | No |

Labels and distinct shapes accompany color. Sending a continuation does not
restore the normal illustration. Recovery requires a new turn followed by actual
model output or execution progress, or a successfully completed turn, with no
newer terminal failure. A user-message item, a queued message, a turn-start
notification, or an accepted request by itself is not recovery evidence.

## Retry Rules

- Network timeouts, broken connections, and temporary upstream failures can be
  retried only after a final failed turn and a fresh state recheck.
- Default network delays are 30, 60, 120, 240, and 300 seconds, with a maximum
  of five attempts per failure incident. Explicit upstream retry timing takes
  precedence when it is available and trustworthy.
- ChatGPT subscription limits use the relevant quota bucket and reset time
  when available. A reset timestamp triggers a recheck, not an assumption that
  the quota is now usable. Multiple exhausted buckets must all permit the work.
- Generic 429 responses and third-party provider limits are not automatically
  treated as subscription-quota exhaustion. Use their available retry timing.
- Credit exhaustion without a reliable recovery time, unauthorized requests,
  bad requests, context-window exhaustion, and unsupported failures require
  attention instead of repeated continuation messages.
- A manual user interruption, a newer user turn, a disabled monitor, normal
  completion, or an approval/input wait cancels pending automatic continuation.
- Repeated notifications are deduplicated by conversation and failed turn ID.
  A failure incident has its own persistent ID and tracks the original failed
  turn plus all automatically generated continuation turns. If one of those
  turns fails again, retain the same incident and its accumulated retry count.
  Only confirmed recovery, a new user-authored task, or an explicit manual
  incident reset can establish a new retry budget. Persist pending continuation
  before delivery and reconcile uncertain delivery before any retry. Allow at
  most one outstanding continuation per conversation and failure incident.
- Recheck current state immediately before sending. Observe execution after
  sending; a CLI exit code of zero is insufficient because resume can fail with
  a warning while the queue command still exits successfully.
- If the monitor restarts, reconcile persisted incidents against the connected
  backend before re-enabling their scheduled actions.

The default continuation text is the Chinese word for "continue" (`\u7ee7\u7eed`).
It can be configured without changing the conversation's model or permissions.

## Panel Design

The visual proposal uses a warm light surface, graphite text, restrained green
and orange accents, and original vector status illustrations. It should feel
like a focused desktop instrument rather than an administration template.

- A narrow left navigation groups sessions, incidents, retry policy, and settings.
- The main region shows searchable session rows with title, project, status,
  last activity, retry timing, and a per-session monitoring switch.
- The inspector shows the selected session's larger state illustration,
  current error, retry count, next action, and event timeline.
- A global monitoring control and connection indicator remain visible.
- A manual continuation action explains pending confirmation. It is unavailable
  for states such as active execution and waiting for approval.
- Tray state summarizes running, waiting, and attention counts without opening
  the window. Notifications report meaningful changes and respect user settings.

The interactive HTML file is a design artifact only. Its fictional examples
and simulated recovery must be labeled clearly. Its layouts and vector drawings
will be translated to WPF XAML after design review; production does not require
embedding a browser.

## Persistence and Packaging

Store application data under the current user's local application data folder,
separate from the installation directory. Use a net48-compatible SQLite package
and explicitly package the required x64 native dependency. Keep serialized error
details bounded and store only the metadata needed for recovery and diagnostics.

Use existing Codex authentication through its normal local execution path.
Do not copy account credentials into application settings or the repository.
The application must not need a separate API key to observe local state or invoke
an existing authenticated Codex CLI.

Build with Visual Studio 2022 MSBuild and the .NET Framework 4.8 developer pack.
The development machine already has this toolchain. Build reproducibility and
Windows packaging are implementation milestones, not completed deliverables.

## Verification Plan

Test the state machine with events representing internal retries, final failure,
duplicate errors, manual interruption, approval waits, newer user input, quota
reset, uncertain message delivery, process restart, and connection loss.

For the preview, verify selection, search, filters, switches, action disabling,
the queued/confirmation/recovered visual sequence, keyboard access, and usable
layout at both wide and narrow viewport sizes.

For the live adapter, complete the connection acceptance gate before claiming
support for monitoring or recovering real desktop conversations. Validate one
explicitly selected conversation before expanding to multiple conversations.

## Official References

- https://learn.chatgpt.com/docs/app-server
- https://learn.chatgpt.com/docs/hooks
- https://learn.chatgpt.com/docs/developer-commands?surface=cli
- https://developers.openai.com/plugins/build/plugins
