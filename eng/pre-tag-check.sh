#!/usr/bin/env bash

set -euo pipefail

if (($# != 0)); then
    echo "Usage: $0" >&2
    exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root"

if [[ "$(git branch --show-current)" != main ]]; then
    echo "Release preparation requires the local main branch." >&2
    exit 1
fi

# Porcelain output is stable for scripts and omits optional branch headers.
if [[ -n "$(git status --porcelain --untracked-files=all)" ]]; then
    echo "Release preparation requires a clean working tree." >&2
    exit 1
fi

git fetch origin main --tags

release_commit="$(git rev-parse HEAD)"
if [[ "$release_commit" != "$(git rev-parse origin/main)" ]]; then
    echo "Local main must equal origin/main." >&2
    exit 1
fi

if git tag --points-at "$release_commit" \
    | grep -Eq '^v[0-9]+[.][0-9]+[.][0-9]+([-.][0-9A-Za-z.-]+)?$'; then
    echo "The release commit already has a semantic release tag." >&2
    exit 1
fi

if [[ "$(git config --get gpg.format || true)" != ssh ]]; then
    echo "Git tag signing must use the SSH signature format." >&2
    exit 1
fi

signing_key="$(git config --get user.signingkey || true)"
if [[ -z "$signing_key" ]]; then
    echo "Git user.signingkey is not configured." >&2
    exit 1
fi

case "$signing_key" in
    key::* | ssh-*) ;;
    *)
        expanded_signing_key="${signing_key/#\~/$HOME}"
        if [[ ! -f "$expanded_signing_key" ]]; then
            echo "Configured SSH signing key does not exist: $signing_key" >&2
            exit 1
        fi
        ;;
esac

echo "Release source and signing preparation verified for $release_commit."
