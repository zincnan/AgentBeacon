"""End-to-end HTTP protocol tests against the C# Receiver.

Spawns the Receiver as a subprocess, points it at a free localhost port,
and runs the full Protocol v1 matrix.
"""

import json
import os
import socket
import subprocess
import time
import unittest
import urllib.error
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RECEIVER_PROJECT = os.path.join(REPO, "receiver")
TOKEN = "test-token-placeholder"


def find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_ready(url: str, timeout: float = 30.0) -> None:
    deadline = time.time() + timeout
    last_err: Exception | None = None
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=1) as r:
                if r.status == 200:
                    return
        except (urllib.error.URLError, ConnectionError, OSError) as e:
            last_err = e
        time.sleep(0.2)
    raise RuntimeError(f"receiver did not become ready in {timeout}s: {last_err}")


def build_receiver() -> None:
    subprocess.run(
        ["dotnet", "build", RECEIVER_PROJECT, "-c", "Debug", "--nologo", "--verbosity", "quiet"],
        check=True,
        capture_output=True,
    )


_proc: subprocess.Popen | None = None
_port: int = 0


def setUpModule() -> None:
    global _proc, _port
    build_receiver()
    _port = find_free_port()
    env = os.environ.copy()
    env["AGENTBEACON_TOKEN"] = TOKEN
    _proc = subprocess.Popen(
        [
            "dotnet", "run",
            "--project", RECEIVER_PROJECT,
            "-c", "Debug", "--no-build",
            "--",
            "--bind", "127.0.0.1",
            "--port", str(_port),
            "--debug",
        ],
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.STDOUT,
    )
    wait_ready(f"http://127.0.0.1:{_port}/healthz", timeout=30)


def tearDownModule() -> None:
    if _proc and _proc.poll() is None:
        _proc.terminate()
        try:
            _proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            _proc.kill()
            _proc.wait()


