#!/usr/bin/env python3
"""Tests for the Claude Code plugin adapter (plugins/claude-code).

Pure-logic tests (mapping / envelope / throttle / watchdog decisions)
plus small end-to-end tests that run agentbeacon_hook.py as a
subprocess against a local HTTP server standing in for the Receiver.
"""

import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS = os.path.join(REPO, "plugins", "claude-code", "scripts")
sys.path.insert(0, SCRIPTS)

import agentbeacon_hook as adapter  # noqa: E402

HOOK = os.path.join(SCRIPTS, "agentbeacon_hook.py")


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
    def test_running_events(self):
        for e in ("UserPromptSubmit", "PreToolUse", "PostToolUse",
                  "PermissionDenied"):
            self.assertEqual(adapter.map_event(e), "running", e)

    def test_session_start_is_green_idle(self):
        # Fresh boot / resume: no task running, no error → completed
        # (green), NOT running (blue).
        self.assertEqual(adapter.map_event("SessionStart"), "completed")

    def test_approval_completed_failed(self):
        self.assertEqual(adapter.map_event("PermissionRequest"), "approval")
        self.assertEqual(adapter.map_event("Stop"), "completed")
        self.assertEqual(adapter.map_event("StopFailure"), "failed")

    def test_ignored_events(self):
        for e in ("SessionEnd", "SubagentStop", "PreCompact", "Whatever",
                  ""):
            self.assertIsNone(adapter.map_event(e), e)


class TestEnvelope(unittest.TestCase):
    def test_fields(self):
        env = adapter.build_envelope("s1", "running", "hi", "host-a")
        self.assertEqual(env["session_id"], "s1")
        self.assertEqual(env["agent"], "claude-code")
        self.assertEqual(env["status"], "running")
        self.assertEqual(env["message"], "hi")
        self.assertEqual(env["host"], "host-a")

    def test_optional_fields_omitted(self):
        env = adapter.build_envelope("s1", "completed")
        self.assertNotIn("message", env)
        self.assertNotIn("host", env)

    def test_message_truncated_to_512(self):
        env = adapter.build_envelope("s1", "failed", "x" * 2000)
        self.assertEqual(len(env["message"]), 512)


class TestConfig(unittest.TestCase):
    def test_plugin_option_wins(self):
        url, tok = adapter.resolve_config({
            "CLAUDE_PLUGIN_OPTION_AGENTBEACON_URL": "http://a",
            "AGENTBEACON_URL": "http://b",
            "CLAUDE_PLUGIN_OPTION_AGENTBEACON_TOKEN": "t1",
            "AGENTBEACON_TOKEN": "t2",
        })
        self.assertEqual(url, "http://a")
        self.assertEqual(tok, "t1")

    def test_plain_env_fallback(self):
        url, tok = adapter.resolve_config({
            "AGENTBEACON_URL": "http://b", "AGENTBEACON_TOKEN": "t2"})
        self.assertEqual(url, "http://b")
        self.assertEqual(tok, "t2")

    def test_missing(self):
        self.assertEqual(adapter.resolve_config(
            {"AGENTBEACON_CONFIG": "/nonexistent/ab.json"}), (None, None))

    def test_config_file_supplies_url_and_token(self):
        # Round 7: ~/.agentbeacon.json is the edit-once fallback layer.
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "ab.json")
            with open(path, "w") as f:
                json.dump({"url": "http://from-file:8765", "token": "ft"}, f)
            url, tok = adapter.resolve_config({"AGENTBEACON_CONFIG": path})
            self.assertEqual(url, "http://from-file:8765")
            self.assertEqual(tok, "ft")

    def test_env_beats_config_file(self):
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

    def test_broken_config_file_ignored(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "ab.json")
            with open(path, "w") as f:
                f.write("{not json")
            self.assertEqual(
                adapter.resolve_config({"AGENTBEACON_CONFIG": path}),
                (None, None))


