#!/usr/bin/env python3
"""AgentBeacon adapter for Codex CLI (hooks entry).

Reads one Codex hook-event JSON from stdin, translates the event into an
AgentBeacon status, and POSTs it to the Receiver — single POST, no
retry, short timeout, always exit 0 so the hook can never block Codex.

Event → status mapping (see docs/adapter-codex.md):

  SessionStart → completed          (session boot / resume: idle & ready → GREEN)
  UserPromptSubmit / PreToolUse / PostToolUse → running
  PermissionRequest                 → approval
  Stop                              → completed
  SessionEnd                        → failed if last status was running/approval
  Interrupt / everything else       → ignored (v1)

Codex specifics vs the Claude Code adapter:
  - Codex has NO StopFailure/error hook, so red while alive is not
    observable; the two failed paths are the same as Claude's:
    SessionEnd-while-working and the PID watchdog.
  - Codex runs hook commands with env_clear(): the hooks.json command
    must use absolute paths (the installer renders them), and AGENTBEACON
    config comes from ~/.agentbeacon.json — env vars only work if the
    user's shell exported them INTO codex's environment before the clear.
  - Hooks are skipped until trusted via Codex's /hooks command.

Config: ~/.agentbeacon.json {"url", "token"} (same file as the Claude
adapter; env AGENTBEACON_URL/AGENTBEACON_TOKEN override).
"""

import json
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

AGENT = "codex"
POST_TIMEOUT_S = 3.0
THROTTLE_S = 2.0

STATE_DIR = os.path.join(
    os.environ.get("TMPDIR", "/tmp"), "agentbeacon-codex")

# ---------------------------------------------------------------------------
# Pure logic (unit-tested in tests/test_codex_adapter.py)
# ---------------------------------------------------------------------------

RUNNING_EVENTS = {
    "UserPromptSubmit": None,   # message filled from the prompt text
    "PreToolUse": "tool started",
    "PostToolUse": "tool finished",
}
APPROVAL_EVENTS = {"PermissionRequest": "permission requested"}
# SessionStart (fresh boot or resume) = alive but no task running → GREEN.
COMPLETED_EVENTS = {
    "SessionStart": "session started — idle",
    "Stop": "turn completed",
}
FAILED_EVENTS: dict[str, str] = {}  # no error hook exists in Codex
# SessionEnd handled separately; Interrupt / unknown → ignored.


def map_event(event_name):
    """Return the AgentBeacon status for a hook event, or None."""
    if event_name in RUNNING_EVENTS:
        return "running"
    if event_name in APPROVAL_EVENTS:
        return "approval"
    if event_name in COMPLETED_EVENTS:
        return "completed"
    if event_name in FAILED_EVENTS:
        return "failed"
    return None


def build_envelope(session_id, status, message=None, host=None, agent=AGENT):
    env = {"session_id": session_id, "agent": agent, "status": status}
    if message:
        env["message"] = str(message)[:512]
    if host:
        env["host"] = host
    return env


def load_config_file(env=None):
    """Read ~/.agentbeacon.json (or AGENTBEACON_CONFIG=<path>). Missing or
    malformed → {} (the config file is a convenience layer, never fatal)."""
    env = env if env is not None else os.environ
    path = env.get("AGENTBEACON_CONFIG") or os.path.join(
        os.path.expanduser("~"), ".agentbeacon.json")
    try:
        with open(path) as f:
            doc = json.load(f)
        return doc if isinstance(doc, dict) else {}
    except (OSError, ValueError):
        return {}


def resolve_config(env=None):
    """Resolve (url, token): env first, then ~/.agentbeacon.json."""
    env = env if env is not None else os.environ

    def pick(plain, file_key):
        v = env.get(plain)
        if v:
            return v
        file_val = load_config_file(env).get(file_key)
        return file_val if file_val else None

    return (
        pick("AGENTBEACON_URL", "url"),
        pick("AGENTBEACON_TOKEN", "token"),
    )


def should_post(last_status, last_post_epoch, status, now_epoch,
                throttle_s=THROTTLE_S):
    if last_status == status and last_post_epoch is not None:
        if now_epoch - last_post_epoch < throttle_s:
            return False
    return True


def watchdog_decide(sidecar, pid_alive, now_epoch, stale_s=24 * 3600):
    """Same decision table as the Claude adapter: alive → none; dead while
    running/approval → grace → failed; ended/terminal → cleanup; unknown
    pid never fires failed."""
    last = sidecar.get("last_status")
    if sidecar.get("ended"):
        return "cleanup"
    pid = sidecar.get("codex_pid")
    if pid is None:
        if now_epoch - sidecar.get("updated_at", 0) > stale_s:
            return "cleanup"
        return "none"
    if pid_alive(pid):
        return "none"
    if last in ("running", "approval"):
        died_at = sidecar.get("codex_pid_lost_at")
        if died_at is None:
            return "mark-lost"
        if now_epoch - died_at < 10:
            return "none"
        return "failed"
    if now_epoch - sidecar.get("updated_at", 0) > stale_s:
        return "cleanup"
    return "cleanup"


# ---------------------------------------------------------------------------
# Runtime helpers
# ---------------------------------------------------------------------------

