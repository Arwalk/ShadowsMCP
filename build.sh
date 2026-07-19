#!/usr/bin/env bash
# Build the mod and assemble two payloads under dist/:
#   dist/ShadowsMCP/        flat local install  -> <game>/data/optionalData/ShadowsMCP/
#   dist/upload/ShadowsMCP/ Steam Workshop item -> drop into the game's modUploadFolder/
# Usage: ./build.sh [--skip-tests]
set -eu

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

if [ ! -f lib/Managed/Assembly-CSharp.dll ]; then
  echo "error: lib/Managed/Assembly-CSharp.dll not found." >&2
  echo "Copy the game's ShadowsOfForbiddenGods_Data/Managed/ folder to lib/Managed/ first." >&2
  exit 1
fi

# Release version is the single source of truth: <Version> in the .csproj.
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Mod/ShadowsMCP.csproj | head -1)"
[ -n "$VERSION" ] || { echo "error: could not read <Version> from src/Mod/ShadowsMCP.csproj" >&2; exit 1; }
echo "== ShadowsMCP v$VERSION =="

if [ "${1:-}" != "--skip-tests" ]; then
  echo "== protocol smoke test =="
  tools/smoke-test.sh >/tmp/shadowsmcp-smoke.log 2>&1 \
    || { echo "smoke test FAILED:"; tail -30 /tmp/shadowsmcp-smoke.log; exit 1; }
  tail -1 /tmp/shadowsmcp-smoke.log
fi

# Build both configs (Release ships; Debug is kept so a readable-symbols DLL is always available).
echo "== building mod (Debug + Release) =="
dotnet build src/Mod -c Debug   -v q
dotnet build src/Mod -c Release -v q

# ---- payload 1: flat local install ------------------------------------------
echo "== packaging dist/ShadowsMCP (local install) =="
rm -rf dist/ShadowsMCP
mkdir -p dist/ShadowsMCP
cp src/Mod/bin/Release/ShadowsMCP.dll dist/ShadowsMCP/
cp mod/mod_desc.json   dist/ShadowsMCP/
cp mod/mod_config.json dist/ShadowsMCP/

# ---- payload 2: Steam Workshop upload folder --------------------------------
# Layout the game's uploader expects (see SteamManager.cs / PrefabStore.cs):
#   ShadowsMCP/mod.json        Workshop listing metadata (title/description/tags)
#   ShadowsMCP/preview.png     Workshop thumbnail (optional)
#   ShadowsMCP/content/        the item content that gets uploaded (== local install)
echo "== packaging dist/upload/ShadowsMCP (workshop) =="
UP=dist/upload/ShadowsMCP
rm -rf dist/upload
mkdir -p "$UP/content"
cp -r dist/ShadowsMCP/. "$UP/content/"
[ -f mod/preview.png ] && cp mod/preview.png "$UP/preview.png" || echo "  (no mod/preview.png — workshop item will have no thumbnail)"

# Stamp the build version into the Workshop description so the page always reflects what shipped.
python3 - "$VERSION" <<'PY'
import json, sys
version = sys.argv[1]
with open("mod/mod.json", encoding="utf-8") as f:
    data = json.load(f)
data["description"] = data["description"].rstrip() + f"\n\nBuild {version}"
with open("dist/upload/ShadowsMCP/mod.json", "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")
PY

echo ""
echo "Done (v$VERSION)."
echo "Local install : copy dist/ShadowsMCP/ -> <game>\\data\\optionalData\\ShadowsMCP\\"
echo "Workshop upload: copy dist/upload/ShadowsMCP/ -> <game>\\modUploadFolder\\ShadowsMCP\\"
echo "                 then in-game: Workshop menu -> User Mods -> publish."
echo ""
echo "dist/ShadowsMCP:"; ls -la dist/ShadowsMCP/
echo "dist/upload/ShadowsMCP:"; ls -laR dist/upload/ShadowsMCP/
