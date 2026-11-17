#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIRECTORY="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec python3 "$SCRIPT_DIRECTORY/runtime_test_gate.py" "$@"
