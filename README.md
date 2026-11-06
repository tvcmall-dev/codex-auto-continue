# Codex Auto Continue

A Windows desktop companion that monitors Codex conversations and helps them
continue after recoverable network failures or usage limits.

**Status: design and interactive UI preview.** The production WPF application
and live Codex integration have not been implemented. The preview uses fictional
sessions and does not send messages to Codex.

## Desktop application

- Windows 10/11, x64.
- C# and .NET Framework 4.8.
- Native WPF interface with MVVM, vector state illustrations, and a system tray.
- Multiple-session overview, individual monitoring switches, and an event timeline.
- Retry scheduling and recovery history persisted locally in SQLite.
- Automatic continuation only after a confirmed terminal, recoverable failure.
- Explicit distinction between a queued message and confirmed recovery.

## Preview

Open [the interactive dashboard](docs/prototypes/dashboard.html) in a browser.
It is a visual proposal for the native WPF application, not a web-based runtime
dependency. It requires no package installation or external services.

The preview demonstrates session selection, search, filters, monitoring controls,
and a simulated continuation followed by recovery confirmation. Its status
illustrations use inline vectors, so they can be recreated with native WPF
geometry and drawing resources.

## Integration approach

The planned application combines a Codex app-server adapter, optional lifecycle
hooks, a retry state machine, and a CLI message sender. An optional hook relay
will also be written in .NET Framework 4.8; Python is not required.

The first live-integration milestone must prove all three operations against the
**same backend used by the Codex desktop application**:

1. Read actual runtime state for multiple existing desktop conversations.
2. Queue a continuation for one selected conversation without changing its identity.
3. Observe a new turn and normal execution before marking it recovered.

Starting another app-server process and reading saved conversation history does
not prove access to the desktop application's live runtime. A successful
`codex queue --resume` exit code alone does not prove that execution resumed.

See the [design proposal](docs/superpowers/specs/2026-11-06-codex-auto-continue-design.md)
for state transitions, UI behavior, retry rules, and acceptance criteria.

## Official references

- [Codex app-server](https://learn.chatgpt.com/docs/app-server)
- [Codex lifecycle hooks](https://learn.chatgpt.com/docs/hooks)
- [Codex command line options](https://learn.chatgpt.com/docs/developer-commands?surface=cli)
- [Plugin packaging and lifecycle hooks](https://developers.openai.com/plugins/build/plugins)
