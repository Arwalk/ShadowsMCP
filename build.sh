#!/usr/bin/env bash
# Build the mod and assemble the installable mod folder in dist/ShadowsMCP/.
# Usage: ./build.sh [--skip-tests]
set -eu

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

if [ ! -f lib/Managed/Assembly-CSharp.dll ]; then
  echo "error: lib/Managed/Assembly-CSharp.dll not found." >&2
  echo "Copy the game's ShadowsOfForbiddenGods_Data/Managed/ folder to lib/Managed/ first." >&2
  exit 1
fi

if [ "${1:-}" != "--skip-tests" ]; then
  echo "== protocol smoke test =="
  tools/smoke-test.sh >/tmp/shadowsmcp-smoke.log 2>&1 \
    || { echo "smoke test FAILED:"; tail -30 /tmp/shadowsmcp-smoke.log; exit 1; }
  tail -1 /tmp/shadowsmcp-smoke.log
fi

echo "== building mod =="
dotnet build src/Mod -c Release -v q

echo "== packaging dist/ShadowsMCP =="
rm -rf dist/ShadowsMCP
mkdir -p dist/ShadowsMCP
cp src/Mod/bin/Release/ShadowsMCP.dll dist/ShadowsMCP/
cp mod/mod_desc.json dist/ShadowsMCP/
[ -f mod/params.txt ] && cp mod/params.txt dist/ShadowsMCP/
[ -f mod/preview.png ] && cp mod/preview.png dist/ShadowsMCP/

echo ""
echo "Done. Install by copying dist/ShadowsMCP/ to:"
echo "  <game>\\data\\optionalData\\ShadowsMCP\\"
echo "(e.g. C:\\Program Files (x86)\\Steam\\steamapps\\common\\Shadows of Forbidden Gods\\data\\optionalData\\ShadowsMCP\\)"
ls -la dist/ShadowsMCP/
