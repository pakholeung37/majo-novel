#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIRECTORY="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPOSITORY_ROOT="$(CDPATH= cd -- "$SCRIPT_DIRECTORY/.." && pwd)"
GAME_PROJECT="$REPOSITORY_ROOT/Majo Novel.csproj"
UNIT_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj"
FORMAT_MODE="check"

print_usage() {
    printf 'Usage: %s [--check | --format]\n' "$0"
    printf '  --check   Verify formatting without changing files (default).\n'
    printf '  --format  Apply formatting changes to the project.\n'
}

if [[ "$#" -gt 1 ]]; then
    print_usage >&2
    exit 2
fi
if [[ "$#" -eq 1 ]]; then
    case "$1" in
        --check)
            ;;
        --format|--fix)
            FORMAT_MODE="format"
            ;;
        -h|--help)
            print_usage
            exit 0
            ;;
        *)
            printf 'Unknown quality option: %s\n' "$1" >&2
            print_usage >&2
            exit 2
            ;;
    esac
fi

collect_cs_files() {
    local directory
    local file
    local target_array="$1"
    shift

    for directory in "$@"; do
        while IFS= read -r -d '' file; do
            case "$target_array" in
                game) game_format_files+=("${file#"$REPOSITORY_ROOT/"}") ;;
                unit) unit_format_files+=("${file#"$REPOSITORY_ROOT/"}") ;;
            esac
        done < <(find "$directory" -type f -name '*.cs' \
            ! -path '*/bin/*' ! -path '*/obj/*' ! -path '*/.godot/*' -print0)
    done
}

game_format_files=()
unit_format_files=()
collect_cs_files game \
    "$REPOSITORY_ROOT/addons/majo_runtime" \
    "$REPOSITORY_ROOT/demos/demo-01" \
    "$REPOSITORY_ROOT/demos/manosaba" \
    "$REPOSITORY_ROOT/tests/headless"
collect_cs_files unit "$REPOSITORY_ROOT/tests/Majo.Runtime.UnitTests"

cd "$REPOSITORY_ROOT"
printf 'Restoring .NET dependencies for quality checks...\n'
dotnet restore "$UNIT_TEST_PROJECT" --nologo

run_format() {
    local project="$1"
    shift
    if [[ "$FORMAT_MODE" == "check" ]]; then
        dotnet format "$project" --no-restore --verify-no-changes \
            --include "$@" --verbosity minimal
    else
        dotnet format "$project" --no-restore \
            --include "$@" --verbosity minimal
    fi
}

printf 'Checking main project formatting...\n'
run_format "$GAME_PROJECT" "${game_format_files[@]}"

printf 'Checking unit test formatting...\n'
run_format "$UNIT_TEST_PROJECT" "${unit_format_files[@]}"

if [[ "$FORMAT_MODE" == "format" ]]; then
    printf 'Formatting applied successfully.\n'
else
    printf 'Quality checks passed.\n'
fi
