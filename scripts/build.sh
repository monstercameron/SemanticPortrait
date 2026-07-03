#!/usr/bin/env bash
#
# Build, test, and optionally run SemanticPortrait.
# Wraps the dotnet CLI for the desktop app (.NET MAUI Blazor Hybrid, .NET 10, win-arm64).
#
# Usage:
#   scripts/build.sh [options]
#
# Options:
#   -c, --configuration <Debug|Release>   Build configuration (default: Debug)
#   -t, --test                            Run the test suite after building
#   -r, --run                             Launch the app after a successful build
#       --clean                           Remove bin/ and obj/ before building
#   -h, --help                            Show this help
#
# Examples:
#   scripts/build.sh --test
#   scripts/build.sh -c Release --run
set -euo pipefail

CONFIGURATION="Debug"
DO_TEST=0
DO_RUN=0
DO_CLEAN=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration) CONFIGURATION="$2"; shift 2 ;;
        -t|--test)          DO_TEST=1; shift ;;
        -r|--run)           DO_RUN=1; shift ;;
        --clean)            DO_CLEAN=1; shift ;;
        -h|--help)          sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_PROJ="$REPO_ROOT/src/SemanticPortrait.App/SemanticPortrait.App.csproj"
TEST_PROJ="$REPO_ROOT/tests/SemanticPortrait.Tests/SemanticPortrait.Tests.csproj"
FRAMEWORK="net10.0-windows10.0.19041.0"

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }

if [[ "$DO_CLEAN" -eq 1 ]]; then
    step "Cleaning bin/ and obj/"
    find "$REPO_ROOT/src" "$REPO_ROOT/tests" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
fi

step "Building app ($CONFIGURATION, $FRAMEWORK)"
dotnet build "$APP_PROJ" -c "$CONFIGURATION" -f "$FRAMEWORK"

if [[ "$DO_TEST" -eq 1 ]]; then
    step "Running tests ($CONFIGURATION)"
    dotnet test "$TEST_PROJ" -c "$CONFIGURATION"
fi

if [[ "$DO_RUN" -eq 1 ]]; then
    step "Launching app"
    dotnet run --project "$APP_PROJ" -c "$CONFIGURATION" -f "$FRAMEWORK"
fi

step "Done."
