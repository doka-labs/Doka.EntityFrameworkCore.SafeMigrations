#!/usr/bin/env bash

set -euo pipefail

if (($# != 0)); then
    echo "Usage: $0" >&2
    exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root"

echo "Checking pre-tag readiness..."

current_branch="$(git branch --show-current)"
if [[ "$current_branch" != main ]]; then
    branch_display="${current_branch:-detached HEAD}"
    echo "  [FAIL] Branch: expected main, found $branch_display." >&2
    exit 1
fi
echo "  [PASS] Branch: main"

# Porcelain output is stable for scripts and omits optional branch headers.
if [[ -n "$(git status --porcelain --untracked-files=all)" ]]; then
    echo "  [FAIL] Working tree: tracked, staged, or untracked changes exist." >&2
    exit 1
fi
echo "  [PASS] Working tree: clean"

if ! git fetch --quiet origin main --tags; then
    echo "  [FAIL] Remote state: could not fetch origin/main and tags." >&2
    exit 1
fi

release_commit="$(git rev-parse HEAD)"
origin_commit="$(git rev-parse origin/main)"
if [[ "$release_commit" != "$origin_commit" ]]; then
    echo "  [FAIL] Source commit: HEAD $release_commit does not match origin/main $origin_commit." >&2
    exit 1
fi
echo "  [PASS] Source commit: $release_commit matches origin/main"

if git tag --points-at "$release_commit" \
    | grep -Eq '^v[0-9]+[.][0-9]+[.][0-9]+([-.][0-9A-Za-z.-]+)?$'; then
    echo "  [FAIL] Release tag: a semantic release tag already points to $release_commit." >&2
    exit 1
fi
echo "  [PASS] Release tag: no semantic release tag exists for this commit"

if [[ "$(git config --get gpg.format || true)" != ssh ]]; then
    echo "  [FAIL] Tag signing: gpg.format must be ssh." >&2
    exit 1
fi

signing_key="$(git config --get user.signingkey || true)"
if [[ -z "$signing_key" ]]; then
    echo "  [FAIL] Tag signing: user.signingkey is not configured." >&2
    exit 1
fi

case "$signing_key" in
    key::* | ssh-*) ;;
    *)
        expanded_signing_key="${signing_key/#\~/$HOME}"
        if [[ ! -f "$expanded_signing_key" ]]; then
            echo "  [FAIL] Tag signing: configured key does not exist: $signing_key" >&2
            exit 1
        fi
        ;;
esac

echo "  [PASS] Tag signing: SSH configuration is present"
echo "Ready to start Release candidate for $release_commit."
