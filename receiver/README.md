# receiver/

AgentBeacon v1 HTTP receiver. C# / ASP.NET Core / Kestrel. Cross-platform — v1 deliberately avoids Windows Service / WPF / any Windows-only code.

See [`../docs/protocol.md`](../docs/protocol.md) for the wire contract.

## Build

```bash
cd receiver
dotnet build
```

## Run

```bash
dotnet run -- \
    --bind 0.0.0.0 \
    --port 8765 \
    --token dev-token-placeholder \
    --debug
```

CLI flags and env vars:

| Flag | Env | Default | Notes |
| --- | --- | --- | --- |
| `--bind` | `AGENTBEACON_BIND` | `0.0.0.0` | Bind address. `0.0.0.0` allows WSL-internal and remote Linux hosts. |
| `--port` | `AGENTBEACON_PORT` | `8765` | TCP port. |
| `--token` | `AGENTBEACON_TOKEN` | (required) | Bearer Token. Must match what `agent-notify` sends. |
| `--debug` | — | off | Enable `GET /debug/sessions`. Not part of Protocol v1. |

If neither `--token` nor `AGENTBEACON_TOKEN` is set, the receiver exits with code `4`.

## Endpoints

| Method | Path | Auth | Purpose | In Protocol v1? |
| --- | --- | --- | --- | --- |
| `POST` | `/api/v1/status` | Bearer | Status event submission | **yes** (only) |
| `GET` | `/debug/sessions` | Bearer | In-memory state snapshot | no (dev only, requires `--debug`) |
| `GET` | `/healthz` | none | Liveness probe | no |

## Behavior summary

- Last received wins: a new event for an existing `session_id` overwrites the previous state.
- `session_id` > 256 chars → `422 session_id_too_long`. Identity fields are not truncated.
- `message` > 512 chars → silently truncated to 512. Status event still recorded.
- `status` not in `{running, approval, completed, failed}` → `400 invalid_status`.
- Bearer Token check uses `CryptographicOperations.FixedTimeEquals` (constant time).
- Body size enforced at 8 KiB → `413 payload_too_large`.
- Wrong Content-Type → `415 unsupported_media_type`.
- JSON parse error or missing required field → `400 invalid_json` / `400 missing_field`.

## Logs

Per-request info log line:

```
HH:mm:ss.fff info: Status[0] session=<id> agent=<agent> status <prev> -> <new>
```

Add to your logging pipeline as needed (stdout by default in v1).

## What this service does NOT do

- No persistence (no SQLite, no file log of states).
- No retry / dedup of incoming events.
- No agent discovery, no service registration.
- No WebSocket / SSE / push.
- No Windows Service host (added later on top of this binary, not by rewriting it).