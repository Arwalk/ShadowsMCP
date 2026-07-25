#!/usr/bin/env bash
# Smoke test for the save-analysis engine, run through the savecli against the
# checked-in fixture (src/SaveCli/fixtures/mini-save.sv). The fixture exercises every
# FullSerializer reserved-key case: $id defined after its first $ref, $type+$content
# wrapping, $version, a reference cycle, a dangling $ref, and >128-deep nesting.
# Usage: tools/save-smoke.sh
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FIXDIR="$ROOT/src/SaveCli/fixtures"
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

say "== building savecli =="
dotnet build "$ROOT/src/SaveCli" -c Release -v q >/dev/null || { say "build failed"; exit 1; }
CLI() { dotnet "$ROOT/src/SaveCli/bin/Release/net10.0/savecli.dll" "$@"; }

say "== savecli smoke =="

OUT=$(CLI list --dir "$FIXDIR")
assert_contains "list shows fixture" "$OUT" "mini-save.sv"

# summary: deep-nesting parse succeeded, top fields, $content-wrapped list counted
OUT=$(CLI summary mini-save.sv --dir "$FIXDIR")
assert_contains "summary turn" "$OUT" '"turn": 42'
assert_contains "summary god" "$OUT" '"god": "God_Snake"'
assert_contains "summary victory mode label" "$OUT" '"victoryModeName": "dark empire"'
assert_contains "summary agent name via personID" "$OUT" '"name": "Zelda"'
assert_contains "summary \$content-wrapped cultures" "$OUT" '"cultures": 1'

# inspect: $ref resolution ($id appears after the $ref textually)
OUT=$(CLI inspect mini-save 'locations[0].soc.name' --dir "$FIXDIR")
assert_contains "inspect follows \$ref" "$OUT" "Kingdom of Wolfden"

# inspect: reference cycle collapses to a marker instead of recursing
OUT=$(CLI inspect mini-save 'socialGroups[0]' --depth 5 --dir "$FIXDIR")
assert_contains "inspect marks cycles" "$OUT" "<cycle: \$id=10 Society>"

# inspect: dangling $ref is reported, not fatal
OUT=$(CLI inspect mini-save 'units[1]' --depth 2 --dir "$FIXDIR")
assert_contains "inspect marks dangling \$ref" "$OUT" "<unresolved \$ref:999>"

# raw: refs stay unresolved
OUT=$(CLI raw mini-save 'locations[1]' --dir "$FIXDIR")
assert_contains "raw keeps \$ref verbatim" "$OUT" '"$ref": "11"'

# errors are prescriptive and exit 1
OUT=$(CLI inspect mini-save 'locations[9]' --dir "$FIXDIR" 2>&1); RC=$?
assert_contains "bad index message" "$OUT" "index 9 out of range"
[ "$RC" = 1 ] && ok "bad index exit code" || bad "bad index exit code" "$RC"

OUT=$(CLI summary "$ROOT/game/data/scenarioDarkEmpire.mapsv" 2>&1); RC=$?
assert_contains ".mapsv rejected with hint" "$OUT" "scenario scripts, not saves"
[ "$RC" = 1 ] && ok ".mapsv exit code" || bad ".mapsv exit code" "$RC"

say "== $PASS passed, $FAIL failed =="
[ "$FAIL" = 0 ]
