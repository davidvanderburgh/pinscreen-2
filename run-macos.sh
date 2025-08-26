#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR=$(cd "$(dirname "$0")" && pwd)
APP_DIR="$ROOT_DIR/Pinscreen2.App"

# Default VLC paths; can be overridden by LibVlcPath in config.json
VLC_LIB="/Applications/VLC.app/Contents/MacOS/lib"
VLC_PLUGINS="/Applications/VLC.app/Contents/MacOS/plugins"

CFG_LIB=$(jq -r '.LibVlcPath // empty' "$APP_DIR/config.json" 2>/dev/null || true)
if [[ -n "${CFG_LIB:-}" ]]; then
  VLC_LIB="$CFG_LIB"
  # On macOS plugins live one level up from lib
  VLC_PLUGINS="$(cd "$VLC_LIB/.." && pwd)/plugins"
fi

export DYLD_LIBRARY_PATH="$VLC_LIB${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
export VLC_PLUGIN_PATH="$VLC_PLUGINS"

exec dotnet run --project "$APP_DIR" "$@"


