#!/usr/bin/env bash

set -euo pipefail

TEST_STARTED_AT_SECONDS="$SECONDS"
TEST_MODE="full"
SCRIPT_DIRECTORY="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPOSITORY_ROOT="$(CDPATH= cd -- "$SCRIPT_DIRECTORY/.." && pwd)"
GAME_PROJECT="$REPOSITORY_ROOT"
UNIT_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj"
GODOT_EXECUTABLE="${GODOT_BIN:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"

print_usage() {
    printf 'Usage: %s [--fast | --full]\n' "$0"
    printf '  --fast  Run the local feedback suite with one Godot integration pass.\n'
    printf '  --full  Run the complete distributability and release gate (default).\n'
}

if [[ "$#" -gt 1 ]]; then
    print_usage >&2
    exit 2
fi
if [[ "$#" -eq 1 ]]; then
    case "$1" in
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

run_with_timeout() {
    local timeout_seconds="$1"
    shift
    perl -e 'alarm shift; exec @ARGV' "$timeout_seconds" "$@"
}

copy_clean_addon() {
    local source_directory="$1"
    local destination_directory="$2"
    local source_file
    local relative_path
    local destination_file

    mkdir -p "$destination_directory"
    while IFS= read -r -d '' source_file; do
        relative_path="${source_file#"$source_directory"/}"
        destination_file="$destination_directory/$relative_path"
        mkdir -p "$(dirname -- "$destination_file")"
        cp "$source_file" "$destination_file"
    done < <(find "$source_directory" -type f \
        ! -path '*/bin/*' \
        ! -path '*/obj/*' \
        ! -path '*/.godot/*' \
        ! -name '.DS_Store' \
        ! -name '*.tmp' \
        ! -name '*.log' \
        -print0)
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
}

if [[ ! -x "$GODOT_EXECUTABLE" ]]; then
    printf 'Godot Mono executable was not found or is not executable: %s\n' "$GODOT_EXECUTABLE" >&2
    exit 1
fi

printf 'Restoring .NET dependencies...\n'
restore_dotnet_projects

printf 'Building Godot C# project...\n'
if [[ "$TEST_MODE" == "full" ]]; then
    run_with_timeout 180 dotnet build "$GAME_PROJECT/Majo Rebuild.csproj" --nologo --no-restore --no-incremental
else
    run_with_timeout 180 dotnet build "$GAME_PROJECT/Majo Rebuild.csproj" --nologo --no-restore
fi

printf 'Running pure C# unit tests...\n'
run_with_timeout 180 dotnet test "$UNIT_TEST_PROJECT" --nologo --no-restore

printf 'Checking Godot headless import and C# compilation for main game project...\n'
run_with_timeout 180 "$GODOT_EXECUTABLE" --headless --path "$GAME_PROJECT" --editor --quit

printf 'Running visible Runtime Demo headless smoke test...\n'
run_with_timeout 60 "$GODOT_EXECUTABLE" --headless --path "$GAME_PROJECT" \
    res://demos/demo-01/RuntimeDemo.tscn -- --demo-auto

HEADLESS_RUN_COUNT=3
if [[ "$TEST_MODE" == "fast" ]]; then
    HEADLESS_RUN_COUNT=1
fi
for ((headless_run = 1; headless_run <= HEADLESS_RUN_COUNT; headless_run++)); do
    printf 'Running Godot headless integration tests (%s/%s)...\n' \
        "$headless_run" "$HEADLESS_RUN_COUNT"
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

if [[ "$TEST_MODE" == "full" ]]; then
    printf 'Verifying Release build configuration smoke test...\n'
    run_with_timeout 180 dotnet build "$GAME_PROJECT/Majo Rebuild.csproj" -c Release --nologo --no-restore --no-incremental
    case "$(uname -s)" in
        Darwin)
            EXPORT_PRESET="M10.5 macOS Smoke"
            EXPORT_OUTPUT="$REPOSITORY_ROOT/build/m10.5/macos/majo-runtime-smoke.pck"
            ;;
        Linux)
            EXPORT_PRESET="M10.5 Linux Smoke"
            EXPORT_OUTPUT="$REPOSITORY_ROOT/build/m10.5/linux/majo-runtime-smoke.pck"
            ;;
        *)
            printf 'Unsupported export smoke host platform: %s\n' "$(uname -s)" >&2
            exit 1
            ;;
    esac

    printf 'Running target-platform Godot export smoke (%s)...\n' "$EXPORT_PRESET"
    mkdir -p "$(dirname -- "$EXPORT_OUTPUT")"
    run_with_timeout 300 "$GODOT_EXECUTABLE" --headless --path "$GAME_PROJECT" \
        --export-pack "$EXPORT_PRESET" "$EXPORT_OUTPUT"
    if [[ ! -s "$EXPORT_OUTPUT" ]]; then
        printf 'Godot export smoke did not produce a non-empty artifact: %s\n' "$EXPORT_OUTPUT" >&2
        exit 1
    fi
fi

printf 'Majo Runtime %s test suite passed.\n' "$TEST_MODE"
TEST_ELAPSED_SECONDS=$((SECONDS - TEST_STARTED_AT_SECONDS))
printf 'Completed in %dm %02ds.\n' \
    "$((TEST_ELAPSED_SECONDS / 60))" "$((TEST_ELAPSED_SECONDS % 60))"
