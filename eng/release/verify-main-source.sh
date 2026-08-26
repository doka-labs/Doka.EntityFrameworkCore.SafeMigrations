#!/usr/bin/env bash

set -euo pipefail

if (($# != 1)) || [[ -z "$1" ]]; then
    echo "Usage: $0 <expected-commit>" >&2
    exit 2
fi

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
expected_commit="$1"

if [[ "$(git -C "$source_root" rev-parse --verify HEAD)" != "$expected_commit" ]]; then
    echo "The local checkout does not identify the qualified commit." >&2
    exit 1
fi

git -C "$source_root" fetch --quiet --no-tags --no-prune --no-prune-tags \
    --no-recurse-submodules --refmap= origin \
    +refs/heads/main:refs/remotes/origin/main

if ! git -C "$source_root" merge-base --is-ancestor "$expected_commit" refs/remotes/origin/main; then
    echo "The qualified commit is not an ancestor of current origin/main." >&2
    exit 1
fi

echo "Verified qualified commit $expected_commit remains on origin/main."
