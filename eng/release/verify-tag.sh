#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 <release-tag> <expected-commit>" >&2
}

if (($# != 2)) || [[ -z "$1" || -z "$2" ]]; then
    usage
    exit 2
fi

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
release_tag="$1"
expected_commit="$2"
allowed_signers="$source_root/eng/release/allowed-signers"

if [[ ! -s "$allowed_signers" ]]; then
    echo "Release signer trust policy is missing or empty: $allowed_signers" >&2
    exit 1
fi

if [[ "$(git -C "$source_root" cat-file -t "refs/tags/$release_tag")" != "tag" ]]; then
    echo "Release tag must be an annotated tag object: $release_tag" >&2
    exit 1
fi

if [[ "$(git -C "$source_root" rev-list -n 1 "refs/tags/$release_tag")" != "$expected_commit" ]]; then
    echo "Release tag does not identify the qualified commit: $release_tag" >&2
    exit 1
fi

git -C "$source_root" \
    -c gpg.format=ssh \
    -c gpg.ssh.allowedSignersFile="$allowed_signers" \
    verify-tag "$release_tag"

echo "Verified authorized release tag $release_tag for $expected_commit."
