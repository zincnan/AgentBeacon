#!/usr/bin/env python3
"""Tests for the Codex CLI adapter (plugins/codex). Same shape as
tests/test_hook_adapter.py: pure logic + subprocess end-to-end against a
local recording Receiver."""

import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS = os.path.join(REPO, "plugins", "codex", "scripts")
sys.path.insert(0, SCRIPTS)

import agentbeacon_codex_hook as adapter  # noqa: E402

HOOK = os.path.join(SCRIPTS, "agentbeacon_codex_hook.py")


class RecordingReceiver(BaseHTTPRequestHandler):
    posts = []

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        RecordingReceiver.posts.append(
            {"path": self.path,
             "auth": self.headers.get("Authorization"),
             "body": json.loads(body)})
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        payload = b'{"ok":true}'
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *a):
        pass


def event(name, session="sess-1", **extra):
    doc = {"session_id": session, "hook_event_name": name}
    doc.update(extra)
    return json.dumps(doc)


class TestMapping(unittest.TestCase):
    def test_session_start_is_green_idle(self):
        self.assertEqual(adapter.map_event("SessionStart"), "completed")

    def test_running_events(self):
        for e in ("UserPromptSubmit", "PreToolUse", "PostToolUse"):
            self.assertEqual(adapter.map_event(e), "running", e)

    def test_approval_completed(self):
        self.assertEqual(adapter.map_event("PermissionRequest"), "approval")
        self.assertEqual(adapter.map_event("Stop"), "completed")

    def test_ignored_events(self):
        # Codex has no error hook; Interrupt and unknown events are noise.
        for e in ("SessionEnd", "Interrupt", "SubagentStop", "PreCompact",
                  ""):
            self.assertIsNone(adapter.map_event(e), e)


class TestEnvelope(unittest.TestCase):
    def test_agent_is_codex(self):
        env = adapter.build_envelope("s1", "running", "hi", "host-a")
        self.assertEqual(env["agent"], "codex")
        self.assertEqual(env["status"], "running")
        self.assertEqual(env["message"], "hi")

    def test_message_truncated_to_512(self):
        env = adapter.build_envelope("s1", "failed", "x" * 2000)
        self.assertEqual(len(env["message"]), 512)


class TestConfig(unittest.TestCase):
    def test_env_beats_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "ab.json")
            with open(path, "w") as f:
                json.dump({"url": "http://from-file:8765", "token": "ft"}, f)
            url, tok = adapter.resolve_config({
                "AGENTBEACON_CONFIG": path,
                "AGENTBEACON_URL": "http://from-env:8765",
            })
            self.assertEqual(url, "http://from-env:8765")
            self.assertEqual(tok, "ft", "token still falls through to file")

    def test_config_file_supplies_both(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "ab.json")
            with open(path, "w") as f:
                json.dump({"url": "http://from-file:8765", "token": "ft"}, f)
            self.assertEqual(
                adapter.resolve_config({"AGENTBEACON_CONFIG": path}),
                ("http://from-file:8765", "ft"))

    def test_missing(self):
        self.assertEqual(adapter.resolve_config(
            {"AGENTBEACON_CONFIG": "/nonexistent/ab.json"}), (None, None))


class TestThrottle(unittest.TestCase):
    def test_duplicate_within_window_skipped(self):
        self.assertFalse(
            adapter.should_post("running", 100.0, "running", 101.0))

    def test_status_change_always_posted(self):
        self.assertTrue(
            adapter.should_post("running", 100.0, "approval", 100.5))


class TestWatchdogDecision(unittest.TestCase):
    def setUp(self):
        self.now = 1000.0

    def test_alive_is_none(self):
        sc = {"codex_pid": 123, "last_status": "running",
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: True, self.now), "none")

    def test_dead_running_marks_lost_then_failed(self):
        sc = {"codex_pid": 123, "last_status": "running",
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now),
            "mark-lost")
        sc["codex_pid_lost_at"] = self.now - 30
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now), "failed")

    def test_dead_completed_cleans_up(self):
        sc = {"codex_pid": 123, "last_status": "completed",
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now),
            "cleanup")

    def test_unknown_pid_never_fires_failed(self):
        sc = {"codex_pid": None, "last_status": "running",
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now), "none")


class TestHookScriptEndToEnd(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), RecordingReceiver)
        cls.port = cls.srv.server_address[1]
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.tmp = tempfile.mkdtemp(prefix="ab-codex-test-")

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()

    def run_hook(self, payload, token="test-token"):
        env = dict(os.environ)
        env["AGENTBEACON_URL"] = f"http://127.0.0.1:{self.port}"
        if token is None:
            env.pop("AGENTBEACON_TOKEN", None)
        else:
            env["AGENTBEACON_TOKEN"] = token
        env["TMPDIR"] = self.tmp
        p = subprocess.run(
            [sys.executable, HOOK], input=payload.encode(),
            capture_output=True, env=env, timeout=15)
        return p

    def sidecar_file(self, session):
        safe = "".join(c for c in session if c.isalnum() or c in "-_")
        return os.path.join(self.tmp, "agentbeacon-codex", safe + ".json")

    def test_lifecycle_chain(self):
        RecordingReceiver.posts.clear()
        for ev in ("SessionStart",          # → completed (green idle)
                   "UserPromptSubmit",      # → running
                   "PermissionRequest",     # → approval
                   "Stop"):                 # → completed
            p = self.run_hook(event(ev, session="cx-1"))
            self.assertEqual(p.returncode, 0, p.stderr)
        statuses = [p["body"]["status"] for p in RecordingReceiver.posts]
        self.assertEqual(statuses, ["completed", "running", "approval",
                                    "completed"])
        agents = {p["body"]["agent"] for p in RecordingReceiver.posts}
        self.assertEqual(agents, {"codex"})

    def test_session_end_while_running_reports_failed(self):
        RecordingReceiver.posts.clear()
        self.run_hook(event("UserPromptSubmit", session="cx-2",
                            prompt="working"))
        RecordingReceiver.posts.clear()
        p = self.run_hook(event("SessionEnd", session="cx-2"))
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(len(RecordingReceiver.posts), 1)
        body = RecordingReceiver.posts[0]["body"]
        self.assertEqual(body["status"], "failed")
        self.assertIn("session ended while running", body["message"])
        self.assertFalse(os.path.exists(self.sidecar_file("cx-2")))

    def test_session_end_after_completed_posts_nothing(self):
        RecordingReceiver.posts.clear()
        self.run_hook(event("SessionStart", session="cx-3"))
        self.run_hook(event("Stop", session="cx-3"))
        RecordingReceiver.posts.clear()
        p = self.run_hook(event("SessionEnd", session="cx-3"))
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(RecordingReceiver.posts, [])
        self.assertFalse(os.path.exists(self.sidecar_file("cx-3")))

    def test_no_token_omits_authorization_header(self):
        RecordingReceiver.posts.clear()
        p = self.run_hook(event("SessionStart", session="cx-4"), token=None)
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(len(RecordingReceiver.posts), 1)
        self.assertIsNone(RecordingReceiver.posts[0]["auth"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
