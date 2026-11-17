#!/usr/bin/env bash

set -euo pipefail

TEST_STARTED_AT_SECONDS="$SECONDS"
TEST_MODE="full"
SCRIPT_DIRECTORY="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPOSITORY_ROOT="$(CDPATH= cd -- "$SCRIPT_DIRECTORY/.." && pwd)"
GAME_PROJECT="$REPOSITORY_ROOT"
UNIT_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj"
GENERATOR_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.CommandGenerator.Tests/Majo.CommandGenerator.Tests.csproj"
GODOT_EXECUTABLE="${GODOT_BIN:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"

print_usage() {
    printf 'Usage: %s [--unit | --fast | --full]\n' "$0"
    printf '  --unit  Build and run pure C# unit tests without launching Godot.\n'
    printf '  --fast  Run the local feedback suite with one Godot integration pass.\n'
    printf '  --full  Run the complete distributability and release gate (default).\n'
}

if [[ "$#" -gt 1 ]]; then
    print_usage >&2
    exit 2
fi
if [[ "$#" -eq 1 ]]; then
    case "$1" in
        --unit)
            TEST_MODE="unit"
            ;;
        --fast)
            TEST_MODE="fast"
            ;;
        --full)
            ;;
        -h|--help)
            print_usage
            exit 0
            ;;
        *)
            printf 'Unknown test option: %s\n' "$1" >&2
            print_usage >&2
            exit 2
            ;;
    esac
fi

print_result() {
    printf 'Majo Runtime %s test suite passed.\n' "$TEST_MODE"
    TEST_ELAPSED_SECONDS=$((SECONDS - TEST_STARTED_AT_SECONDS))
    printf 'Completed in %dm %02ds.\n' \
        "$((TEST_ELAPSED_SECONDS / 60))" "$((TEST_ELAPSED_SECONDS % 60))"
}

run_with_timeout() {
    local timeout_seconds="$1"
    shift
    perl -e 'alarm shift; exec @ARGV' "$timeout_seconds" "$@"
}

restore_dotnet_projects() {
    local unit_log
    local unit_pid
    local restore_status=0

    unit_log="$(mktemp)"
    run_with_timeout 180 dotnet restore "$UNIT_TEST_PROJECT" --nologo >"$unit_log" 2>&1 &
    unit_pid="$!"

    if ! wait "$unit_pid"; then
        restore_status=1
    fi
    cat "$unit_log"
    rm -f "$unit_log"
    if [[ "$restore_status" -ne 0 ]]; then
        printf 'One or more .NET dependency restores failed.\n' >&2
        return 1
    fi
    run_with_timeout 180 dotnet restore "$GENERATOR_TEST_PROJECT" --nologo
}

run_core_consumer_smoke() {
    local smoke_directory
    local package_directory
    smoke_directory="$(mktemp -d)"
    package_directory="$(mktemp -d)"
    trap 'rm -rf "$smoke_directory" "$package_directory"' RETURN
    python3 "$REPOSITORY_ROOT/tools/package-addon.py" --output-dir "$package_directory"
    cp -R "$REPOSITORY_ROOT/tools/core-consumer-smoke/." "$smoke_directory/"
    python3 "$REPOSITORY_ROOT/addons/majo_runtime/Tools/majo.py" install \
        --project "$smoke_directory" \
        --archive "$package_directory/majo-runtime-0.1.0-with-yarn.zip"
    if find "$smoke_directory" -path '*/demos/*' -print -quit | grep -q .; then
        printf 'Core consumer smoke unexpectedly copied demo sources.\n' >&2
        return 1
    fi
    (
        cd "$smoke_directory"
        run_with_timeout 180 dotnet restore CoreConsumerSmoke.csproj --nologo
        run_with_timeout 180 dotnet build CoreConsumerSmoke.csproj --nologo --no-restore
        run_with_timeout 180 "$GODOT_EXECUTABLE" --headless --path "$smoke_directory" --editor --quit
        python3 "$REPOSITORY_ROOT/addons/majo_runtime/Tools/majo.py" doctor --project "$smoke_directory"
        python3 "$REPOSITORY_ROOT/addons/majo_runtime/Tools/majo.py" install \
            --project "$smoke_directory" --archive "$package_directory/majo-runtime-0.1.0-with-yarn.zip"
        run_with_timeout 60 "$GODOT_EXECUTABLE" --headless --path "$smoke_directory"
        python3 "$REPOSITORY_ROOT/tools/test-addon-export.py" "$smoke_directory" "$GODOT_EXECUTABLE"

    )
}

if [[ "$TEST_MODE" != "unit" && ! -x "$GODOT_EXECUTABLE" ]]; then
    printf 'Godot Mono executable was not found or is not executable: %s\n' "$GODOT_EXECUTABLE" >&2
    exit 1
fi

printf 'Checking addon installation and upgrade protection...\n'
python3 "$REPOSITORY_ROOT/tools/test-addon-manager.py"

printf 'Restoring .NET dependencies...\n'
restore_dotnet_projects

printf 'Building unit tests and their Godot project dependencies...\n'
if [[ "$TEST_MODE" == "full" ]]; then
    run_with_timeout 180 dotnet build "$UNIT_TEST_PROJECT" --nologo --no-restore --no-incremental
else
    run_with_timeout 180 dotnet build "$UNIT_TEST_PROJECT" --nologo --no-restore
fi

printf 'Running pure C# unit tests...\n'
run_with_timeout 180 dotnet test "$UNIT_TEST_PROJECT" --nologo --no-restore --no-build

printf 'Running command generator unit tests...\n'
run_with_timeout 180 dotnet test "$GENERATOR_TEST_PROJECT" --nologo --no-restore

if [[ "$TEST_MODE" == "unit" ]]; then
    print_result
    exit 0
fi

printf 'Checking Godot headless import and C# compilation for main game project...\n'
run_with_timeout 180 "$GODOT_EXECUTABLE" --headless --path "$GAME_PROJECT" --editor --quit

printf 'Running Godot headless integration tests...\n'
run_with_timeout 60 "$GODOT_EXECUTABLE" --headless --path "$GAME_PROJECT" \
    res://tests/headless/HeadlessTestRunner.tscn --

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

if [[ "$TEST_MODE" == "full" ]]; then
    printf 'Installing release ZIP and running independent consumer import, export and PCK boot...\n'
    run_core_consumer_smoke
fi

print_result
