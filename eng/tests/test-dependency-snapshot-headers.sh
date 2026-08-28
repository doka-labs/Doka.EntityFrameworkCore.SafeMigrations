#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
verifier="$repository_root/eng/verify-dependency-snapshot-headers.sh"
fixture_root="$(mktemp -d)"
trap 'rm -rf "$fixture_root"' EXIT

printf 'HTTP/2.0 200 OK\r\nX-Github-Dependency-Graph-Snapshot-Warnings: \r\n\r\n' \
  >"$fixture_root/complete.headers"

bash "$verifier" "$fixture_root/complete.headers" \
  >"$fixture_root/complete.stdout" \
  2>"$fixture_root/complete.stderr"

grep -Fq "reports complete snapshots" "$fixture_root/complete.stdout"
test ! -s "$fixture_root/complete.stderr"

printf 'HTTP/2.0 200 OK\nX-Github-Dependency-Graph-Snapshot-Warnings: c25hcHNob3QgbWlzc2luZw==\n\n' \
  >"$fixture_root/incomplete.headers"

if bash "$verifier" "$fixture_root/incomplete.headers" \
  >"$fixture_root/incomplete.stdout" \
  2>"$fixture_root/incomplete.stderr"; then
  echo "incomplete dependency snapshots unexpectedly passed" >&2
  exit 1
fi

grep -Fq "still reports incomplete snapshots" "$fixture_root/incomplete.stderr"

printf 'HTTP/2.0 200 OK\nContent-Type: application/json\n\n' \
  >"$fixture_root/missing.headers"

if bash "$verifier" "$fixture_root/missing.headers" \
  >"$fixture_root/missing.stdout" \
  2>"$fixture_root/missing.stderr"; then
  echo "a missing dependency snapshot header unexpectedly passed" >&2
  exit 1
fi

grep -Fq "omitted the snapshot-warning header" "$fixture_root/missing.stderr"

if bash "$verifier" "$fixture_root/absent.headers" \
  >"$fixture_root/absent.stdout" \
  2>"$fixture_root/absent.stderr"; then
  echo "an absent dependency response unexpectedly passed" >&2
  exit 1
fi

grep -Fq "dependency comparison headers do not exist" "$fixture_root/absent.stderr"

echo "dependency snapshot header tests passed"
