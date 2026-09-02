#!/usr/bin/env python3
"""AgentBeacon adapter for Claude Code (plugin hook entry).

Reads one Claude Code hook-event JSON from stdin, translates the event
into an AgentBeacon status, and POSTs it to the Receiver — same
transport semantics as the repo's generic `agent-notify` CLI (single
POST, no retry, short timeout, always exit 0 so the hook can never
block or break Claude Code).

Event → status mapping (see docs/adapter-claude-code.md):

  SessionStart → completed          (session boot / resume: idle & ready → GREEN)
  UserPromptSubmit / PreToolUse / PostToolUse / PermissionDenied → running
  PermissionRequest                 → approval
  Stop                              → completed
  StopFailure                       → failed
  SessionEnd                        → (see failed semantics; cleanup)

Blue immediately on approval grant: PreToolUse fires the moment the
(now-approved) tool starts executing, so yes/no both land on running
right away (deny via PermissionDenied, grant via PreToolUse).

Failed-on-crash handling: a persistent fatal API error (e.g. the model
endpoint drops and the CLI sits stuck at "API error") emits NO hook
event — empirically verified. To still surface a red light we record
the claude process PID in a per-session sidecar file and spawn a tiny
watchdog (agentbeacon_watchdog.py) at SessionStart; when the claude
process dies while the last reported status is still running/approval,
the watchdog POSTs `failed`. A clean exit runs SessionEnd first, which
removes the sidecar so the watchdog never fires on normal shutdown.

Config (first hit wins):
  CLAUDE_PLUGIN_OPTION_AGENTBEACON_URL / AGENTBEACON_URL
  CLAUDE_PLUGIN_OPTION_AGENTBEACON_TOKEN / AGENTBEACON_TOKEN
"""

import json
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

AGENT = "claude-code"
POST_TIMEOUT_S = 3.0
THROTTLE_S = 2.0  # skip duplicate POST of the same status within this window

STATE_DIR = os.path.join(
    os.environ.get("TMPDIR", "/tmp"), "agentbeacon-claude-code")

# ---------------------------------------------------------------------------
# Pure logic (unit-tested in tests/test_hook_adapter.py)
# ---------------------------------------------------------------------------

RUNNING_EVENTS = {
    "UserPromptSubmit": None,  # message filled from the prompt text
    "PreToolUse": "tool started",
    "PostToolUse": "tool finished",
    "PermissionDenied": "permission denied — continuing",
}
APPROVAL_EVENTS = {"PermissionRequest": "permission requested"}
# SessionStart (fresh boot or resume) means "alive but no task running":
# per UI policy that is the GREEN idle state, not blue. It also means a
# claude that is opened and killed without any interaction does NOT
# produce a false red (last status is completed, not running).
COMPLETED_EVENTS = {
    "SessionStart": "session started — idle",
    "Stop": "turn completed",
}
FAILED_EVENTS = {"StopFailure": "turn failed"}
# SessionEnd / everything else → no status post (SessionEnd only cleans up).


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
    """Build the Protocol v1 POST body."""
    env = {"session_id": session_id, "agent": agent, "status": status}
    if message:
        env["message"] = str(message)[:512]
    if host:
        env["host"] = host
    return env


def resolve_config(env=None):
    """Resolve (url, token) from plugin options / environment."""
    env = env if env is not None else os.environ

    def pick(opt, plain):
        v = env.get(opt) or env.get(plain)
        return v or None

    return (
        pick("CLAUDE_PLUGIN_OPTION_AGENTBEACON_URL", "AGENTBEACON_URL"),
        pick("CLAUDE_PLUGIN_OPTION_AGENTBEACON_TOKEN", "AGENTBEACON_TOKEN"),
    )


def should_post(last_status, last_post_epoch, status, now_epoch,
                throttle_s=THROTTLE_S):
    """True when this status should actually be POSTed.

    Skips exact duplicates within the throttle window (e.g. when two
    hook events map to the same status back to back).
    """
    if last_status == status and last_post_epoch is not None:
        if now_epoch - last_post_epoch < throttle_s:
            return False
    return True


