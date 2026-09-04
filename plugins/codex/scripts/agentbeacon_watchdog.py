#!/usr/bin/env python3
"""AgentBeacon per-session watchdog for Codex (see the Claude adapter's
watchdog for the shared design). Polls the session sidecar:

  - codex process alive                     → keep waiting
  - dead + last status running/approval     → POST `failed`
  - sidecar removed (clean SessionEnd)      → exit quietly
  - sidecar stale                           → cleanup + exit

Codex has no error hook, so this watchdog plus SessionEnd-while-working
are the only two `failed` paths (see docs/adapter-codex.md).
"""

import json
import os
import socket
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import agentbeacon_codex_hook as adapter  # noqa: E402

POLL_S = 5.0
MAX_RUNTIME_S = 12 * 3600


def log(msg):
    print(f"agentbeacon-watchdog: {msg}", file=sys.stderr, flush=True)


def _save(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(data, f)
    os.replace(tmp, path)


def main():
    args = sys.argv[1:]
    sidecar_file = None
    for i, a in enumerate(args):
        if a == "--sidecar" and i + 1 < len(args):
            sidecar_file = args[i + 1]
    if not sidecar_file:
        print("usage: agentbeacon_watchdog.py --sidecar <file>", file=sys.stderr)
        return 2
    log(f"started sidecar={sidecar_file}")

    deadline = time.time() + MAX_RUNTIME_S
    while time.time() < deadline:
        time.sleep(POLL_S)
        try:
            with open(sidecar_file) as f:
                sidecar = json.load(f)
        except (OSError, ValueError):
            log("sidecar gone/unreadable → exit")
            return 0

        now = time.time()
        action = adapter.watchdog_decide(sidecar, adapter.pid_alive, now)
        session_id = sidecar.get("session_id")
        log(f"poll: pid={sidecar.get('codex_pid')} "
            f"last={sidecar.get('last_status')} ended={sidecar.get('ended')} "
            f"lost_at={sidecar.get('codex_pid_lost_at')} → {action}")

        if action == "mark-lost":
            sidecar["codex_pid_lost_at"] = now
            sidecar["updated_at"] = now
            try:
                _save(sidecar_file, sidecar)
            except OSError:
                pass
            continue

        if action == "failed":
            log(f"FAILED branch: session={session_id}")
            url, token = adapter.resolve_config()
            agent = sidecar.get("agent", adapter.AGENT)
            if url and session_id:
                try:
                    adapter.post_status(url, token, adapter.build_envelope(
                        session_id, "failed",
                        "codex process exited unexpectedly",
                        socket.gethostname(), agent=agent))
                except Exception as e:  # noqa: BLE001 - best effort
                    print(f"agentbeacon-watchdog: post failed: {e}",
                          file=sys.stderr)
            log("failed posted, sidecar removed, exit")
            try:
                os.remove(sidecar_file)
            except OSError:
                pass
            return 0

        if action == "cleanup":
            log("cleanup: sidecar removed, exit")
            try:
                os.remove(sidecar_file)
            except OSError:
                pass
            return 0

        # "none": process alive — keep waiting.
    return 0


if __name__ == "__main__":
    sys.exit(main())
