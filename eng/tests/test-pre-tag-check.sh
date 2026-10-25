#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
validator="$repo_root/eng/pre-tag-check.sh"
fixture_root="$(mktemp -d)"
trap 'rm -rf "$fixture_root"' EXIT

remote="$fixture_root/remote.git"
repository="$fixture_root/repository"
signing_key="$fixture_root/signing-key.pub"

expect_failure() {
    local name="$1"
    local expected_message="$2"

    if bash "$repository/eng/pre-tag-check.sh" \
        >"$fixture_root/$name.stdout" \
        2>"$fixture_root/$name.stderr"; then
        echo "Pre-tag case '$name' unexpectedly passed." >&2
        exit 1
    fi

    grep -Fxq "$expected_message" "$fixture_root/$name.stderr"
}

git init --quiet --bare "$remote"
git init --quiet --initial-branch=main "$repository"
git -C "$repository" remote add origin "$remote"
git -C "$repository" config commit.gpgsign false
git -C "$repository" config user.email test@example.invalid
git -C "$repository" config user.name "SafeMigrations Test"

mkdir -p "$repository/eng/release"
cp "$validator" "$repository/eng/pre-tag-check.sh"
cp "$repo_root/eng/release/lines.py" "$repository/eng/release/lines.py"
cp "$repo_root/eng/release/lines-policy.json" "$repository/eng/release/lines-policy.json"
printf 'fixture\n' >"$repository/tracked.txt"
git -C "$repository" add eng tracked.txt
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

release_commit="$(git -C "$repository" rev-parse HEAD)"
grep -Fxq "Checking pre-tag readiness..." "$fixture_root/clean.stdout"
grep -Fxq "  [PASS] Branch: main" "$fixture_root/clean.stdout"
grep -Fxq "  [PASS] Working tree: clean" "$fixture_root/clean.stdout"
grep -Fxq "  [PASS] Source commit: $release_commit matches origin/main" \
    "$fixture_root/clean.stdout"
grep -Fxq "  [PASS] Release tag: no semantic release tag exists for this commit" \
    "$fixture_root/clean.stdout"
grep -Fxq "  [PASS] Tag signing: SSH configuration is present" \
    "$fixture_root/clean.stdout"
grep -Fxq "Ready to start Release candidate for $release_commit." \
    "$fixture_root/clean.stdout"
test ! -s "$fixture_root/clean.stderr"

printf 'untracked\n' >"$repository/untracked.txt"
expect_failure \
    dirty \
    "  [FAIL] Working tree: tracked, staged, or untracked changes exist."
rm "$repository/untracked.txt"

git -C "$repository" switch --quiet -c feature/test
expect_failure feature-branch \
    "  [FAIL] Branch: expected an eligible exact release line, found feature/test."
git -C "$repository" switch --quiet main

git -C "$repository" switch --quiet -c maintenance/10.x
expect_failure inactive-line \
    "  [FAIL] Branch: expected an eligible exact release line, found maintenance/10.x."
git -C "$repository" switch --quiet main

# WHY: A prepared line may qualify without granting any publication permission.
# Mutate only the isolated Git fixture; the repository policy stays inactive.
git -C "$repository" switch --quiet maintenance/10.x
python3 - "$repository/eng/release/lines-policy.json" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
policy = json.loads(path.read_text(encoding="ascii"))
policy["lines"][1]["state"] = "prepared"
path.write_text(json.dumps(policy, indent=2) + "\n", encoding="ascii")
PY
git -C "$repository" add eng/release/lines-policy.json
git -C "$repository" commit --quiet -m "Prepare isolated maintenance fixture"
git -C "$repository" push --quiet --set-upstream origin maintenance/10.x
bash "$repository/eng/pre-tag-check.sh" \
    >"$fixture_root/prepared.stdout" 2>"$fixture_root/prepared.stderr"
grep -Fxq "  [PASS] Branch: maintenance/10.x" "$fixture_root/prepared.stdout"
test ! -s "$fixture_root/prepared.stderr"
git -C "$repository" switch --quiet main

release_tag=v10.0.0-rc.2
git -C "$repository" -c tag.gpgSign=false tag "$release_tag"
expect_failure \
    existing-tag \
    "  [FAIL] Release tag: a semantic release tag already points to $release_commit."
git -C "$repository" tag --delete "$release_tag" >/dev/null

git -C "$repository" config gpg.format openpgp
expect_failure invalid-signing-format "  [FAIL] Tag signing: gpg.format must be ssh."
git -C "$repository" config gpg.format ssh

git -C "$repository" config --unset user.signingkey
expect_failure missing-signing-key "  [FAIL] Tag signing: user.signingkey is not configured."
git -C "$repository" config user.signingkey "$signing_key"

missing_signing_key="$fixture_root/missing-signing-key.pub"
git -C "$repository" config user.signingkey "$missing_signing_key"
expect_failure \
    invalid-signing-key \
    "  [FAIL] Tag signing: configured key does not exist: $missing_signing_key"
git -C "$repository" config user.signingkey "$signing_key"

git -C "$repository" remote set-url origin "$fixture_root/missing.git"
expect_failure \
    fetch-failure \
    "  [FAIL] Remote state: could not fetch origin/main and tags."
git -C "$repository" remote set-url origin "$remote"

printf 'ahead\n' >"$repository/ahead.txt"
git -C "$repository" add ahead.txt
git -C "$repository" commit --quiet -m "Create divergent fixture"

local_commit="$(git -C "$repository" rev-parse HEAD)"
origin_commit="$(git -C "$repository" rev-parse origin/main)"
expect_failure \
    divergent-main \
    "  [FAIL] Source commit: HEAD $local_commit does not match origin/main $origin_commit."

echo "Pre-tag positive and negative cases passed."
