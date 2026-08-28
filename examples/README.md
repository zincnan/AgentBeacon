# examples/

Minimal smoke-test scripts for manual verification.

`smoke_curl.sh` — bash + curl. Sends `running → approval → running → completed`
to a running Receiver and prints the HTTP status code for each call.

Run it against a Receiver launched with `--debug` so you can also inspect
the resulting state via `GET /debug/sessions`.

Most protocol coverage lives in `tests/test_receiver.py` (Python unittest);
this script is just a one-shot human-facing smoke test.

## Usage

```bash
# Terminal 1 — Receiver
cd receiver
dotnet run -- --bind 127.0.0.1 --port 8765 --token dev-token-placeholder --debug

# Terminal 2 — smoke
AGENTBEACON_URL=http://127.0.0.1:8765 \
AGENTBEACON_TOKEN=dev-token-placeholder \
bash examples/smoke_curl.sh
```