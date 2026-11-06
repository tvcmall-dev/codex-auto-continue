# Codex Auto Continue: Windows Desktop Design Proposal

Date: 2026-11-06

Status: implemented as a runnable net48 WPF preview after the user approved
development on 2026-11-06. Real desktop-owned SQLite projection reads are verified;
the full live-resume acceptance gate remains unmet on the current stdio-only
desktop backend. See the Chinese README and usage guide for current limitations.

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
| Notifications | Platform-specific webhook payloads, signing, delivery queue, and results |

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

### Continuation Message Selection

Settings offer three mutually exclusive message modes:

| Mode | Message sent to Codex |
| --- | --- |
| Chinese (default) | The Chinese word for "continue" (`\u7ee7\u7eed`) |
| English | `continue` |
| Custom | The user's saved non-empty input |

This setting controls the message sent to Codex, not the interface language.
Both manual continuation and automatically scheduled continuation use the same
saved message. Preserve custom text as plain text and trim outer whitespace;
reject empty/whitespace-only input and text longer than 1,000 characters.
Show the effective message before saving. Unsaved edits do not change active
behavior. Do not change the conversation's model or permissions.

Persist the selected mode and custom text with other production settings, and
recheck the effective saved message immediately before dispatch. Pending actions
use the current saved setting; edits do not alter messages already queued.
The prototype offers this configuration in the retry-policy panel and stores it
in page memory only. Its simulated timeline records the actual selected message
as escaped plain text.

## Panel Design

The user requests a dark theme. The visual proposal uses deep graphite surfaces,
layered charcoal panels, subtle borders, bright readable text, restrained green
and orange accents, and original vector status illustrations. Forms, modal
dialogs, disabled actions, and every state illustration must match the dark
palette. It should feel like a focused desktop instrument rather than an
administration template.

- A narrow left navigation groups sessions, incidents, retry policy, and settings.
- The main region shows searchable session rows with title, project, status,
  last activity, retry timing, and a per-session monitoring switch.
- The inspector shows the selected session's larger state illustration,
  current error, retry count, next action, and event timeline.
- A global monitoring control and connection indicator remain visible.
- Retry settings offer Chinese, English, and custom continuation messages.
- Notification settings include the destination platform, webhook address,
  optional signing secret, event subscriptions, and an explicit test action.
- A manual continuation action explains pending confirmation. It is unavailable
  for states such as active execution and waiting for approval.
- Tray state summarizes running, waiting, and attention counts without opening
  the window. Notifications report meaningful changes and respect user settings.

The interactive HTML file is a design artifact only. Its fictional examples
and simulated recovery must be labeled clearly. Its layouts and vector drawings
will be translated to WPF XAML after design review; production does not require
embedding a browser.

## Webhook Notifications

The first version supports one active notification destination at a time, with
adapters for DingTalk, Feishu, WeCom, and a generic JSON webhook. Separate
adapters define each platform's message shape, signing scheme, and interpretation
of delivery success. A generic webhook must not be assumed to accept a
platform-specific payload.

Settings contain a display name, provider, HTTPS webhook address, enabled state,
optional signing secret where supported, and event subscriptions. Default events
are confirmed recoverable failure, confirmed recovery, and retry exhaustion.
Normal internal retries and repeated reports of the same failure are quiet.

Notification content includes the event, conversation title/ID, state, sanitized
reason, retry count, and next retry time when applicable. It does not include
full transcripts, prompts, command output, account credentials, webhook tokens,
or signing secrets. Messages have a stable Codex Auto Continue title prefix so
users can configure a provider's keyword-based robot policy if needed.

For DingTalk signing, use the configured secret as the HMAC-SHA256 key and
`timestamp + "\n" + secret` as UTF-8 input, then Base64 and URL-encode the
signature. Add timestamp and sign to the request URL as required by the official
custom-robot API. Parse the platform response as well as the HTTP status:
DingTalk HTTP 200 with a nonzero `errcode` is a failed notification. Feishu and
WeCom adapters must be verified against their own current platform contracts.

Use a separate bounded delivery queue. Deduplicate by incident and event type;
a flapping connection must not produce a notification flood. Delivery has its
own finite retry budget of three attempts, respects explicit provider rate-limit
timing, and never blocks conversation recovery. Show final delivery failures
in notification history without treating them as Codex execution failures.

In the production Windows application, protect the entire webhook URL and its
secret with Windows DPAPI (`ProtectedData`, `CurrentUser`) before storing them
under the user's application data directory. Mask them in settings, summaries,
diagnostics, and exports. Test notifications use an explicit test message and
show the actual platform result after a user invokes the test action.

The HTML prototype only validates input and simulates a test result. It makes
no webhook requests, stores entered values only in page memory, and does not
put secrets in browser storage or files. This limitation is visible in the
settings panel and test result. Preview screenshots use empty or fictional
values only.

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
layout at both wide and narrow viewport sizes. Also verify dark-theme readability,
provider selection, hidden credential inputs, empty/invalid webhook validation,
event choices, settings summaries, and the clearly simulated test result.
Verify the default Chinese continuation, English selection, custom input,
empty-custom rejection, unsaved-edit isolation, and that both manual and
scheduled continuation use the saved message.

For production notifications, verify platform payloads and signatures, platform
failure responses inside HTTP 200, finite retries, incident/event deduplication,
DPAPI round trips, masked error reporting, and independent monitoring operation
when a notification cannot be delivered.

For the live adapter, complete the connection acceptance gate before claiming
support for monitoring or recovering real desktop conversations. Validate one
explicitly selected conversation before expanding to multiple conversations.

### Preview Verification Record

On 2026-11-06, the dark HTML prototype passed 12 browser interaction checks for
session selection, filters, search, protected states, monitoring switches, and
the pending-to-confirmed recovery sequence. It also passed 15 notification
checks for masked fields, input validation, provider selection, event selection,
simulated test progress, memory-only saving, sanitized summaries, and absence
of external webhook requests. The 1440-pixel desktop layout and 430-pixel narrow
layout were inspected; neither the page nor the settings dialog overflowed
horizontally. The final page reported no browser console errors. These checks
validate the prototype only, not the WPF application or live integrations.

The continuation-message update also passed browser checks for the Chinese
default, English selection, unsaved-edit isolation, blank custom rejection,
preservation after a failed save, trimmed custom text, and input length limits.
Manual English continuation and scheduled custom-message continuation were
verified through the simulated timeline and recovery confirmation.

## Official References

- https://learn.chatgpt.com/docs/app-server
- https://learn.chatgpt.com/docs/hooks
- https://learn.chatgpt.com/docs/developer-commands?surface=cli
- https://developers.openai.com/plugins/build/plugins
- https://open.dingtalk.com/document/dingstart/customize-robot-security-settings
- https://open.dingtalk.com/document/development/custom-robots-send-group-messages
