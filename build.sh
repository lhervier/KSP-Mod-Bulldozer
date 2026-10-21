#!/usr/bin/env bash
# Thin wrapper: delegates to the shared generic build in KSP-Shared/tools.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
export MOD_CSPROJ="KSPModBulldozer.csproj"
exec bash KSP-Shared/tools/build.sh
