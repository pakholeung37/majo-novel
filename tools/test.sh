#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIRECTORY="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPOSITORY_ROOT="$(CDPATH= cd -- "$SCRIPT_DIRECTORY/.." && pwd)"
GAME_PROJECT="$REPOSITORY_ROOT/game"
UNIT_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj"
GODOT_EXECUTABLE="${GODOT_BIN:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"

run_with_timeout() {
    local timeout_seconds="$1"
    shift
    perl -e 'alarm shift; exec @ARGV' "$timeout_seconds" "$@"
}

if [[ ! -x "$GODOT_EXECUTABLE" ]]; then
    printf 'Godot Mono executable was not found or is not executable: %s\n' "$GODOT_EXECUTABLE" >&2
    exit 1
fi

printf 'Checking Majo Runtime and test formatting/static diagnostics...\n'
run_with_timeout 180 dotnet format "$GAME_PROJECT/Majo Rebuild.csproj" \
    --verify-no-changes \
    --include "$GAME_PROJECT/addons/majo_runtime" "$GAME_PROJECT/demo" \
        "$GAME_PROJECT/tests/headless" \
    --verbosity minimal
run_with_timeout 180 dotnet format "$UNIT_TEST_PROJECT" \
    --verify-no-changes \
    --include "$REPOSITORY_ROOT/tests/Majo.Runtime.UnitTests" \
    --verbosity minimal

printf 'Building Godot C# project...\n'
run_with_timeout 180 dotnet build "$GAME_PROJECT/Majo Rebuild.csproj" --nologo --no-incremental

printf 'Running pure C# unit tests...\n'
run_with_timeout 180 dotnet test "$UNIT_TEST_PROJECT" --nologo

printf 'Checking Godot headless import and C# compilation...\n'
run_with_timeout 180 "$GODOT_EXECUTABLE" --headless --path "$GAME_PROJECT" --editor --quit

printf 'Running M5 visible-scene headless smoke test...\n'
run_with_timeout 60 "$GODOT_EXECUTABLE" --headless --path "$GAME_PROJECT" -- --m5-auto

for headless_run in 1 2 3; do
    printf 'Running Godot headless integration tests (%s/3)...\n' "$headless_run"
    run_with_timeout 60 "$GODOT_EXECUTABLE" --headless --path "$GAME_PROJECT" \
        res://tests/headless/HeadlessTestRunner.tscn
done

printf 'Verifying that a failing headless assertion returns a non-zero exit code...\n'
set +e
run_with_timeout 60 "$GODOT_EXECUTABLE" --headless --path "$GAME_PROJECT" \
    res://tests/headless/HeadlessTestRunner.tscn -- --majo-force-failure \
    >/dev/null 2>&1
failure_probe_status="$?"
set -e

if [[ "$failure_probe_status" -eq 0 ]]; then
    printf 'Headless failure probe unexpectedly succeeded.\n' >&2
    exit 1
fi

printf 'Majo Runtime test gate passed.\n'
