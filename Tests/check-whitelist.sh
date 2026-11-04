#!/usr/bin/env bash
# Compiles packed mod folders with Space Engineers' own script compiler and whitelist (the game's check, offline).
# Usage: check-whitelist.sh [mod folder...]   (defaults to the deployed toolkit and LCD mod)
set -euo pipefail
cd "$(dirname "$0")"
MODS="${SE_MODS:-$HOME/.config/SpaceEngineers/Mods}"
if [ $# -eq 0 ]; then set -- "$MODS/AcruxUiToolkit" "$MODS/Arthur-s-Lcd-Mod"; fi
dotnet build WhitelistCheck/WhitelistCheck.csproj -v q -nologo | grep -E " error " || true
dotnet run --project WhitelistCheck/WhitelistCheck.csproj --no-build -- "$@"
