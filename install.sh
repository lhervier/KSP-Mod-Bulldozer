#!/usr/bin/env bash
# Thin wrapper: delegates to the shared generic install in KSP-Shared/tools.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
export MOD_NAME="KSPModBulldozer"
exec bash KSP-Shared/tools/install.sh
