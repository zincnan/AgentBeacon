#!/usr/bin/env python3
"""agent-notify: forward an AgentBeacon status event to a Receiver.

Single-shot HTTP POST; no retry. See docs/protocol.md for the wire contract.
"""

import json
import os
import sys
import urllib.error
import urllib.request

PROTOCOL_VERSION = "v1"
ENDPOINT_PATH = "/api/v1/status"

ALLOWED_STATUS = ("running", "approval", "completed", "failed")
MAX_MESSAGE_CHARS = 512
MAX_SESSION_ID_CHARS = 256
MAX_BODY_BYTES = 8 * 1024
HTTP_TIMEOUT_SECONDS = 10

EXIT_OK = 0
EXIT_LOCAL_ERROR = 4
EXIT_CLIENT_ERROR = 2
EXIT_TRANSIENT_ERROR = 3


def die(code, msg):
    print(f"agent-notify: {msg}", file=sys.stderr)
    sys.exit(code)


def parse_args(argv):
    import argparse

    parser = argparse.ArgumentParser(
        prog="agent-notify",
        description=(
            "Forward an AgentBeacon status event to a Receiver. "
            "Single POST; no retry. See docs/protocol.md."
        ),
    )
    parser.add_argument("--session-id", required=True,
                        help="Session id (max 256 chars; identity field, not truncated)")
    parser.add_argument("--agent", required=True,
                        help="Agent type identifier (free string)")
    parser.add_argument("--status", required=True, choices=ALLOWED_STATUS,
                        help="One of: running, approval, completed, failed")
    parser.add_argument("--message", default=None,
                        help=f"Optional human-readable context (truncated to {MAX_MESSAGE_CHARS} chars)")
    parser.add_argument("--host", default=None,
                        help="Optional display host identifier; not used for routing")
    parser.add_argument("--url", default=None,
                        help="Override AGENTBEACON_URL, e.g. http://127.0.0.1:8765")
    parser.add_argument("--token", default=None,
                        help="Override AGENTBEACON_TOKEN. Dev-only: visible in shell history and /proc.")
    parser.add_argument("--quiet", action="store_true",
                        help="Suppress per-call info lines on stderr")

    # Treat argparse errors (bad --status, missing required arg, etc.) as local errors.
    parser.error = lambda message: die(EXIT_LOCAL_ERROR, message)
    return parser.parse_args(argv)


def resolve_url(args):
    url = args.url or os.environ.get("AGENTBEACON_URL")
    if not url:
        die(EXIT_LOCAL_ERROR,
            "missing receiver URL: set AGENTBEACON_URL env var or pass --url")
    return url.rstrip("/") + ENDPOINT_PATH


def resolve_token(args):
    """Token is optional: omit it when the Receiver runs in --no-auth
    mode (no Authorization header is sent). Against a token-mode
    Receiver the request will 401 — that is the operator's choice."""
    return args.token or os.environ.get("AGENTBEACON_TOKEN") or None


def validate_and_build_payload(args):
    session_id = args.session_id
    if len(session_id) == 0:
        die(EXIT_LOCAL_ERROR, "session_id must not be empty")
    if len(session_id) > MAX_SESSION_ID_CHARS:
        die(EXIT_LOCAL_ERROR,
            f"session_id exceeds {MAX_SESSION_ID_CHARS} chars; "
            "identity fields are not truncated, refusing to send")

    agent = args.agent
    if len(agent) == 0:
        die(EXIT_LOCAL_ERROR, "agent must not be empty")

    message = args.message
    if message is not None and len(message) > MAX_MESSAGE_CHARS:
        message = message[:MAX_MESSAGE_CHARS]

    payload = {
        "session_id": session_id,
        "agent": agent,
        "status": args.status,
    }
    if message is not None:
        payload["message"] = message
    if args.host is not None:
        payload["host"] = args.host
    return payload


def post(url, token, payload):
    body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(body) > MAX_BODY_BYTES:
        die(EXIT_LOCAL_ERROR,
            f"encoded body is {len(body)}B, exceeds {MAX_BODY_BYTES}B; refusing to send")

    headers = {
        "Content-Type": "application/json",
        "User-Agent": f"agent-notify/{PROTOCOL_VERSION}",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, data=body, method="POST", headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT_SECONDS) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


def main(argv):
    args = parse_args(argv)
    url = resolve_url(args)
    token = resolve_token(args)
    payload = validate_and_build_payload(args)

    if not args.quiet:
        print(
            f"-> POST {url} status={payload['status']} "
            f"session_id={payload['session_id']} agent={payload['agent']}",
            file=sys.stderr,
        )

    try:
        status_code, body = post(url, token, payload)
    except urllib.error.URLError as e:
        die(EXIT_TRANSIENT_ERROR, f"transport error: {e.reason}")
    except (TimeoutError, OSError) as e:
        die(EXIT_TRANSIENT_ERROR, f"transport error: {e}")

    if status_code == 200:
        if not args.quiet:
            print("<- 200 OK", file=sys.stderr)
        return EXIT_OK
    if 400 <= status_code < 500:
        snippet = body[:200].decode("utf-8", errors="replace")
        die(EXIT_CLIENT_ERROR, f"receiver rejected ({status_code}): {snippet}")
    if 500 <= status_code < 600:
        snippet = body[:200].decode("utf-8", errors="replace")
        die(EXIT_TRANSIENT_ERROR, f"receiver error ({status_code}): {snippet}")
    die(EXIT_TRANSIENT_ERROR, f"unexpected status {status_code}")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))