#!/usr/bin/env bash
# Claude Code Stop hook: keep the deployable (dist/) in sync while C# source is uncommitted.
#
# Runs after every turn, but only rebuilds when there are uncommitted .cs/.csproj changes
# under src/ — so dist/ is never stale and nobody has to remember to run build.sh. Uses
# `build.sh --skip-tests` (builds Debug + Release, stages dist/ShadowsMCP + Workshop payload).
# Non-blocking by design: always exits 0 so it can never wedge a turn; a failed build just
# prints a note and leaves the log at /tmp/shadowsmcp-autobuild.log.
#
# Wired from .claude/settings.json (hooks.Stop). Review or disable it via the /hooks menu.
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 0

# Nothing to do on doc-only / chat-only turns (no C# changes staged in the working tree).
git status --porcelain -- src 2>/dev/null | grep -qE '\.(cs|csproj)$' || exit 0

if ./build.sh --skip-tests >/tmp/shadowsmcp-autobuild.log 2>&1; then
  echo "auto-build: dist refreshed"
else
  echo "auto-build FAILED — see /tmp/shadowsmcp-autobuild.log" >&2
fi
exit 0
