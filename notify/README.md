# notify/agent_notify.py

Single-file Python script (stdlib only) that forwards an AgentBeacon status event to a Receiver over HTTP.

See [`../docs/protocol.md`](../docs/protocol.md) for the wire contract.

## Configuration

| Source | Variable / flag | Notes |
| --- | --- | --- |
| env var | `AGENTBEACON_URL` | e.g. `http://127.0.0.1:8765` |
| env var | `AGENTBEACON_TOKEN` | Shared Bearer Token |
| CLI | `--url` | Overrides `AGENTBEACON_URL` |
| CLI | `--token` | Overrides `AGENTBEACON_TOKEN`. **Dev-only.** Command-line secrets leak into shell history and `/proc/<pid>/cmdline`. |

## Usage

```bash
AGENTBEACON_URL=http://127.0.0.1:8765 \
AGENTBEACON_TOKEN=dev-token-placeholder \
python notify/agent_notify.py \
    --session-id claude-code-session-2026-08-28T10-00-00Z \
    --agent claude-code \
    --status running
```

Optional fields:

```bash
... --host workstation-lan --message "Reading file src/main.rs"
```

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Receiver returned `200 OK` |
| `2` | Receiver returned `4xx` (request itself is invalid; do not retry) |
| `3` | Receiver returned `5xx`, network error, timeout (v1 does not retry) |
| `4` | Local validation error (missing env var, empty required field, `session_id` > 256 chars, encoded body > 8 KiB, etc.) |

## Defensive handling performed before the request

- `session_id` empty → exit `4`.
- `session_id` > 256 chars → exit `4` (identity field is **not** truncated).
- `agent` empty → exit `4`.
- `status` not in `{running, approval, completed, failed}` → exit `4` (argparse rejects).
- `message` > 512 chars → truncated to 512 chars locally before send.
- Encoded body > 8 KiB → exit `4` (refuses to send).

The Receiver performs the same checks defensively (`message` is re-truncated there too).

## What this script does NOT do

- No retry on transport errors or `5xx` (v1 rule; see protocol §8.2).
- No caching, no batching, no debounce.
- No inference of `status` from logs / output — it forwards what the caller passes.
- No agent-type awareness.

## Hook contract

Claude Code 参考 Adapter (and any future Agent Hook) is expected to:

1. Catch the Agent Runtime's lifecycle event.
2. Immediately translate it to one of `running` / `approval` / `completed` / `failed`.
3. Spawn this script as a child process (or call it through `python -m`), passing the appropriate flags.
4. Not debounce, batch, or delay the call.