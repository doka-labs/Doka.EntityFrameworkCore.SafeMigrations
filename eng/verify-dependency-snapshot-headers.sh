#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "usage: verify-dependency-snapshot-headers.sh <response-headers>" >&2
  exit 2
fi

headers_path="$1"
if [[ ! -f "$headers_path" ]]; then
  echo "dependency comparison headers do not exist: $headers_path" >&2
  exit 1
fi

if ! snapshot_warning="$({
  awk '
    BEGIN { found = 0 }
    tolower($0) ~ /^x-github-dependency-graph-snapshot-warnings:[[:space:]]*/ {
      found = 1
      value = $0
      sub(/^[^:]*:[[:space:]]*/, "", value)
      sub(/\r$/, "", value)
      print value
      exit
    }
    END { if (!found) exit 1 }
  ' "$headers_path"
})"; then
  echo "GitHub dependency comparison omitted the snapshot-warning header." >&2
  exit 1
fi

if [[ -n "$snapshot_warning" ]]; then
  echo "GitHub dependency comparison still reports incomplete snapshots." >&2
  exit 1
fi

echo "GitHub dependency comparison reports complete snapshots."