class TestThrottle(unittest.TestCase):
    def test_duplicate_within_window_skipped(self):
        self.assertFalse(
            adapter.should_post("running", 100.0, "running", 101.0))

    def test_duplicate_after_window_posted(self):
        self.assertTrue(
            adapter.should_post("running", 100.0, "running", 103.0))

    def test_status_change_always_posted(self):
        self.assertTrue(
            adapter.should_post("running", 100.0, "approval", 100.5))

    def test_first_post(self):
        self.assertTrue(adapter.should_post(None, None, "running", 1.0))


class TestWatchdogDecision(unittest.TestCase):
    def setUp(self):
        self.now = 1000.0

    def test_alive_is_none(self):
        sc = {"claude_pid": 123, "last_status": "running",
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: True, self.now), "none")

    def test_dead_running_first_marks_lost(self):
        sc = {"claude_pid": 123, "last_status": "running",
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now),
            "mark-lost")

    def test_dead_running_after_grace_fires_failed(self):
        sc = {"claude_pid": 123, "last_status": "running",
              "claude_pid_lost_at": self.now - 30,
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now), "failed")

    def test_dead_approval_also_fires_failed(self):
        sc = {"claude_pid": 123, "last_status": "approval",
              "claude_pid_lost_at": self.now - 30,
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now), "failed")

    def test_dead_completed_cleans_up(self):
        sc = {"claude_pid": 123, "last_status": "completed",
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now),
            "cleanup")

    def test_ended_cleans_up(self):
        sc = {"claude_pid": 123, "last_status": "running", "ended": True,
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now),
            "cleanup")

    def test_unknown_pid_never_fires_failed(self):
        sc = {"claude_pid": None, "last_status": "running",
              "updated_at": self.now}
        self.assertEqual(
            adapter.watchdog_decide(sc, lambda p: False, self.now), "none")


