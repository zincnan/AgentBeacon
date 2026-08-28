"""Local validation tests for notify/agent_notify.py.

These tests verify local argument validation and exit codes.
For tests that exercise the network path, we start a tiny TCP
server that accepts and immediately closes the connection so the
notify script sees a fast transport-level failure rather than relying
on WSL2 NAT behaviour (which can hang on closed 127.0.0.1 ports).
"""

import os
import socket
import subprocess
import sys
import threading
import unittest

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NOTIFY = os.path.join(REPO, "notify", "agent_notify.py")
PY = sys.executable

BASE_ENV = {
    "PATH": os.environ.get("PATH", ""),
    "HOME": os.environ.get("HOME", ""),
    "LANG": os.environ.get("LANG", "C.UTF-8"),
}


def run(args, env_extra=None):
    env = dict(BASE_ENV)
    if env_extra:
        env.update(env_extra)
    proc = subprocess.run(
        [PY, NOTIFY, *args],
        env=env,
        capture_output=True,
        text=True,
        timeout=15,
    )
    return proc.returncode, proc.stdout, proc.stderr


class _RefusingServer:
    """Accept connections on 127.0.0.1 and close them immediately.

    This produces a fast, deterministic 'transport error' for callers
    that successfully connect but then see EOF / connection reset.
    """

    def __init__(self) -> None:
        self._srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._srv.bind(("127.0.0.1", 0))
        self._srv.listen(8)
        self.port = self._srv.getsockname()[1]
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def _loop(self) -> None:
        self._srv.settimeout(0.2)
        while not self._stop.is_set():
            try:
                conn, _ = self._srv.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            try:
                conn.close()
            except OSError:
                pass

    def close(self) -> None:
        self._stop.set()
        try:
            self._srv.close()
        except OSError:
            pass
        self._thread.join(timeout=2)

    def __enter__(self) -> "_RefusingServer":
        return self

    def __exit__(self, *exc) -> None:
        self.close()


class TestNotifyLocal(unittest.TestCase):
    def test_missing_url_returns_4(self):
        c, _, err = run([
            "--session-id", "x", "--agent", "y", "--status", "running",
            "--token", "t",
        ])
        self.assertEqual(c, 4)
        self.assertIn("AGENTBEACON_URL", err)

    def test_missing_token_returns_4(self):
        c, _, err = run([
            "--session-id", "x", "--agent", "y", "--status", "running",
            "--url", "http://127.0.0.1:1",
        ])
        self.assertEqual(c, 4)
        self.assertIn("AGENTBEACON_TOKEN", err)

    def test_invalid_status_rejected_by_argparse(self):
        c, _, _ = run([
            "--session-id", "x", "--agent", "y", "--status", "bogus",
            "--url", "http://127.0.0.1:1", "--token", "t",
        ])
        self.assertEqual(c, 4)

    def test_oversized_session_id_returns_4(self):
        c, _, err = run([
            "--session-id", "x" * 300, "--agent", "y", "--status", "running",
            "--url", "http://127.0.0.1:1", "--token", "t",
        ])
        self.assertEqual(c, 4)
        self.assertIn("session_id", err.lower())

    def test_env_vars_used_when_no_cli_override(self):
        # env vars provide URL/token; no CLI override. The remote closes
        # immediately so we get a transport-level error (exit 3), proving
        # the env-var path actually reached the network.
        with _RefusingServer() as srv:
            c, _, _ = run([
                "--session-id", "x", "--agent", "y", "--status", "running",
            ], env_extra={
                "AGENTBEACON_URL": f"http://127.0.0.1:{srv.port}",
                "AGENTBEACON_TOKEN": "t",
            })
            self.assertEqual(c, 3)

    def test_cli_url_overrides_env_var(self):
        # env URL points elsewhere; CLI URL points at the refusing server.
        # exit 3 means the CLI URL was actually used.
        with _RefusingServer() as srv:
            c, _, _ = run([
                "--session-id", "x", "--agent", "y", "--status", "running",
                "--url", f"http://127.0.0.1:{srv.port}",
                "--token", "t",
            ], env_extra={"AGENTBEACON_URL": "http://127.0.0.1:1"})
            self.assertEqual(c, 3)

    def test_long_message_does_not_fail_validation(self):
        # 800-char message is locally truncated to 512 and the call
        # proceeds. The refusing server then forces exit 3, proving
        # message length did not refuse the call locally.
        with _RefusingServer() as srv:
            c, _, err = run([
                "--session-id", "x", "--agent", "y", "--status", "running",
                "--message", "m" * 800,
                "--url", f"http://127.0.0.1:{srv.port}",
                "--token", "t",
            ])
            self.assertEqual(c, 3, f"expected 3 (transport), got {c}: {err}")


if __name__ == "__main__":
    unittest.main()