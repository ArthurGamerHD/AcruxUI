#!/usr/bin/env bash
# Dev only: builds the Acrux UI toolkit mod and a -dev SDK package, installs that package in the client mods and
# builds them. Every build deploys through the MDK packager (mdk.local.ini) into the game's Mods folder.
#
#   1. AcruxUiToolkit   builds and deploys the toolkit mod; also refreshes ClientApi/Generated
#   2. Acrux.Sdk        packs and signs Acrux.Sdk.<version>-dev.<timestamp> (embeds ClientApi, so it runs after 1)
#   3. client mods      in parallel: update-acrux.sh <dev version> from Tools/Acrux.Sdk/out, then build + deploy
#
# usage: Tools/dev-deploy.sh                 everything
#        Tools/dev-deploy.sh minimap lcd     toolkit + SDK, then only these clients
#        SKIP_TOOLKIT=1 Tools/dev-deploy.sh  reuse the toolkit mod and newest SDK package already built
# CONFIGURATION defaults to Debug.
#
# Clients: the optional, untracked Tools/deploy.local.ini replaces the built-in list (minimap, lcd, highway, the
# checkouts next to this repository). One "name = project" line per client, in build order:
#
#   [projects]
#   minimap = ../../Minimap/Minimap/Minimap.csproj      ; relative to the ini, absolute, or ~/...
#   lcd     = ~/RiderProjects/Arthur-s-LCD-Mod/Arthur-s-Lcd-Mod/Arthur-s-Lcd-Mod.csproj
#
# The SDK is installed with the update-acrux.sh found in the project's folder or the nearest folder above it; a
# project without one is only built.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PARENT="$(dirname "$ROOT")"
CONFIGURATION="${CONFIGURATION:-Debug}"
SDK_OUT="$ROOT/Tools/Acrux.Sdk/out"
CONFIG="$ROOT/Tools/deploy.local.ini"

NAMES=()
declare -A PROJECTS=()
add_client() {
  [ -z "${PROJECTS[$1]:-}" ] || { echo "dev-deploy: client '$1' is listed twice" >&2; exit 1; }
  NAMES+=("$1")
  PROJECTS[$1]="$2"
}

if [ -f "$CONFIG" ]; then
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%%[;#]*}"
    line="$(echo "$line" | sed -E 's/^[[:space:]]+|[[:space:]]+$//g')"
    case "$line" in ''|\[*\]) continue ;; esac
    [[ "$line" == *=* ]] || { echo "dev-deploy: $CONFIG: expected 'name = project', got '$line'" >&2; exit 1; }
    name="$(echo "${line%%=*}" | sed -E 's/[[:space:]]+$//')"
    path="$(echo "${line#*=}" | sed -E 's/^[[:space:]]+//')"
    case "$path" in
      "~"/*) path="$HOME/${path#"~/"}" ;;
      /*) ;;
      *) path="$(dirname "$CONFIG")/$path" ;;
    esac
    add_client "$name" "$path"
  done < "$CONFIG"
  [ ${#NAMES[@]} -gt 0 ] || { echo "dev-deploy: $CONFIG lists no projects" >&2; exit 1; }
else
  add_client minimap "$PARENT/Minimap/Minimap/Minimap.csproj"
  add_client lcd "$PARENT/Arthur-s-LCD-Mod/Arthur-s-Lcd-Mod/Arthur-s-Lcd-Mod.csproj"
  add_client highway "$PARENT/RoadCrafter/HighwayEngineer/HighwayEngineer.csproj"
fi

CLIENTS=("$@")
[ ${#CLIENTS[@]} -gt 0 ] || CLIENTS=("${NAMES[@]}")
for client in "${CLIENTS[@]}"; do
  [ -n "${PROJECTS[$client]:-}" ] || { echo "dev-deploy: unknown client '$client' (${NAMES[*]})" >&2; exit 1; }
  [ -f "${PROJECTS[$client]}" ] || { echo "dev-deploy: $client: no project at ${PROJECTS[$client]}" >&2; exit 1; }
done

LOGS="$(mktemp -d "${TMPDIR:-/tmp}/acrux-dev-deploy.XXXXXX")"

# Runs a build step with its output in $LOGS/<name>.log; prints the tail of the log when it fails.
step() {
  local name="$1"; shift
  local started=$SECONDS
  if "$@" > "$LOGS/$name.log" 2>&1; then
    echo "  ok    $name ($((SECONDS - started))s)"
  else
    echo "  FAIL  $name ($((SECONDS - started))s), log: $LOGS/$name.log"
    grep -E ': error |error:|FAILED' "$LOGS/$name.log" | sort -u | head -20 | sed 's/^/        /'
    return 1
  fi
}

if [ -z "${SKIP_TOOLKIT:-}" ]; then
  echo "Toolkit:"
  step toolkit dotnet build "$ROOT/AcruxUiToolkit/AcruxUiToolkit.csproj" -c "$CONFIGURATION" -nologo
  step sdk dotnet build "$ROOT/Tools/Acrux.Sdk/Acrux.Sdk.csproj" -c "$CONFIGURATION" -nologo
fi

PACKAGE="$(ls -t "$SDK_OUT"/Acrux.Sdk.*-dev.*.nupkg 2> /dev/null | head -1)"
[ -n "$PACKAGE" ] || { echo "dev-deploy: no -dev SDK package in $SDK_OUT" >&2; exit 1; }
[ -f "$PACKAGE.sig" ] || { echo "dev-deploy: $PACKAGE is unsigned (no signing key); update-acrux would refuse it" >&2; exit 1; }
VERSION="$(basename "$PACKAGE" .nupkg)"
VERSION="${VERSION#Acrux.Sdk.}"
echo "Clients (Acrux.Sdk $VERSION, in parallel):"

deploy_client() {
  local project="${PROJECTS[$1]}"
  local dir
  dir="$(cd "$(dirname "$project")" && pwd)"
  while [ "$dir" != "/" ] && [ ! -x "$dir/update-acrux.sh" ]; do
    dir="$(dirname "$dir")"
  done
  if [ -x "$dir/update-acrux.sh" ]; then
    (cd "$dir" && ACRUX_SDK_URL="file://$SDK_OUT" ./update-acrux.sh "$VERSION") || return 1
  else
    echo "dev-deploy: no update-acrux.sh above $project; building without an SDK update"
  fi
  dotnet build "$project" -c "$CONFIGURATION" -nologo
}

declare -A PIDS=()
for client in "${CLIENTS[@]}"; do
  step "$client" deploy_client "$client" &
  PIDS[$client]=$!
done
failed=0
for client in "${CLIENTS[@]}"; do
  wait "${PIDS[$client]}" || failed=1
done

if [ $failed -ne 0 ]; then
  echo "dev-deploy: some builds failed; logs in $LOGS" >&2
  exit 1
fi
rm -rf "$LOGS"
echo "Deployed. Restart the world (or the game) to load the new mods."
