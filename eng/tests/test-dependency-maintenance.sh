#!/usr/bin/env bash

set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repository_root"

explicit_sdk_versions="$(
  grep -R -n --include='*.yml' --include='*.yaml' \
    'dotnet-version:' .github/workflows || true
)"

if [[ -n "$explicit_sdk_versions" ]]; then
  printf '%s\n' "$explicit_sdk_versions" >&2
  echo "Workflow SDK versions must come from global.json." >&2
  exit 1
fi

setup_count="$(
  grep -R -h --include='*.yml' --include='*.yaml' \
    'uses: actions/setup-dotnet@' .github/workflows \
    | wc -l \
    | tr -d ' '
)"
global_json_count="$(
  grep -R -h --include='*.yml' --include='*.yaml' \
    -E 'global-json-file: (snapshot-source/)?global\.json[[:space:]]*$' .github/workflows \
    | wc -l \
    | tr -d ' '
)"

if [[ "$setup_count" != "$global_json_count" ]]; then
  echo "Every setup-dotnet step must use global.json." >&2
  exit 1
fi

floating_runner_references="$(
  grep -R -n --include='*.yml' --include='*.yaml' \
    'ubuntu-latest' .github/workflows || true
)"

if [[ -n "$floating_runner_references" ]]; then
  printf '%s\n' "$floating_runner_references" >&2
  echo "Workflow runners must not use floating Ubuntu labels." >&2
  exit 1
fi

unexpected_ubuntu_runners="$(
  grep -R -n --include='*.yml' --include='*.yaml' \
    '^[[:space:]]*runs-on:' .github/workflows \
    | grep -Ev 'runs-on:[[:space:]]*ubuntu-26\.04[[:space:]]*$' || true
)"

if [[ -n "$unexpected_ubuntu_runners" ]]; then
  printf '%s\n' "$unexpected_ubuntu_runners" >&2
  echo "GitHub-hosted jobs must use the pinned ubuntu-26.04 x64 runner." >&2
  exit 1
fi

pinned_ubuntu_runner_count="$(
  # WHY: An empty inventory must reach the explicit failure, not exit silently under pipefail.
  { grep -R -h --include='*.yml' --include='*.yaml' \
    '^[[:space:]]*runs-on:[[:space:]]*ubuntu-26\.04[[:space:]]*$' .github/workflows || true; } \
    | wc -l \
    | tr -d ' '
)"

if [[ "$pinned_ubuntu_runner_count" == "0" ]]; then
  echo "At least one pinned ubuntu-26.04 x64 workflow job is required." >&2
  exit 1
fi

expected_lockfiles="$(
  # WHY: Every solution role owns a reviewed default-profile lock. Let the SDK
  # interpret the solution instead of coupling coverage to project XML layout.
  DOTNET_CLI_UI_LANGUAGE=en dotnet sln Doka.EntityFrameworkCore.SafeMigrations.slnx list \
    | awk '{ sub(/\r$/, "") } /\.csproj$/ { print }' \
    | while IFS= read -r project; do
        printf '%s/packages.lock.json\n' "$(dirname "$project")"
      done \
    | LC_ALL=C sort -u
)"

if [[ -z "$expected_lockfiles" ]]; then
  echo "The solution project inventory is empty; default lock coverage cannot be verified." >&2
  exit 1
fi

actual_lockfiles="$(
  find . \
    -type d \( -name .git -o -name artifacts -o -name bin -o -name obj \
      -o -name BenchmarkDotNet.Artifacts -o -name .bdn \) -prune -o \
    -type f -name packages.lock.json -print \
    | sed 's#^\./##' \
    | LC_ALL=C sort
)"

if [[ "$actual_lockfiles" != "$expected_lockfiles" ]]; then
  echo "Default-profile lock files must match the solution project inventory exactly." >&2
  diff \
    <(printf '%s\n' "$expected_lockfiles") \
    <(printf '%s\n' "$actual_lockfiles") >&2 || true
  exit 1
fi

package_project="src/Doka.EntityFrameworkCore.SafeMigrations/Doka.EntityFrameworkCore.SafeMigrations.csproj"

ci_locked_mode="$(
  dotnet msbuild "$package_project" \
    -nologo \
    -getProperty:RestoreLockedMode \
    -p:ContinuousIntegrationBuild=true
)"
bundle_locked_mode="$(
  dotnet msbuild "$package_project" \
    -nologo \
    -getProperty:RestoreLockedMode \
    -p:ContinuousIntegrationBuild=true \
    -p:RuntimeIdentifier=linux-x64
)"

if [[ "$ci_locked_mode" != "true" || "$bundle_locked_mode" == "true" ]]; then
  echo "Locked restore must apply to platform-neutral CI restores only." >&2
  exit 1
fi

echo "Dependency maintenance contract passed."