def watchdog_decide(sidecar, pid_alive, now_epoch, stale_s=24 * 3600):
    """Decide what the watchdog should do for one session sidecar.

    Returns one of:
      "failed"  — claude process is dead but last status was running or
                  approval → POST failed (crash / kill / quit-after-API-error)
      "cleanup" — session is over or hopelessly stale → remove the sidecar
      "none"    — nothing to do
    """
    last = sidecar.get("last_status")
    if sidecar.get("ended"):
        return "cleanup"
    pid = sidecar.get("claude_pid")
    if pid is None:
        # Never identified the claude process — cannot judge liveness;
        # only clear the sidecar once it is hopelessly stale.
        if now_epoch - sidecar.get("updated_at", 0) > stale_s:
            return "cleanup"
        return "none"
    if pid_alive(pid):
        return "none"
    if last in ("running", "approval"):
        # Grace: the SessionEnd hook may not have run yet (normal exit
        # races the watchdog poll). Require the pid to have been dead
        # for a moment before declaring failure.
        died_at = sidecar.get("claude_pid_lost_at")
        if died_at is None:
            return "mark-lost"  # caller records claude_pid_lost_at=now
        if now_epoch - died_at < 10:
            return "none"
        return "failed"
    # dead pid + terminal last status (completed/failed) or no pid known
    if now_epoch - sidecar.get("updated_at", 0) > stale_s:
        return "cleanup"
    return "cleanup"


# ---------------------------------------------------------------------------
# Runtime helpers
# ---------------------------------------------------------------------------

def find_claude_pid():
    """Walk up the process tree from this hook's parent to find the
    running `claude` CLI process. Returns its pid or None (Linux/WSL)."""
    pid = os.getppid()
    for _ in range(10):
        if pid <= 1:
            return None
        try:
            with open(f"/proc/{pid}/cmdline", "rb") as f:
                cmdline = f.read().decode("utf-8", "replace")
        except OSError:
            return None
        if "claude" in cmdline:
            return pid
        try:
            with open(f"/proc/{pid}/stat") as f:
                pid = int(f.read().split(") ", 1)[1].split()[1])
        except (OSError, ValueError, IndexError):
            return None
    return None


def pid_alive(pid):
    """True only if pid exists AND is not a zombie.

    A SIGKILLed process whose parent never reaps it stays as a zombie —
    os.kill(pid, 0) still succeeds there, so we must read the process
    state from /proc instead. Falls back to the signal probe on systems
    without /proc.
    """
    if not pid:
        return False
    try:
        with open(f"/proc/{pid}/stat") as f:
            data = f.read()
        # state is the field right after the closing paren of comm
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
        # No token → no Authorization header; works against a Receiver
        # started with --no-auth, 401s against a token-mode Receiver.
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(
        url.rstrip("/") + "/api/v1/status",
        data=body,
        headers=headers,
        method="POST")
    with urllib.request.urlopen(req, timeout=POST_TIMEOUT_S) as resp:
        resp.read()


def spawn_watchdog(session_id):
    """Start one detached watchdog for this session (best-effort).

    stderr goes to a per-session log file so a crashed watchdog is
    diagnosable after the fact (hooks' own stderr vanishes into the
    CLI's internal logs).
    """
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
    except OSError as e:  # pragma: no cover - spawn failure is non-fatal
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
        # Session over. If the last reported status is still running or
        # approval, the work never completed — the session was terminated
        # mid-flight (user closed the terminal while stuck at an API
        # error, quit mid-run, crashed, ...). Per the AgentBeacon failed
        # semantics ("session-level fatal termination"), report failed.
        # After a normal Stop the last status is already completed and
        # nothing extra is posted.
        last = state.get("last_status")
        reason = event.get("reason") or "unknown"
        if last in ("running", "approval") and url:
            try:
                post_status(url, token, build_envelope(
                    session_id, "failed",
                    f"session ended while {last} (reason: {reason})",
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
        # Still refresh heartbeat + pid bookkeeping.
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
                   or COMPLETED_EVENTS.get(hook_event)
                   or FAILED_EVENTS.get(hook_event))

    if url:
        try:
            post_status(url, token, build_envelope(
                session_id, status, message, socket.gethostname()))
        except (urllib.error.URLError, OSError, ValueError) as e:
            print(f"agentbeacon: post failed: {e}", file=sys.stderr)
    else:
        # No Receiver configured; nothing we can do quietly.
        print("agentbeacon: AGENTBEACON_URL not set; status not sent",
              file=sys.stderr)

    state["session_id"] = session_id
    state["last_status"] = status
    state["last_post_at"] = now
    state["updated_at"] = now
    if "claude_pid" not in state:
        state["claude_pid"] = find_claude_pid()
        if state["claude_pid"] is not None and hook_event == "SessionStart":
            spawn_watchdog(session_id)
    save_sidecar(session_id, state)
    return 0


if __name__ == "__main__":
    sys.exit(main())
