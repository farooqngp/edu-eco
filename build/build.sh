#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${1:-Release}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SLN="$ROOT_DIR/SDLC.sln"

cd "$ROOT_DIR"

dotnet restore "$SLN"
dotnet build "$SLN" --no-restore --configuration "$CONFIGURATION"
dotnet test "$SLN" --no-build --configuration "$CONFIGURATION"
