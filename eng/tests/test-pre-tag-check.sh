#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
validator="$repo_root/eng/pre-tag-check.sh"
fixture_root="$(mktemp -d)"
trap 'rm -rf "$fixture_root"' EXIT

remote="$fixture_root/remote.git"
repository="$fixture_root/repository"
signing_key="$fixture_root/signing-key.pub"

git init --quiet --bare "$remote"
git init --quiet --initial-branch=main "$repository"
git -C "$repository" remote add origin "$remote"
git -C "$repository" config commit.gpgsign false
git -C "$repository" config user.email test@example.invalid
git -C "$repository" config user.name "SafeMigrations Test"

mkdir -p "$repository/eng"
cp "$validator" "$repository/eng/pre-tag-check.sh"
printf 'fixture\n' >"$repository/tracked.txt"
git -C "$repository" add eng/pre-tag-check.sh tracked.txt
git -C "$repository" commit --quiet -m "Create release fixture"
git -C "$repository" push --quiet --set-upstream origin main

printf 'test signing key\n' >"$signing_key"
git -C "$repository" config gpg.format ssh
git -C "$repository" config user.signingkey "$signing_key"
git -C "$repository" config status.branch true

short_status="$(git -C "$repository" status --short)"
if [[ "$short_status" != '## main...origin/main' ]]; then
    echo "Fixture did not reproduce the status.branch header." >&2
    exit 1
fi

bash "$repository/eng/pre-tag-check.sh" \
    >"$fixture_root/clean.stdout" \
    2>"$fixture_root/clean.stderr"

grep -Fq "Release source and signing preparation verified" \
    "$fixture_root/clean.stdout"

printf 'untracked\n' >"$repository/untracked.txt"
if bash "$repository/eng/pre-tag-check.sh" \
    >"$fixture_root/dirty.stdout" \
    2>"$fixture_root/dirty.stderr"; then
    echo "A dirty release checkout unexpectedly passed." >&2
    exit 1
fi

grep -Fq "Release preparation requires a clean working tree." \
    "$fixture_root/dirty.stderr"

echo "Pre-tag working-tree positive and negative cases passed."
