#!/usr/bin/env bash
# Manual smoke test against a running Receiver.
#
# Usage:
#   AGENTBEACON_URL=http://127.0.0.1:8765 \
#   AGENTBEACON_TOKEN=dev-token-placeholder \
#   bash examples/smoke_curl.sh
#
# Verifies the four states plus a last-received-wins overwrite chain.

set -euo pipefail

URL="${AGENTBEACON_URL:?AGENTBEACON_URL is required}"
TOKEN="${AGENTBEACON_TOKEN:?AGENTBEACON_TOKEN is required}"
SID="${SESSION_ID:-smoke-$(date +%s)}"

post() {
    local status="$1" message="${2:-}"
    local body
    if [[ -n "$message" ]]; then
        body=$(printf '{"session_id":"%s","agent":"smoke","status":"%s","message":"%s"}' \
            "$SID" "$status" "$message")
    else
        body=$(printf '{"session_id":"%s","agent":"smoke","status":"%s"}' \
            "$SID" "$status")
    fi
    curl -sS -o /tmp/smoke_body.txt -w "%{http_code}\n" \
        -X POST -H "Content-Type: application/json" \
        -H "Authorization: Bearer $TOKEN" \
        --data "$body" \
        "$URL/api/v1/status"
    echo "  body: $(cat /tmp/smoke_body.txt)"
}

echo "session_id: $SID"
echo "url:        $URL"

echo "--- running ---"
post running
echo "--- approval ---"
post approval "needs user consent"
echo "--- running (overwrite) ---"
post running
echo "--- completed ---"
post completed

echo
echo "OK"