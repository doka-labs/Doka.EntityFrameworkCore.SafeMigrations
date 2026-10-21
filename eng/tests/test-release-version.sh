#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
validator="$repo_root/eng/validate-release-version.sh"

expect_success() {
    if ! bash "$validator" "$1" >/dev/null; then
        echo "Expected valid release version: $1" >&2
        exit 1
    fi
}

expect_failure() {
    if bash "$validator" "$1" >/dev/null 2>&1; then
        echo "Expected invalid release version: $1" >&2
        exit 1
    fi
}

expect_success 10.6.0

# Release-note categories belong to one section each, even when several fixes
# and release-preparation changes are combined in the same patch.
awk '
    /^## \[10\.6\.0\] - / { in_release = 1; next }
    in_release && /^## / { exit }
    in_release && /^### / && seen[$0]++ {
        print "Duplicate release-note category: " $0 > "/dev/stderr"
        exit 1
    }
' "$repo_root/CHANGELOG.md"

expect_failure v10.6.0
expect_failure 10.6.0-RC.1
expect_failure 10.6.0+build.1
expect_failure 10.6.0-rc.01
expect_failure 10.6.0--rc
expect_failure 10.6.0-rc.1
expect_failure 10.1.0
expect_failure 10.2.0
expect_failure 10.2.1
expect_failure 10.3.0
expect_failure 10.3.1
expect_failure 10.3.2
expect_failure 10.3.3
expect_failure 10.4.0
expect_failure 10.4.1
expect_failure 10.4.2
expect_failure 10.4.3
expect_failure 10.4.4
expect_failure 10.4.5
expect_failure 10.4.6
expect_failure 10.4.7
expect_failure 10.4.8
expect_failure 10.4.9
expect_failure 10.4.10
expect_failure 10.4.11
expect_failure 10.4.12
expect_failure 10.5.0
expect_failure 10.5.1
expect_failure 10.6.1
expect_failure 10.7.0

echo "Release version positive and negative cases passed."
