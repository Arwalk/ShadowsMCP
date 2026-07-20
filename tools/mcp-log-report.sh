#!/usr/bin/env bash
# Summarise an mcp-tee capture: response bytes per tool, ranked (approx tokens = bytes / 4).
# Feeds the token-efficiency analysis with real per-tool costs instead of estimates.
# Usage: tools/mcp-log-report.sh [logfile]   (default mcp-log.jsonl)
set -eu

LOG="${1:-mcp-log.jsonl}"
[ -f "$LOG" ] || { echo "no log file: $LOG" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "this report needs 'jq' (https://jqlang.github.io/jq/)" >&2; exit 1; }

echo "== per-tool response cost in $LOG (approx tokens = bytes / 4) =="

# One (tool, resBytes) row per successful result (tools/call, and one-time initialize/tools/list).
jq -r 'select(.response.result) | "\(.request.params.name // .request.method) \(.resBytes)"' "$LOG" \
 | awk '{ n[$1]++; b[$1]+=$2; if ($2 > mx[$1]) mx[$1] = $2 }
        END { for (k in b) printf "%d\t%s\t%d\t%d\t%d\n", b[k], k, n[k], b[k]/n[k], mx[k] }' \
 | sort -nr \
 | awk -F'\t' 'BEGIN { printf "%-24s %8s %11s %9s %9s\n", "tool", "calls", "totBytes", "avgBytes", "maxBytes" }
              { printf "%-24s %8d %11d %9d %9d\n", $2, $3, $1, $4, $5 }'

echo
jq -s 'map(select(.response.result) | .resBytes)
       | { results: length, totalBytes: (add // 0), approxTokens: ((add // 0) / 4 | floor) }' "$LOG"
