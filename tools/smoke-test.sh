#!/usr/bin/env bash
# Smoke test for the MCP protocol layer, run against the Linux TestHost.
# Usage: tools/smoke-test.sh [port]   (default 8917)
set -u

PORT="${1:-8917}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
URL="http://127.0.0.1:$PORT/mcp"
PASS=0
FAIL=0

say()  { printf '%s\n' "$*"; }
ok()   { PASS=$((PASS+1)); say "  ok: $1"; }
bad()  { FAIL=$((FAIL+1)); say "  FAIL: $1"; say "       got: $2"; }

# assert_contains <label> <haystack> <needle>
assert_contains() {
  case "$2" in
    *"$3"*) ok "$1" ;;
    *) bad "$1 (expected to contain: $3)" "$2" ;;
  esac
}

# post <json> -> body (and sets STATUS)
post() {
  RESP=$(curl -s -w '\n%{http_code}' -X POST "$URL" \
    -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
    --data "$1")
  STATUS=$(printf '%s' "$RESP" | tail -n1)
  BODY=$(printf '%s' "$RESP" | sed '$d')
}

say "== building TestHost =="
dotnet build "$ROOT/src/TestHost" -c Release -v q >/dev/null || { say "build failed"; exit 1; }
DLL="$ROOT/src/TestHost/bin/Release/net10.0/ShadowsMcp.TestHost.dll"

say "== starting TestHost on port $PORT =="
dotnet "$DLL" "$PORT" > /tmp/shadowsmcp-testhost.log 2>&1 &
HOST_PID=$!
trap 'kill $HOST_PID 2>/dev/null; kill ${HOST2_PID:-0} 2>/dev/null' EXIT
for i in $(seq 1 50); do
  grep -q READY /tmp/shadowsmcp-testhost.log 2>/dev/null && break
  sleep 0.1
done
grep -q READY /tmp/shadowsmcp-testhost.log || { say "TestHost did not start"; cat /tmp/shadowsmcp-testhost.log; exit 1; }

say "== protocol tests =="

post '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}'
assert_contains "initialize echoes supported version" "$BODY" '"protocolVersion":"2025-06-18"'
assert_contains "initialize advertises tools capability" "$BODY" '"tools":{}'
assert_contains "initialize serverInfo" "$BODY" '"name":"shadows-mcp-testhost"'

post '{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}'
# Spec: when the client's requested version is unsupported, answer with the LATEST supported one.
assert_contains "initialize falls back on unsupported version" "$BODY" '"protocolVersion":"2025-06-18"'

post '{"jsonrpc":"2.0","method":"notifications/initialized"}'
[ "$STATUS" = "202" ] && ok "notification returns 202" || bad "notification returns 202" "$STATUS"
[ -z "$BODY" ] && ok "notification body empty" || bad "notification body empty" "$BODY"

post '{"jsonrpc":"2.0","id":3,"method":"ping"}'
assert_contains "ping" "$BODY" '"result":{}'

post '{"jsonrpc":"2.0","id":4,"method":"tools/list"}'
assert_contains "tools/list has echo" "$BODY" '"name":"echo"'
assert_contains "tools/list has schema" "$BODY" '"inputSchema":{"type":"object"'
assert_contains "tools/list marks required" "$BODY" '"required":["text"]'

post '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}'
assert_contains "tools/call echo content" "$BODY" '"text":"echo: hi"'
assert_contains "tools/call echo isError false" "$BODY" '"isError":false'

post '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"fail_tool","arguments":{}}}'
assert_contains "tools/call failure sets isError" "$BODY" '"isError":true'

post '{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"echo","arguments":{}}}'
assert_contains "missing required param -> isError" "$BODY" '"isError":true'
assert_contains "missing required param named" "$BODY" 'missing required parameter(s): text'

post '{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi","txt":"x"}}}'
assert_contains "unknown param -> isError" "$BODY" '"isError":true'
assert_contains "unknown param named" "$BODY" "unknown parameter 'txt'"
assert_contains "unknown param lists valid params" "$BODY" 'Valid parameters: text (required)'

post '{"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"fake_overview","arguments":{"foo":1}}}'
assert_contains "no-param tool rejects args" "$BODY" "'fake_overview' takes no parameters (got 'foo')"

post '{"jsonrpc":"2.0","id":23,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi","_meta":{"x":1}}}}'
assert_contains "underscore metadata keys tolerated" "$BODY" '"text":"echo: hi"'

post '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"no_such_tool"}}'
assert_contains "unknown tool -> -32602" "$BODY" '"code":-32602'

post '{"jsonrpc":"2.0","id":8,"method":"bogus/method"}'
assert_contains "unknown method -> -32601" "$BODY" '"code":-32601'

post 'this is not json'
[ "$STATUS" = "400" ] && ok "parse error -> 400" || bad "parse error -> 400" "$STATUS"
assert_contains "parse error -> -32700" "$BODY" '"code":-32700'

post '[{"jsonrpc":"2.0","id":9,"method":"ping"}]'
assert_contains "batch -> -32600" "$BODY" '"code":-32600'

GETSTATUS=$(curl -s -o /dev/null -w '%{http_code}' "$URL")
[ "$GETSTATUS" = "405" ] && ok "GET -> 405" || bad "GET -> 405" "$GETSTATUS"

PATHSTATUS=$(curl -s -o /dev/null -w '%{http_code}' -X POST "http://127.0.0.1:$PORT/other" --data '{}')
[ "$PATHSTATUS" = "404" ] && ok "wrong path -> 404" || bad "wrong path -> 404" "$PATHSTATUS"

say "== port-conflict retry =="
dotnet "$DLL" "$PORT" > /tmp/shadowsmcp-testhost2.log 2>&1 &
HOST2_PID=$!
for i in $(seq 1 50); do
  grep -q READY /tmp/shadowsmcp-testhost2.log 2>/dev/null && break
  sleep 0.1
done
NEXT=$((PORT+1))
if grep -q "READY on http://127.0.0.1:$NEXT/mcp" /tmp/shadowsmcp-testhost2.log; then
  ok "second instance retried onto port $NEXT"
else
  bad "second instance retried onto port $NEXT" "$(cat /tmp/shadowsmcp-testhost2.log)"
fi
kill $HOST2_PID 2>/dev/null

say "== concurrency: ping while slow_tool runs =="
curl -s -X POST "$URL" -H 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"slow_tool","arguments":{"ms":2000}}}' >/dev/null &
SLOW_PID=$!
sleep 0.3
START_NS=$(date +%s%N)
post '{"jsonrpc":"2.0","id":11,"method":"ping"}'
ELAPSED_MS=$(( ($(date +%s%N) - START_NS) / 1000000 ))
if [ "$ELAPSED_MS" -lt 1000 ] && printf '%s' "$BODY" | grep -q '"result":{}'; then
  ok "ping answered in ${ELAPSED_MS}ms while slow_tool was running"
else
  bad "ping during slow_tool (took ${ELAPSED_MS}ms)" "$BODY"
fi
wait $SLOW_PID 2>/dev/null

say ""
say "== $PASS passed, $FAIL failed =="
[ "$FAIL" = "0" ]