def find_codex_pid():
    """Walk up the process tree to find the running `codex` CLI process."""
    pid = os.getppid()
    for _ in range(10):
        if pid <= 1:
            return None
        try:
            with open(f"/proc/{pid}/cmdline", "rb") as f:
                cmdline = f.read().decode("utf-8", "replace")
        except OSError:
            return None
        if "codex" in cmdline:
            return pid
        try:
            with open(f"/proc/{pid}/stat") as f:
                pid = int(f.read().split(") ", 1)[1].split()[1])
        except (OSError, ValueError, IndexError):
            return None
    return None


def pid_alive(pid):
    """True only if pid exists AND is not a zombie (SIGKILLed children stay
    as zombies until reaped; os.kill(pid,0) still succeeds there)."""
    if not pid:
        return False
    try:
        with open(f"/proc/{pid}/stat") as f:
            data = f.read()
        state = data.rsplit(")", 1)[1].split()[0]
        return state != "Z"
    except (OSError, IndexError):
        try:
            os.kill(pid, 0)
            return True
        except OSError:
            return False


def sidecar_path(session_id):
    safe = "".join(c for c in session_id if c.isalnum() or c in "-_")
    return os.path.join(STATE_DIR, safe + ".json")


def load_sidecar(session_id):
    try:
        with open(sidecar_path(session_id)) as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_sidecar(session_id, data):
    os.makedirs(STATE_DIR, exist_ok=True)
    tmp = sidecar_path(session_id) + ".tmp"
    with open(tmp, "w") as f:
        json.dump(data, f)
    os.replace(tmp, sidecar_path(session_id))


def remove_sidecar(session_id):
    try:
        os.remove(sidecar_path(session_id))
    except OSError:
        pass


def post_status(url, token, envelope):
    body = json.dumps(envelope).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(
        url.rstrip("/") + "/api/v1/status",
        data=body,
        headers=headers,
        method="POST")
    with urllib.request.urlopen(req, timeout=POST_TIMEOUT_S) as resp:
        resp.read()


def spawn_watchdog(session_id):
    sidecar_file = sidecar_path(session_id)
    here = os.path.dirname(os.path.abspath(__file__))
    script = os.path.join(here, "agentbeacon_watchdog.py")
    try:
        os.makedirs(STATE_DIR, exist_ok=True)
        log = open(sidecar_file + ".watchdog.log", "ab", buffering=0)
        subprocess.Popen(
            [sys.executable, script, "--sidecar", sidecar_file],
            stdout=log,
            stderr=log,
            stdin=subprocess.DEVNULL,
            start_new_session=True)
        log.close()
    except OSError as e:  # pragma: no cover
        print(f"agentbeacon: watchdog spawn failed: {e}", file=sys.stderr)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main():
    try:
        raw = sys.stdin.read()
        event = json.loads(raw) if raw.strip() else {}
    except ValueError:
        event = {}

    session_id = event.get("session_id")
    hook_event = event.get("hook_event_name")
    if not session_id or not hook_event:
        return 0

    url, token = resolve_config()
    state = load_sidecar(session_id)
    now = time.time()

    if hook_event == "SessionEnd":
        # Codex's SessionEnd fires only on orderly teardown, its reason is
        # always "other", and it gets a 1-3s budget. If the last status was
        # still running/approval, the work was cut short → failed.
        last = state.get("last_status")
        if last in ("running", "approval") and url:
            try:
                post_status(url, token, build_envelope(
                    session_id, "failed",
                    f"session ended while {last}",
                    socket.gethostname()))
            except (urllib.error.URLError, OSError, ValueError) as e:
                print(f"agentbeacon: post failed: {e}", file=sys.stderr)
        state["ended"] = True
        state["updated_at"] = now
        remove_sidecar(session_id)
        return 0

    status = map_event(hook_event)
    if status is None:
        return 0

    if not should_post(state.get("last_status"), state.get("last_post_at"),
                       status, now):
        state["session_id"] = session_id
        state["updated_at"] = now
        save_sidecar(session_id, state)
        return 0

    message = None
    if hook_event == "UserPromptSubmit":
        prompt = event.get("prompt") or ""
        message = (prompt[:100] + "…") if len(prompt) > 100 else prompt
    else:
        message = (RUNNING_EVENTS.get(hook_event)
                   or APPROVAL_EVENTS.get(hook_event)
                   or COMPLETED_EVENTS.get(hook_event))

    if url:
        try:
            post_status(url, token, build_envelope(
                session_id, status, message, socket.gethostname()))
        except (urllib.error.URLError, OSError, ValueError) as e:
            print(f"agentbeacon: post failed: {e}", file=sys.stderr)
    else:
        print("agentbeacon: AGENTBEACON_URL not set; status not sent",
              file=sys.stderr)

    state["session_id"] = session_id
    state["last_status"] = status
    state["last_post_at"] = now
    state["updated_at"] = now
    if "codex_pid" not in state:
        state["codex_pid"] = find_codex_pid()
        if state["codex_pid"] is not None and hook_event == "SessionStart":
            spawn_watchdog(session_id)
    save_sidecar(session_id, state)
    return 0


if __name__ == "__main__":
    sys.exit(main())