class TestHookScriptEndToEnd(unittest.TestCase):
    """Run agentbeacon_hook.py as a subprocess against a local server."""

    @classmethod
    def setUpClass(cls):
        cls.srv = ThreadingHTTPServer(("127.0.0.1", 0), RecordingReceiver)
        cls.port = cls.srv.server_address[1]
        threading.Thread(target=cls.srv.serve_forever, daemon=True).start()
        cls.tmp = tempfile.mkdtemp(prefix="ab-adapter-test-")

    @classmethod
    def tearDownClass(cls):
        cls.srv.shutdown()
        cls.srv.server_close()

    def run_hook(self, payload, token="test-token", extra_env=None):
        env = dict(os.environ)
        env["AGENTBEACON_URL"] = f"http://127.0.0.1:{self.port}"
        if token is None:
            env.pop("AGENTBEACON_TOKEN", None)
        else:
            env["AGENTBEACON_TOKEN"] = token
        env["TMPDIR"] = self.tmp
        env.pop("CLAUDE_PLUGIN_OPTION_AGENTBEACON_URL", None)
        if extra_env:
            env.update(extra_env)
        p = subprocess.run(
            [sys.executable, HOOK], input=payload.encode(),
            capture_output=True, env=env, timeout=15)
        return p

    def sidecar_file(self, session):
        safe = "".join(c for c in session if c.isalnum() or c in "-_")
        return os.path.join(self.tmp, "agentbeacon-claude-code",
                            safe + ".json")

    def test_session_start_posts_completed_and_writes_sidecar(self):
        RecordingReceiver.posts.clear()
        p = self.run_hook(event("SessionStart", session="e2e-1"))
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(len(RecordingReceiver.posts), 1)
        post = RecordingReceiver.posts[0]
        self.assertEqual(post["path"], "/api/v1/status")
        self.assertEqual(post["auth"], "Bearer test-token")
        self.assertEqual(post["body"]["status"], "completed")
        self.assertEqual(post["body"]["agent"], "claude-code")
        self.assertEqual(post["body"]["session_id"], "e2e-1")
        self.assertTrue(os.path.exists(self.sidecar_file("e2e-1")))

    def test_lifecycle_chain_maps_correctly(self):
        RecordingReceiver.posts.clear()
        for ev in ("UserPromptSubmit",      # → running
                   "PermissionRequest",     # → approval (yellow)
                   "PreToolUse",            # → running (instant blue on grant)
                   "PostToolUse",           # → running (throttled, same <2s)
                   "Stop"):                 # → completed
            p = self.run_hook(event(ev, session="e2e-2"))
            self.assertEqual(p.returncode, 0, p.stderr)
        # PostToolUse lands <2s after PreToolUse with the same status →
        # throttled by design (sidecar still records the beat).
        statuses = [p["body"]["status"] for p in RecordingReceiver.posts]
        self.assertEqual(statuses, ["running", "approval", "running",
                                    "completed"])

    def test_throttle_dedupes_rapid_duplicates(self):
        RecordingReceiver.posts.clear()
        payload = event("PostToolUse", session="e2e-3")
        self.run_hook(payload)
        self.run_hook(payload)  # same status within 2s → suppressed
        self.assertEqual(len(RecordingReceiver.posts), 1)

    def test_session_end_after_completed_posts_nothing(self):
        RecordingReceiver.posts.clear()
        self.run_hook(event("SessionStart", session="e2e-4"))
        self.run_hook(event("Stop", session="e2e-4"))
        RecordingReceiver.posts.clear()
        p = self.run_hook(event("SessionEnd", session="e2e-4"))
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(RecordingReceiver.posts, [])
        self.assertFalse(os.path.exists(self.sidecar_file("e2e-4")))

    def test_session_end_while_running_reports_failed(self):
        # The user's failure scenario: session terminated mid-work
        # (e.g. closed the terminal after the CLI got stuck at an API
        # error). SessionEnd must surface `failed`, not silence.
        RecordingReceiver.posts.clear()
        self.run_hook(event("UserPromptSubmit", session="e2e-6",
                            prompt="working..."))
        RecordingReceiver.posts.clear()
        p = self.run_hook(event("SessionEnd", session="e2e-6",
                                reason="prompt_input_exit"))
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(len(RecordingReceiver.posts), 1)
        body = RecordingReceiver.posts[0]["body"]
        self.assertEqual(body["status"], "failed")
        self.assertIn("session ended while running", body["message"])
        self.assertFalse(os.path.exists(self.sidecar_file("e2e-6")))

    def test_session_end_while_approval_reports_failed(self):
        RecordingReceiver.posts.clear()
        self.run_hook(event("PermissionRequest", session="e2e-7"))
        RecordingReceiver.posts.clear()
        p = self.run_hook(event("SessionEnd", session="e2e-7"))
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(RecordingReceiver.posts[0]["body"]["status"],
                         "failed")

    def test_prompt_becomes_message(self):
        RecordingReceiver.posts.clear()
        self.run_hook(event("UserPromptSubmit", session="e2e-5",
                            prompt="fix the login bug"))
        self.assertEqual(RecordingReceiver.posts[0]["body"]["message"],
                         "fix the login bug")

    def test_no_token_omits_authorization_header(self):
        # Round 6 dual auth modes: without a token configured, the POST
        # must carry NO Authorization header at all (works against a
        # --no-auth Receiver; 401s against a token-mode Receiver).
        RecordingReceiver.posts.clear()
        p = self.run_hook(event("SessionStart", session="e2e-8"),
                          token=None)
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(len(RecordingReceiver.posts), 1)
        self.assertIsNone(RecordingReceiver.posts[0]["auth"])

    def test_config_file_drives_end_to_end_post(self):
        # Round 7: ~/.agentbeacon.json (here via AGENTBEACON_CONFIG)
        # supplies BOTH the url and the token when env has neither.
        cfg = os.path.join(self.tmp, "ab.json")
        with open(cfg, "w") as f:
            json.dump({"url": f"http://127.0.0.1:{self.port}",
                       "token": "file-tok"}, f)
        RecordingReceiver.posts.clear()
        p = self.run_hook(event("SessionStart", session="e2e-9"),
                          token=None,
                          extra_env={"AGENTBEACON_CONFIG": cfg,
                                     "AGENTBEACON_URL": ""})
        # empty AGENTBEACON_URL above must NOT shadow the file (env only
        # wins when non-empty)
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertEqual(len(RecordingReceiver.posts), 1)
        self.assertEqual(RecordingReceiver.posts[0]["auth"],
                         "Bearer file-tok")


if __name__ == "__main__":
    unittest.main(verbosity=2)
