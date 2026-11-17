#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIRECTORY="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPOSITORY_ROOT="$(CDPATH= cd -- "$SCRIPT_DIRECTORY/.." && pwd)"
GAME_PROJECT="$REPOSITORY_ROOT/Majo Novel.csproj"
UNIT_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.UnitTests/Majo.Runtime.UnitTests.csproj"
CORE_PROJECT="$REPOSITORY_ROOT/addons/majo_runtime/Assemblies/Majo.Runtime.Core/Majo.Runtime.Core.csproj"
CORE_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.Core.UnitTests/Majo.Runtime.Core.UnitTests.csproj"
DOMAIN_PROJECT="$REPOSITORY_ROOT/addons/majo_runtime/Assemblies/Majo.Runtime.Domain/Majo.Runtime.Domain.csproj"
DOMAIN_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.Domain.UnitTests/Majo.Runtime.Domain.UnitTests.csproj"
COMMAND_PROJECT="$REPOSITORY_ROOT/addons/majo_runtime/Assemblies/Majo.Runtime.Commands/Majo.Runtime.Commands.csproj"
COMMAND_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.Commands.UnitTests/Majo.Runtime.Commands.UnitTests.csproj"
PERSISTENCE_PROJECT="$REPOSITORY_ROOT/addons/majo_runtime/Assemblies/Majo.Runtime.Persistence/Majo.Runtime.Persistence.csproj"
PERSISTENCE_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.Persistence.UnitTests/Majo.Runtime.Persistence.UnitTests.csproj"
RESOURCE_PROJECT="$REPOSITORY_ROOT/addons/majo_runtime/Assemblies/Majo.Runtime.Resources/Majo.Runtime.Resources.csproj"
RESOURCE_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.Resources.UnitTests/Majo.Runtime.Resources.UnitTests.csproj"
ENGINE_PROJECT="$REPOSITORY_ROOT/addons/majo_runtime/Assemblies/Majo.Runtime.Engine/Majo.Runtime.Engine.csproj"
YARN_PROJECT="$REPOSITORY_ROOT/addons/majo_runtime/Assemblies/Majo.Runtime.Yarn/Majo.Runtime.Yarn.csproj"
COMPOSITION_PROJECT="$REPOSITORY_ROOT/addons/majo_runtime/Assemblies/Majo.Runtime.Godot/Majo.Runtime.Godot.csproj"
DEMO_PROJECT="$REPOSITORY_ROOT/demos/Majo.Demos.csproj"
DEMO_TEST_PROJECT="$REPOSITORY_ROOT/tests/Majo.Demos.UnitTests/Majo.Demos.UnitTests.csproj"
HEADLESS_PROJECT="$REPOSITORY_ROOT/tests/Majo.Runtime.Headless/Majo.Runtime.Headless.csproj"
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
                core) core_format_files+=("${file#"$REPOSITORY_ROOT/"}") ;;
                core_unit) core_unit_format_files+=("${file#"$REPOSITORY_ROOT/"}") ;;
                domain_unit) domain_unit_format_files+=("${file#"$REPOSITORY_ROOT/"}") ;;
            esac
        done < <(find "$directory" -type f -name '*.cs' \
            ! -path '*/bin/*' ! -path '*/obj/*' ! -path '*/.godot/*' -print0)
    done
}

game_format_files=()
unit_format_files=()
core_format_files=()
core_unit_format_files=()
domain_unit_format_files=()
collect_cs_files game \
    "$REPOSITORY_ROOT/addons/majo_runtime" \
    "$REPOSITORY_ROOT/demos/demo-01" \
    "$REPOSITORY_ROOT/demos/manosaba" \
    "$REPOSITORY_ROOT/tests/headless"
collect_cs_files unit "$REPOSITORY_ROOT/tests/Majo.Runtime.UnitTests"
collect_cs_files core "$REPOSITORY_ROOT/addons/majo_runtime/Runtime/Core"
collect_cs_files core_unit "$REPOSITORY_ROOT/tests/Majo.Runtime.Core.UnitTests"
collect_cs_files domain_unit "$REPOSITORY_ROOT/tests/Majo.Runtime.Domain.UnitTests"

cd "$REPOSITORY_ROOT"
printf 'Restoring .NET dependencies for quality checks...\n'
dotnet restore "$UNIT_TEST_PROJECT" --nologo
dotnet restore "$CORE_TEST_PROJECT" --nologo
dotnet restore "$DOMAIN_TEST_PROJECT" --nologo
dotnet restore "$COMMAND_TEST_PROJECT" --nologo
dotnet restore "$PERSISTENCE_TEST_PROJECT" --nologo
dotnet restore "$RESOURCE_TEST_PROJECT" --nologo
dotnet restore "$DEMO_TEST_PROJECT" --nologo
dotnet restore "$GAME_PROJECT" --nologo

run_format() {
    local project="$1"
    shift
    local options=(--no-restore --verbosity minimal)
    if [[ "$FORMAT_MODE" == "check" ]]; then
        options+=(--verify-no-changes)
    fi
    if [[ "$#" -gt 0 ]]; then
        options+=(--include "$@")
    fi
    dotnet format "$project" "${options[@]}"
}

printf 'Checking independent Core formatting...\n'
run_format "$CORE_PROJECT" "${core_format_files[@]}"
run_format "$CORE_TEST_PROJECT" "${core_unit_format_files[@]}"

printf 'Checking independent domain formatting...\n'
run_format "$DOMAIN_PROJECT"
run_format "$DOMAIN_TEST_PROJECT" "${domain_unit_format_files[@]}"

printf 'Checking independent command formatting...\n'
run_format "$COMMAND_PROJECT"
run_format "$COMMAND_TEST_PROJECT"

printf 'Checking independent persistence and resource formatting...\n'
run_format "$PERSISTENCE_PROJECT"
run_format "$PERSISTENCE_TEST_PROJECT"
run_format "$RESOURCE_PROJECT"
run_format "$RESOURCE_TEST_PROJECT"

printf 'Checking main project formatting...\n'
run_format "$ENGINE_PROJECT"
run_format "$YARN_PROJECT"
run_format "$COMPOSITION_PROJECT"
run_format "$DEMO_PROJECT"
run_format "$HEADLESS_PROJECT"
run_format "$GAME_PROJECT"
run_format "$DEMO_TEST_PROJECT"

printf 'Checking unit test formatting...\n'
run_format "$UNIT_TEST_PROJECT" "${unit_format_files[@]}"

if [[ "$FORMAT_MODE" == "format" ]]; then
    printf 'Formatting applied successfully.\n'
else
    printf 'Quality checks passed.\n'
fi
