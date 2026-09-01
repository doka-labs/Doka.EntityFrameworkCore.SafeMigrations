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

expect_success 10.1.1

expect_failure v10.1.1
expect_failure 10.1.1-RC.1
expect_failure 10.1.1+build.1
expect_failure 10.1.1-rc.01
expect_failure 10.1.1--rc
expect_failure 10.1.1-rc.1
expect_failure 10.1.0
expect_failure 10.1.2

echo "Release version positive and negative cases passed."
