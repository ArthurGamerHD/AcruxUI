#!/usr/bin/env bash
# Regenerates ClientApi from the toolkit, then builds and runs the two-assembly bridge harness.
# --accept-api rewrites ClientApi/api.shipped.txt from the current ClientApi: run it only when releasing.
set -euo pipefail
cd "$(dirname "$0")"
dotnet build ../AcruxUiToolkit/AcruxUiToolkit.csproj -c Debug -p:Platform=x64 -p:MdkBuildConfiguration=none -v q -nologo | grep -E " error |Build succeeded" | sort -u || true
dotnet build Runner/Runner.csproj -v q -nologo | grep -E " error |Build succeeded" | sort -u || true
dotnet run --project Runner/Runner.csproj --no-build -- "$@"
