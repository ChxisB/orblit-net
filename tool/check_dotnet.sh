#!/bin/bash
# Builds, tests and format-checks everything under dotnet/.
#
# The analyzer rules from the standard's C# section are build errors, so the
# build is the lint. A build that fails stops the run, because a test over a
# stale binary says nothing.
set -uo pipefail
cd "$(dirname "$0")/../dotnet"

failures=0

step() {
  local label=$1
  shift
  if "$@"; then
    echo "  ok    $label"
  else
    echo "  FAIL  $label"
    failures=$((failures + 1))
    return 1
  fi
}

step "dotnet build" dotnet build --nologo || exit "$failures"
step "dotnet test" dotnet test --no-build
step "dotnet format" dotnet format --verify-no-changes --no-restore

exit "$failures"
