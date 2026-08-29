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

expect_success 10.0.0-rc.1
expect_success 10.0.0-rc.2
expect_success 10.0.0-rc.3
expect_success 10.0.0

expect_failure v10.0.0-rc.1
expect_failure 10.0.0-RC.1
expect_failure 10.0.0+build.1
expect_failure 10.0.0-rc.01
expect_failure 10.0.0--rc
expect_failure 10.0.1
expect_failure 10.0.1-rc.1
expect_failure 10.0.0-rc.4

echo "Release version positive and negative cases passed."
