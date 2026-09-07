# Round 2 — Windows Indicator (minimal viable)

> **Historical note (Round 3)**: the UI rules and architecture described
> below reflect Round 2 as shipped. Round 3 redesigned the Indicator UI
> into traffic-light modules (`LampModuleView` + `LampStateMapper`) with
> module-anchored cards (`AnchoredCardLayout`); approval cards now
> auto-retract after 8 s. See `docs/ui-policy.md` for the canonical
> current rules and the README for current test totals. The IPC design
> in this document is still accurate.

This document extends `architecture.md` with the Windows-side Indicator
introduced in Round 2. The HTTP Protocol v1 contract documented in
`protocol.md` is **unchanged**. The Agent → Receiver wire format documented in
`protocol.md` is **unchanged**. Everything below is local to the Windows
machine.

## Process model

Three processes in total, started independently:

```
                 +-----------------------------+
                 |  AgentBeacon.Indicator      |   Windows-only WPF
                 |  (net10.0-windows, WPF)     |
                 +--------------+--------------+
                                ^
                                | Named Pipe IPC
                                | (length-prefixed JSON SnapshotEnvelope)
                                v
+---------------------+    POST /api/v1/status   +---------------------+
| adapter / hook      | ----------------------> | agentbeacon-receiver |
| (WSL/Linux, Python) |                         | (Kestrel, net10.0)  |
+---------------------+                         +---------------------+
```

* The **Receiver** remains cross-platform. It exposes the v1 HTTP endpoint
  and a Named Pipe IPC server.
* The **Indicator** is Windows-only WPF. It is *not* a service; the user
  starts it like any desktop app. It connects to the Receiver's Named Pipe
  and never speaks HTTP itself.
* Both processes are independent: starting, stopping, or crashing either one
  does not affect the other. The Receiver keeps serving HTTP regardless of
  the Indicator's state.

## What is NOT implemented in Round 2

Per the kickoff scope:

* Claude Code Adapter. The Adapter is the per-runtime layer that listens
  to the Agent Runtime's lifecycle events and translates them into the
  four statuses before POSTing them. The hook script itself is
  NOT an Adapter — it's a generic status-reporting CLI that only knows
  how to POST a `(session_id, agent, status, message?, host?)` envelope
  to the Receiver. Each Agent Runtime (Claude Code, OpenCode, Codex,
  proprietary) needs its own Adapter; Round 4 is planned to land the
  first one (Claude Code) — Round 3 was used for the Indicator
  traffic-light UI redesign.
* Windows Service / installer / auto-start.
* Any change to Protocol v1.
* Cross-platform Indicator (macOS / Linux). v1 Indicator is Windows-only.

## Receiver → Indicator IPC

This IPC is **not** part of Protocol v1. It is a local-only implementation
detail.

### Pipe name

* Default: `AgentBeacon.Status` (see `shared/IpcConstants.DefaultPipeName`).
* Receiver: Named Pipe IPC is **enabled by default**. Override with
  `AGENTBEACON_PIPE=<name>` env var or `--pipe <name>` CLI flag. Use
  `--no-pipe` to explicitly disable (mainly for tests / headless use).
* Indicator: `--pipe <name>` (defaults to `AgentBeacon.Status`).

When pipe name resolution differs (e.g. test using a unique pipe per run),
both sides must be passed the same name.

### Wire format

Every frame sent from Receiver → Indicator:

```
+-----------------+-----------------------------+
| 4-byte BE int32 | UTF-8 JSON SnapshotEnvelope |
| length          | payload (length bytes)      |
+-----------------+-----------------------------+
```

`SnapshotEnvelope`:

```json
{
  "type": "snapshot",
  "sessions": [
    { "session_id": "...", "agent": "...", "host": "...",
      "message": "...", "status": "running|approval|completed|failed",
      "updated_at": "<RFC 3339>" }
  ]
}
```

v1 only emits `"type": "snapshot"`. There are no incremental events, no
sequence numbers, no replay — every frame is a full snapshot.

### Push semantics

* On client connect, the Receiver atomically (a) subscribes the client's
  push callback AND (b) enqueues the current snapshot for that specific
  client, both under the same store lock as a concurrent `Upsert`. This
  eliminates the connect/update race: the new client's channel always ends
  up strictly monotonic in store-order, never on a snapshot older than
  its initial.
* On every successful POST, the Receiver enqueues a new snapshot for every
  connected client. Enqueue order is the order in which Upserts acquired
  the store lock, so per-client Channel TryWrite ordering matches
  store-mutation ordering.
* Per-client queue is a bounded `Channel<byte[]>` of capacity 8 with
  `BoundedChannelFullMode.DropOldest`. The Channel uses
  `SingleReader = true` and `SingleWriter = false`. `SingleWriter = false`
  is a conservative Channel configuration: the two enqueue paths for one
  client (the `SubscribeWithInitial.initialEnqueue` on the accept thread
  and the per-client `onChange` callback running on whichever HTTP
  request thread drove the `Upsert`) are actually serialized by
  `SessionStateStore`'s `_stateLock`, so they don't race in the current
  implementation. `SingleWriter = false` is kept so future code paths
  can enqueue from outside the lock without a config change.
* POST `/api/v1/status` does not wait on Named Pipe I/O. A slow or stuck
  Indicator cannot back-pressure `POST /api/v1/status`: when the
  per-client Channel is full, `BoundedChannelFullMode.DropOldest` evicts
  the oldest unsent snapshot in favor of the newest. The POST still
  waits (briefly) on the store lock and on JSON serialization, both of
  which are bounded and non-pipe.

### Transport

* Windows: `System.IO.Pipes.NamedPipeServerStream` /
  `NamedPipeClientStream`.
