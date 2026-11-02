"""Proxy routing regressions shared by the two independently shipped adapters."""

import json
import os
import subprocess
import sys
import tempfile
import threading
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import patch


class RecordingEndpoint(BaseHTTPRequestHandler):
    def do_POST(self):
        self.server.posts.append({
            "path": self.path,
            "auth": self.headers.get("Authorization"),
            "body": json.loads(self.rfile.read(int(self.headers["Content-Length"]))),
        })
        self.send_response(200)
        self.send_header("Content-Length", "2")
        self.end_headers()
        self.wfile.write(b"{}")

    def log_message(self, *args):
        pass


class ProxyRoutingCases:
    def test_hook_routes_receiver_and_proxy(self):
        servers = []
        for _ in range(2):
            server = ThreadingHTTPServer(("127.0.0.1", 0), RecordingEndpoint)
            server.posts = []
            worker = threading.Thread(
                target=server.serve_forever, kwargs={"poll_interval": 0.01})
            worker.start()
            servers.append((server, worker))

        receiver, proxy = [item[0] for item in servers]
        proxy_url = f"http://127.0.0.1:{proxy.server_port}"
        cases = [
            ("IPv4 CIDR", "127.0.0.1", {"no_proxy": "127.0.0.0/8"}, True),
            ("uppercase", "127.0.0.1", {"NO_PROXY": "127.0.0.0/8"}, True),
            ("mixed rules", "127.0.0.1", {
                "no_proxy": " localhost,172.16.0.0/16, 127.0.0.0/8 "}, True),
            ("outside CIDR", "127.0.0.1", {"no_proxy": "10.0.70.0/24"}, False),
            ("no bypass", "127.0.0.1", {}, False),
            ("exact IP", "127.0.0.1", {"no_proxy": "127.0.0.1"}, True),
            ("domain", "localhost", {"no_proxy": "localhost"}, True),
            ("wildcard", "127.0.0.1", {"no_proxy": "*"}, True),
            ("invalid CIDR", "127.0.0.1", {"no_proxy": "127.0.0.0/99"}, False),
            ("invalid then valid", "127.0.0.1", {
                "no_proxy": "not-a-network/8,127.0.0.0/8"}, True),
            ("lowercase wins", "127.0.0.1", {
                "NO_PROXY": "127.0.0.0/8", "no_proxy": "10.0.70.0/24"}, False),
            ("empty lowercase wins", "127.0.0.1", {
                "NO_PROXY": "127.0.0.0/8", "no_proxy": ""}, False),
        ]
        try:
            for name, host, bypass, direct in cases:
                with self.subTest(name=name), tempfile.TemporaryDirectory() as tmp:
                    receiver.posts.clear()
                    proxy.posts.clear()
                    env = {k: v for k, v in os.environ.items()
                           if not k.lower().endswith("_proxy")}
                    env.pop("REQUEST_METHOD", None)
                    env.update({
                        "http_proxy": proxy_url,
                        "AGENTBEACON_URL": f"http://{host}:{receiver.server_port}",
                        "AGENTBEACON_TOKEN": "proxy-test-token",
                        "AGENTBEACON_CONFIG": os.path.join(tmp, "missing.json"),
                        "TMPDIR": tmp,
                    })
                    env.update(bypass)
                    payload = json.dumps({
                        "session_id": "proxy-routing-test",
                        "hook_event_name": "PreToolUse",
                    })
                    result = subprocess.run(
                        [sys.executable, self.hook], input=payload, text=True,
                        capture_output=True, env=env, timeout=10)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(result.stderr, "")
                    self.assertEqual(len(receiver.posts), int(direct))
                    self.assertEqual(len(proxy.posts), int(not direct))
                    post = (receiver if direct else proxy).posts[0]
                    self.assertEqual(post["auth"], "Bearer proxy-test-token")
                    self.assertEqual(post["body"]["status"], "running")
        finally:
            for server, worker in servers:
                server.shutdown()
                worker.join()
                server.server_close()

    def test_cidr_matching_for_ipv4_ipv6_and_hostnames(self):
        cases = [
            ("http://172.16.168.122:8765", "172.16.0.0/16", True),
            ("http://10.0.70.12:8765", "10.0.70.0/24", True),
            ("http://10.0.71.12:8765", "10.0.70.0/24", False),
            ("https://[fd00::123]:8765", "fd00::/8", True),
            ("http://[fe80::123]:8765", "fd00::/8", False),
            ("http://[fd00::123]:8765", "172.16.0.0/16", False),
            ("http://172.16.168.122:8765", "fd00::/8", False),
            ("http://172.16.168.122:8765", "172.16.168.122/16", True),
            ("http://receiver.example:8765", "172.16.0.0/16", False),
        ]
        for url, bypass, direct in cases:
            with self.subTest(url=url, bypass=bypass):
                handler = self.adapter.CidrProxyHandler({"no": bypass})
                request = urllib.request.Request(url)
                with patch.object(urllib.request.ProxyHandler, "proxy_open") as fallback:
                    handler.proxy_open(request, "http://127.0.0.1:2080", request.type)
                    self.assertEqual(fallback.call_count, int(not direct))
