#!/usr/bin/env bash
# Decompile the game's Assembly-CSharp.dll into decompiled/ (one .cs file per type).
# Requires lib/Managed/ (copied from the game) and the ilspycmd dotnet tool.
set -eu

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DLL="$ROOT/lib/Managed/Assembly-CSharp.dll"

if [ ! -f "$DLL" ]; then
  echo "error: $DLL not found." >&2
  echo "Copy the game's ShadowsOfForbiddenGods_Data/Managed/ folder to lib/Managed/ first." >&2
  exit 1
fi

export PATH="$PATH:$HOME/.dotnet/tools"
export DOTNET_ROLL_FORWARD=LatestMajor

rm -rf "$ROOT/decompiled"
mkdir -p "$ROOT/decompiled"
ilspycmd -p -o "$ROOT/decompiled" --nested-directories "$DLL"
echo "decompiled to $ROOT/decompiled ($(find "$ROOT/decompiled" -name '*.cs' | wc -l) files)"
