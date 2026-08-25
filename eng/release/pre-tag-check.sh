#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --version <version> --commit <sha> --run-id <id>" >&2
}

package_version=""
candidate_commit=""
run_id=""

while (($# > 0)); do
    case "$1" in
        --version)
            package_version="${2:-}"
            shift 2
            ;;
        --commit)
            candidate_commit="${2:-}"
            shift 2
            ;;
        --run-id)
            run_id="${2:-}"
            shift 2
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ -z "$package_version" || -z "$candidate_commit" || ! "$run_id" =~ ^[1-9][0-9]*$ ]]; then
    usage
    exit 2
fi

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
allowed_signers="$source_root/eng/release/allowed-signers"
release_tag="v$package_version"

"$source_root/eng/release/validate-version.sh" "$package_version" >/dev/null

if [[ -n "$(git -C "$source_root" status --porcelain --untracked-files=all)" ]]; then
    echo "The release worktree must be clean before creating a tag." >&2
    exit 1
fi

candidate_commit="$(git -C "$source_root" rev-parse "$candidate_commit")"
if [[ "$(git -C "$source_root" rev-parse HEAD)" != "$candidate_commit" ]]; then
    echo "The local checkout does not identify the qualified commit." >&2
    exit 1
fi

git -C "$source_root" fetch --quiet origin \
    +refs/heads/main:refs/remotes/origin/main \
    '+refs/tags/*:refs/tags/*'

if [[ "$(git -C "$source_root" rev-parse refs/remotes/origin/main)" != "$candidate_commit" ]]; then
    echo "The qualified commit is no longer current main." >&2
    exit 1
fi

if git -C "$source_root" show-ref --verify --quiet "refs/tags/$release_tag"; then
    echo "Release tag already exists: $release_tag" >&2
    exit 1
fi

remote_tag="$(git -C "$source_root" ls-remote --tags origin "refs/tags/$release_tag")"
if [[ -n "$remote_tag" ]]; then
    echo "Remote release tag already exists: $release_tag" >&2
    exit 1
fi

if [[ "$(git -C "$source_root" config --get gpg.format)" != "ssh"
    || "$(git -C "$source_root" config --bool --get tag.gpgSign)" != "true" ]]; then
    echo "Git must use SSH signing with tag.gpgSign enabled." >&2
    exit 1
fi

signer_principal="$(git -C "$source_root" config --get user.email)"
signing_key="$(git -C "$source_root" config --path --get user.signingkey)"
if [[ -z "$signer_principal" || ! -f "$signing_key" ]]; then
    echo "The configured SSH signing identity is incomplete." >&2
    exit 1
fi

read -r key_type key_data _ < "$signing_key"
if ! awk \
    -v principal="$signer_principal" \
    -v key_type="$key_type" \
    -v key_data="$key_data" \
    '$1 == principal && $2 == key_type && $3 == key_data { found = 1 } END { exit !found }' \
    "$allowed_signers"; then
    echo "The configured SSH signing key is not authorized by $allowed_signers." >&2
    exit 1
fi

repository="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"
registered_keys="$(gh api "user/ssh_signing_keys?per_page=100")"
if ! jq -e \
    --arg key "$key_type $key_data" \
    'any(.[]; .key == $key)' \
    <<< "$registered_keys" >/dev/null; then
    echo "The authorized SSH signing key is not registered as a GitHub signing key." >&2
    exit 1
fi

run="$(gh api "repos/$repository/actions/runs/$run_id")"
if ! jq -e \
    --arg commit "$candidate_commit" \
    '.event == "workflow_dispatch"
        and .path == ".github/workflows/release-candidate.yml"
        and .head_branch == "main"
        and .head_sha == $commit
        and (.status == "waiting" or .status == "in_progress")
        and .conclusion == null
        and (.run_attempt | type == "number")' \
    <<< "$run" >/dev/null; then
    echo "Workflow run does not identify the waiting qualified main commit." >&2
    exit 1
fi

run_attempt="$(jq -r '.run_attempt' <<< "$run")"
jobs="$(gh api "repos/$repository/actions/runs/$run_id/jobs?filter=latest&per_page=100")"
if ! jq -e '
    [.jobs[] | select(.name == "Verify tag, publish, and read back")] as $publish
    | ($publish | length) == 1
        and $publish[0].status == "waiting"
        and $publish[0].conclusion == null
        and all(.jobs[] | select(.name != "Verify tag, publish, and read back"); .conclusion == "success")
' <<< "$jobs" >/dev/null; then
    echo "Workflow jobs are not in the required qualified-and-waiting state." >&2
    exit 1
fi

artifacts="$(gh api "repos/$repository/actions/runs/$run_id/artifacts?per_page=100")"
qualified_artifact="safe-migrations-release-$package_version-$run_attempt"
attestation_artifact="safe-migrations-attestations-$package_version-$run_attempt"
if ! jq -e \
    --arg qualified "$qualified_artifact" \
    --arg attestations "$attestation_artifact" \
    '([.artifacts[] | select(.name == $qualified and .expired == false)] | length) == 1
        and ([.artifacts[] | select(.name == $attestations and .expired == false)] | length) == 1' \
    <<< "$artifacts" >/dev/null; then
    echo "Workflow run does not expose the exact qualified package and attestation artifacts." >&2
    exit 1
fi

echo "Commit $candidate_commit is ready for signed tag $release_tag from workflow run $run_id."