* Linux/WSL: the same .NET API surface compiles to a Unix domain socket
  under the hood (used here purely for cross-process IPC tests on WSL; the
  shipped Receiver runs on Windows where the Windows pipe implementation
  is used).

## Indicator architecture

```
windows/
  AgentBeacon.Indicator.Core/        net10.0 — pure C#, no WPF deps
    IndicatorStatus.cs               (in SessionViewModel.cs) four valid statuses + Validate
    SessionViewModel.cs              INotifyPropertyChanged per session; LampColor + TooltipText
                                     Agent/Host settable; TooltipText re-fires on either change
    SessionViewModelStore.cs         Pure state machine; lamp + card rules; completed lamps
                                     retained until Receiver removal/local dismiss; fail-fast on unknown status
    CardEvent.cs                     Show / Update / Hide events
    PipeClient.cs                    Connects + reads frames, auto-reconnect with backoff
    IndicatorHost.cs                 Wires PipeClient → Store, surfaces events

  AgentBeacon.Indicator/             net10.0-windows — WPF UI
    App.xaml + .cs                   Entry point, parses --pipe
    IndicatorUiConstants.cs          UI-only knobs (slide-in ms, gutters) — NOT in Protocol v1
    MainWindow.xaml + .cs            Lamp column at right edge (transparent, topmost, no taskbar, no-activate);
                                     per-session DispatcherTimer dictionary for card auto-hide;
                                     any new Show replaces prior timer for that session
    CardWindow.xaml + .cs            Slide-in notification card (right→left, ~220 ms, no-activate);
                                     SlideInFromRight pins final Left to target via animation Completed callback
    NoActivateHelper.cs              Applies WS_EX_NOACTIVATE to HWND
    app.manifest                     PerMonitorV2 DPI awareness
```

`Indicator.Core` is a cross-platform library so the dedup / lamp / card rules
can be unit-tested on Linux without WPF. `Indicator` (WPF) is Windows-only.

## UI rules

See `docs/ui-policy.md` for the canonical rules. The Indicator's
`SessionViewModelStore` encodes:

| Status        | Lamp                       | Card                                                |
| ------------- | -------------------------- | --------------------------------------------------- |
| `running`     | blue `#2F81F7`, present    | none                                                |
| `approval`    | yellow `#D29922`, present  | shows then auto-retracts; lamp stays until status changes |
| `completed`   | green `#3FB950`, present   | shows then auto-retracts; lamp stays until Receiver removal or local dismiss |
| `failed`      | red `#F85149`, **long-lived** | shows then auto-retracts; lamp stays until status changes |

Strictly **four** statuses, **four** colors. No gray / idle / offline /
unknown / paused is ever rendered. Unknown statuses thrown fail-fast at the
Core layer (`IndicatorStatus.Validate`).

`approval` cards re-show when `updated_at` advances, refreshing content and
re-arming the per-session auto-hide timer.

`failed` cards are **not** updated in place when `updated_at` advances.
Instead, the store emits a fresh `Show` event for the same session: the
visible failed card is replaced and the per-session auto-hide timer
is cancelled and re-armed from zero. This is what the user sees as
"another failure toast".

Card durations and slide-in animation (220 ms)
are **UI constants** in `IndicatorUiConstants`. They are not part of HTTP
Protocol v1.

Completed lamps are no longer aged out by time. They remain visible until
Receiver stops reporting the session, a newer status replaces them, or the
user dismisses the lamp locally.

## Build & run (manual verification)

The WPF Indicator project (`net10.0-windows`) **must** be built on Windows
because WPF requires the Windows Desktop runtime
(`Microsoft.WindowsDesktop.App 10.0.11`). Everything else can be built and
tested on WSL/Linux.

### Receiver (cross-platform)

```bash
# from repo root
dotnet build receiver -c Release
dotnet run --project receiver -c Release --no-build -- \
  --bind 127.0.0.1 \
  --port 8765 \
  --token "$TOKEN" \
  --debug
# Named Pipe is enabled by default at AgentBeacon.Status.
# To override: --pipe <name>, AGENTBEACON_PIPE=<name>, or --no-pipe to disable.
```

In another shell, send a status update:

```bash
curl -sS -X POST http://127.0.0.1:8765/api/v1/status \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"session_id":"abc","agent":"claude-code","status":"running","message":"hi"}'
```

### Indicator (Windows only)

On Windows (PowerShell or cmd):

```bat
dotnet build windows\AgentBeacon.Indicator -c Release
dotnet run --project windows\AgentBeacon.Indicator -c Release --no-build
```

The window will appear at the right edge of the primary monitor's working
area (just above the taskbar). It has no taskbar entry, stays on top, and
**does not steal focus** from the foreground app (Terminal / VS Code / etc.).

### Tests

```bash
# Python (Agent-side + Receiver HTTP behavior)
conda run -n py312 python tests/test_receiver.py    # 38 unique tests

# C# Receiver IPC tests (spawns the real receiver as a subprocess)
# Race-aware concurrent-snapshot test verifies that the connect/update
# race cannot leave a client on a stale snapshot, plus a multi-client
# fanout test that asserts exactly one snapshot per Upsert per client.
dotnet run --project tests/Receiver.IpcTests -c Release   # 15 unique tests

# C# Indicator Core tests (SessionViewModelStore + dedup rules + fail-fast
# + completed-lamp retention + Agent/Host replacement +
# PipeClient partial-read / payload-EOF semantics)
dotnet run --project tests/Indicator.CoreTests -c Release  # 31 unique tests
```

**Unique automated tests**: 91 across 4 suites (7 + 38 + 15 + 31).
Test *executions* additionally include the Indicator.CoreTests suite
running on the Windows-native `dotnet.exe` build, which is verified
separately. The two runs execute the same 31 unique tests, not 62.
