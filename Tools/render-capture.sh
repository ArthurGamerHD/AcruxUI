#!/usr/bin/env bash
# Dev only: renders the files "/acrux capture" and "/acrux capture-all" save (dev builds of the toolkit, in
# SpaceEngineers/Storage/<toolkit>/) to PNGs next to them, with the preview host's Debug build.
#
# usage: Tools/render-capture.sh [--mod <mod Content folder>] [--scale <factor>] <capture.acruxcap>...
#   --mod    also loads the sprites a mod defines in its .sbc files (otherwise they are reported missing), e.g.
#            ~/RiderProjects/Arthur-s-LCD-Mod/Arthur-s-Lcd-Mod/Content
#   --scale  output pixels per surface pixel (default 1)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOST="$ROOT/Tools/AcruxPreviewHost/bin/Debug/net9.0/AcruxPreviewHost"

usage() {
  sed -n '2,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
  exit 1
}

OPTIONS=()
FILES=()
while [ $# -gt 0 ]; do
  case "$1" in
    --mod|--scale)
      [ $# -ge 2 ] || usage
      OPTIONS+=("$1" "$2")
      shift 2 ;;
    -h|--help) usage ;;
    *) FILES+=("$1"); shift ;;
  esac
done
[ ${#FILES[@]} -gt 0 ] || usage

# --render-capture exists only in Debug builds of the host.
if [ ! -x "$HOST" ]; then
  echo "Building the preview host (Debug)..."
  dotnet build "$ROOT/Tools/AcruxPreviewHost/AcruxPreviewHost.csproj" -c Debug -nologo -v q > /dev/null
fi

failed=0
for file in "${FILES[@]}"; do
  if [ ! -f "$file" ]; then
    echo "render-capture: no such file: $file" >&2
    failed=1
    continue
  fi
  out="${file%.acruxcap}.png"
  if ! "$HOST" --render-capture "$file" "$out" "${OPTIONS[@]}" | grep -E "^(rendered|missing texture)"; then
    echo "render-capture: failed: $file" >&2
    failed=1
  fi
done
exit $failed