class TestProtocol(unittest.TestCase):
    base = ""

    @classmethod
    def setUpClass(cls) -> None:
        cls.base = f"http://127.0.0.1:{_port}"

    # ----- helpers -----

    def post_status(
        self,
        body=None,
        *,
        token: str | None = TOKEN,
        content_type: str = "application/json",
        raw: bytes | None = None,
        bearer_prefix: str = "Bearer ",
    ):
        url = self.base + "/api/v1/status"
        data = raw if raw is not None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(url, data=data, method="POST")
        req.add_header("Content-Type", content_type)
        if token is not None:
            req.add_header("Authorization", bearer_prefix + token)
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def debug_sessions(self):
        req = urllib.request.Request(self.base + "/debug/sessions")
        req.add_header("Authorization", f"Bearer {TOKEN}")
        with urllib.request.urlopen(req, timeout=5) as r:
            return json.loads(r.read())

    # ----- happy path -----

    def test_running_no_optionals(self):
        c, b = self.post_status({
            "session_id": "s1", "agent": "claude-code", "status": "running",
        })
        self.assertEqual(c, 200)
        self.assertEqual(b, {"session_id": "s1", "status": "running"})

    def test_approval_with_message_and_host(self):
        c, _ = self.post_status({
            "session_id": "s2", "agent": "claude-code",
            "status": "approval", "message": "needs consent", "host": "ws-lan",
        })
        self.assertEqual(c, 200)

    def test_completed(self):
        c, _ = self.post_status({
            "session_id": "s3", "agent": "codex", "status": "completed",
        })
        self.assertEqual(c, 200)

    def test_failed(self):
        c, _ = self.post_status({
            "session_id": "s4", "agent": "codex",
            "status": "failed", "message": "boom",
        })
        self.assertEqual(c, 200)

    # ----- last received wins -----

    def test_overwrite_chain_visible_in_debug(self):
        sid = "s-overwrite"
        for st in ("running", "approval", "running", "completed"):
            c, _ = self.post_status({"session_id": sid, "agent": "claude-code", "status": st})
            self.assertEqual(c, 200)
        rec = next(d for d in self.debug_sessions() if d["session_id"] == sid)
        self.assertEqual(rec["status"], "completed")

    # ----- auth -----

    def test_missing_authorization_returns_401(self):
        c, b = self.post_status(
            {"session_id": "x", "agent": "y", "status": "running"},
            token=None,
        )
        self.assertEqual(c, 401)
        self.assertEqual(b["error"], "unauthorized")

    def test_wrong_token_returns_401(self):
        c, _ = self.post_status(
            {"session_id": "x", "agent": "y", "status": "running"},
            token="wrong-token",
        )
        self.assertEqual(c, 401)

    def test_malformed_bearer_returns_401(self):
        c, _ = self.post_status(
            {"session_id": "x", "agent": "y", "status": "running"},
            bearer_prefix="Token ",
        )
        self.assertEqual(c, 401)

    # ----- protocol errors -----

    def test_wrong_content_type_returns_415(self):
        c, _ = self.post_status({}, content_type="text/plain")
        self.assertEqual(c, 415)

    def test_oversized_body_returns_413(self):
        big = b'{"session_id":"x","agent":"y","status":"running","message":"' + b"z" * (9 * 1024) + b'"}'
        c, _ = self.post_status(raw=big)
        self.assertEqual(c, 413)

    def test_invalid_json_returns_400(self):
        c, b = self.post_status(raw=b"not json")
        self.assertEqual(c, 400)
        self.assertEqual(b["error"], "invalid_json")

    def test_missing_session_id_returns_400(self):
        c, b = self.post_status({"agent": "y", "status": "running"})
        self.assertEqual(c, 400)
        self.assertEqual(b["error"], "missing_field")

    def test_missing_agent_returns_400(self):
        c, _ = self.post_status({"session_id": "x", "status": "running"})
        self.assertEqual(c, 400)

    def test_missing_status_returns_400(self):
        c, _ = self.post_status({"session_id": "x", "agent": "y"})
        self.assertEqual(c, 400)

    def test_invalid_status_returns_400(self):
        c, b = self.post_status({"session_id": "x", "agent": "y", "status": "idle"})
        self.assertEqual(c, 400)
        self.assertEqual(b["error"], "invalid_status")

    def test_session_id_too_long_returns_422(self):
        c, b = self.post_status({
            "session_id": "x" * 257, "agent": "y", "status": "running",
        })
        self.assertEqual(c, 422)
        self.assertEqual(b["error"], "session_id_too_long")

    # ----- message truncation -----

    def test_long_message_truncated_to_512_not_rejected(self):
        sid = "s-longmsg"
        c, _ = self.post_status({
            "session_id": sid, "agent": "y", "status": "running",
            "message": "m" * 800,
        })
        self.assertEqual(c, 200)
        rec = next(d for d in self.debug_sessions() if d["session_id"] == sid)
        self.assertEqual(len(rec["message"]), 512)

    # ----- health -----

    def test_healthz_returns_200(self):
        with urllib.request.urlopen(self.base + "/healthz", timeout=5) as r:
            self.assertEqual(r.status, 200)

    # ----- body limits -----

    def test_content_length_above_cap_returns_413_immediately(self):
        # Build a body just over the 8 KiB cap so Content-Length header is set.
        big = b'{"session_id":"x","agent":"y","status":"running","message":"' + b"z" * (9 * 1024) + b'"}'
        req = urllib.request.Request(
            self.base + "/api/v1/status",
            data=big,
            method="POST",
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {TOKEN}",
            },
        )
        try:
            urllib.request.urlopen(req, timeout=5)
        except urllib.error.HTTPError as e:
            self.assertEqual(e.code, 413)

    def test_chunked_body_above_cap_returns_413(self):
        # Send Transfer-Encoding: chunked with a body > 8 KiB. No Content-Length,
        # so the receiver must enforce the cap during streaming read.
        big = b'{"session_id":"x","agent":"y","status":"running","message":"' + b"z" * (9 * 1024) + b'"}'
        s = socket.create_connection(("127.0.0.1", _port), timeout=5)
        try:
            head = (
                f"POST /api/v1/status HTTP/1.1\r\n"
                f"Host: 127.0.0.1:{_port}\r\n"
                f"Authorization: Bearer {TOKEN}\r\n"
                f"Content-Type: application/json\r\n"
                f"Transfer-Encoding: chunked\r\n"
                f"\r\n"
            ).encode()
            s.sendall(head)
            chunk_size = 1024
            for i in range(0, len(big), chunk_size):
                c = big[i:i + chunk_size]
                s.sendall(f"{len(c):x}\r\n".encode() + c + b"\r\n")
            s.sendall(b"0\r\n\r\n")

            s.settimeout(5)
            resp = b""
            try:
                while True:
                    chunk = s.recv(4096)
                    if not chunk:
                        break
                    resp += chunk
                    if b"\r\n\r\n" in resp:
                        # Got headers, drain a bit more then stop
                        s.settimeout(0.3)
                # don't break early — keep draining
            except socket.timeout:
                pass

            first_line = resp.split(b"\r\n", 1)[0].decode("ascii", errors="replace")
            self.assertTrue(
                first_line.startswith("HTTP/1.1 413"),
                f"expected 413, got: {first_line}",
            )
        finally:
            s.close()

    def test_body_exactly_at_cap_is_accepted(self):
        # Build a body of exactly 8 KiB (boundary).
        prefix = '{"session_id":"x","agent":"y","status":"running","message":"'
        suffix = '"}'
        target_len = 8 * 1024
        msg_len = target_len - len(prefix) - len(suffix)
        body = (prefix + ("z" * msg_len) + suffix).encode("utf-8")
        self.assertEqual(len(body), 8 * 1024)
        url = self.base + "/api/v1/status"
        req = urllib.request.Request(url, data=body, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Authorization", f"Bearer {TOKEN}")
        with urllib.request.urlopen(req, timeout=5) as r:
            self.assertEqual(r.status, 200)

    # ----- JSON root type -----

    def test_json_array_root_returns_400(self):
        c, b = self.post_status(raw=b"[]")
        self.assertEqual(c, 400)
        self.assertEqual(b["error"], "invalid_json")

    def test_json_string_root_returns_400(self):
        c, b = self.post_status(raw=b'"hello"')
        self.assertEqual(c, 400)
        self.assertEqual(b["error"], "invalid_json")

    def test_json_null_root_returns_400(self):
        c, b = self.post_status(raw=b"null")
        self.assertEqual(c, 400)
        self.assertEqual(b["error"], "invalid_json")

    def test_json_number_root_returns_400(self):
        c, b = self.post_status(raw=b"123")
        self.assertEqual(c, 400)
        self.assertEqual(b["error"], "invalid_json")

    # ----- Content-Type -----

    def test_content_type_with_charset_is_accepted(self):
        c, _ = self.post_status({
            "session_id": "s-ct-charset", "agent": "y", "status": "running",
        }, content_type="application/json; charset=utf-8")
        self.assertEqual(c, 200)

    def test_content_type_case_insensitive(self):
        c, _ = self.post_status({
            "session_id": "s-ct-case", "agent": "y", "status": "running",
        }, content_type="Application/JSON")
        self.assertEqual(c, 200)

    def test_content_type_jsonfoo_is_rejected(self):
        c, b = self.post_status({}, content_type="application/jsonfoo")
        self.assertEqual(c, 415)
        self.assertEqual(b["error"], "unsupported_media_type")

    # ----- host field type -----

    def test_host_missing_becomes_null(self):
        sid = "s-host-missing"
        c, _ = self.post_status({"session_id": sid, "agent": "y", "status": "running"})
        self.assertEqual(c, 200)
        rec = next(d for d in self.debug_sessions() if d["session_id"] == sid)
        self.assertIsNone(rec["host"])

    def test_host_explicit_null_becomes_null(self):
        sid = "s-host-null"
        c, _ = self.post_status({
            "session_id": sid, "agent": "y", "status": "running", "host": None,
        })
        self.assertEqual(c, 200)
        rec = next(d for d in self.debug_sessions() if d["session_id"] == sid)
        self.assertIsNone(rec["host"])

    def test_host_string_is_accepted(self):
        sid = "s-host-str"
        c, _ = self.post_status({
            "session_id": sid, "agent": "y", "status": "running", "host": "ws-lan",
        })
        self.assertEqual(c, 200)
        rec = next(d for d in self.debug_sessions() if d["session_id"] == sid)
        self.assertEqual(rec["host"], "ws-lan")

    def test_host_number_returns_400(self):
        c, b = self.post_status({
            "session_id": "x", "agent": "y", "status": "running", "host": 42,
        })
        self.assertEqual(c, 400)
        self.assertEqual(b["error"], "invalid_field_type")

    def test_host_boolean_returns_400(self):
        c, b = self.post_status({
            "session_id": "x", "agent": "y", "status": "running", "host": True,
        })
        self.assertEqual(c, 400)
        self.assertEqual(b["error"], "invalid_field_type")

    def test_host_overwritten_by_later_event_without_host(self):
        sid = "s-host-overwrite"
        self.post_status({
            "session_id": sid, "agent": "y", "status": "running", "host": "ws-lan",
        })
        # Second event without host: whole-event snapshot replaces; host becomes null.
        self.post_status({
            "session_id": sid, "agent": "y", "status": "completed",
        })
        rec = next(d for d in self.debug_sessions() if d["session_id"] == sid)
        self.assertEqual(rec["status"], "completed")
        self.assertIsNone(rec["host"])


class TestCliPortValidation(unittest.TestCase):
    """The receiver must reject bad --port values at startup with exit code 4,
    before Kestrel tries to bind. Each subprocess invocation is slow, so we
    batch them in one test."""

    def _run_with_port(self, port_value: str):
        proc = subprocess.run(
            [
                "dotnet", "run",
                "--project", RECEIVER_PROJECT,
                "-c", "Debug", "--no-build",
                "--",
                "--bind", "127.0.0.1",
                "--port", port_value,
                "--token", "t",
            ],
            capture_output=True, text=True, timeout=15,
        )
        return proc.returncode, proc.stderr

    def test_non_numeric_port_returns_4(self):
        code, err = self._run_with_port("not-a-number")
        self.assertEqual(code, 4, err)

    def test_zero_port_returns_4(self):
        code, err = self._run_with_port("0")
        self.assertEqual(code, 4, err)

    def test_negative_port_returns_4(self):
        code, err = self._run_with_port("-1")
        self.assertEqual(code, 4, err)

    def test_port_above_65535_returns_4(self):
        code, err = self._run_with_port("65536")
        self.assertEqual(code, 4, err)


if __name__ == "__main__":
    unittest.main()