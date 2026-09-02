#!/usr/bin/env python3
"""AgentBeacon per-session watchdog.

Spawned (detached) once per Claude Code session by agentbeacon_hook.py
at SessionStart. Polls the session's sidecar file:

  - claude process alive                     → keep waiting
  - dead + last status running/approval      → POST `failed`
      (crash / kill / user closing the CLI after it got stuck at an
       API error — none of which emit hook events)
  - sidecar removed (clean SessionEnd)       → exit quietly
  - sidecar stale (dead pid, terminal state) → cleanup + exit

Failure semantics (docs/adapter-claude-code.md): this reports the
session-terminating faults the hook system cannot observe. It does NOT
report per-tool or per-request errors, and it cannot turn the light red
while the CLI process is still alive but stuck retrying — for that the
process must actually exit (which is the common outcome of the
"stuck at API error" workflow: the user closes the terminal).
"""

import json
import os
import socket
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import agentbeacon_hook as adapter  # noqa: E402

POLL_S = 5.0
MAX_RUNTIME_S = 12 * 3600


def log(msg):
    print(f"agentbeacon-watchdog: {msg}", file=sys.stderr, flush=True)


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
            return 0  # clean SessionEnd removed it (or unreadable) → done

        now = time.time()
        action = adapter.watchdog_decide(sidecar, adapter.pid_alive, now)
        session_id = sidecar.get("session_id")
        log(f"poll: pid={sidecar.get('claude_pid')} "
            f"last={sidecar.get('last_status')} ended={sidecar.get('ended')} "
            f"lost_at={sidecar.get('claude_pid_lost_at')} → {action}")

        if action == "mark-lost":
            sidecar["claude_pid_lost_at"] = now
            sidecar["updated_at"] = now
            try:
                _save(sidecar_file, sidecar)
            except OSError:
                pass
            continue

        if action == "failed":
            log(f"FAILED branch: session={session_id}")
            url, token = adapter.resolve_config()
            if url and session_id:
                try:
                    adapter.post_status(url, token, adapter.build_envelope(
                        session_id, "failed",
                        "claude process exited unexpectedly",
                        socket.gethostname()))
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
            try:
                os.remove(sidecar_file)
            except OSError:
                pass
            return 0

        # "none": process alive — keep waiting.
    return 0


def _save(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(data, f)
    os.replace(tmp, path)


if __name__ == "__main__":
    sys.exit(main())
