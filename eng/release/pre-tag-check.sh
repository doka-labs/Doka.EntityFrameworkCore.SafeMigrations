#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 [--version <version> --commit <sha> --run-id <id>]" >&2
    echo "No arguments: prepare before dispatch. All three: verify the qualified waiting run before tagging." >&2
}

package_version=""
candidate_commit=""
run_id=""
prepare_only=false

if (($# == 0)); then
    prepare_only=true
elif (($# != 6)); then
    usage
    exit 2
fi

while (($# > 0)); do
    case "$1" in
        --version)
            [[ -z "$package_version" ]] || { usage; exit 2; }
            package_version="${2:-}"
            shift 2
            ;;
        --commit)
            [[ -z "$candidate_commit" ]] || { usage; exit 2; }
            candidate_commit="${2:-}"
            shift 2
            ;;
        --run-id)
            [[ -z "$run_id" ]] || { usage; exit 2; }
            run_id="${2:-}"
            shift 2
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ "$prepare_only" == false ]] \
    && [[ -z "$package_version" || -z "$candidate_commit" || ! "$run_id" =~ ^[1-9][0-9]*$ ]]; then
    usage
    exit 2
fi

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
allowed_signers="$source_root/eng/release/allowed-signers"
release_tag="v$package_version"
cd "$source_root"

if [[ "$prepare_only" == true ]]; then
    candidate_commit="$(git rev-parse --verify HEAD)"
else
    "$source_root/eng/release/validate-version.sh" "$package_version" >/dev/null
fi

if [[ -n "$(git -C "$source_root" status --porcelain --untracked-files=all)" ]]; then
    echo "The release worktree must be clean before creating a tag." >&2
    exit 1
fi

candidate_commit="$(git -C "$source_root" rev-parse --verify --end-of-options "${candidate_commit}^{commit}")"
"$source_root/eng/release/verify-main-source.sh" "$candidate_commit" >/dev/null

if [[ "$prepare_only" == true ]]; then
    if [[ "$(git symbolic-ref --quiet --short HEAD)" != main
        || "$(git rev-parse refs/remotes/origin/main)" != "$candidate_commit" ]]; then
        echo "Preparation requires a clean checkout of current origin/main on local main." >&2
        exit 1
    fi
else
    if git -C "$source_root" show-ref --verify --quiet "refs/tags/$release_tag"; then
        echo "Release tag already exists: $release_tag" >&2
        exit 1
    fi

    remote_tag="$(git -C "$source_root" ls-remote --tags origin "refs/tags/$release_tag")"
    if [[ -n "$remote_tag" ]]; then
        echo "Remote release tag already exists: $release_tag" >&2
        exit 1
    fi
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
registered_keys="$(gh api "user/ssh_signing_keys?per_page=100" --paginate | jq -s 'add')"
if ! jq -e \
    --arg key "$key_type $key_data" \
    'any(.[]; .key == $key)' \
    <<< "$registered_keys" >/dev/null; then
    echo "The authorized SSH signing key is not registered as a GitHub signing key." >&2
    exit 1
fi

if [[ "$prepare_only" == true ]]; then
    echo "Commit $candidate_commit is ready for untagged qualification in $repository."
    echo "Start Release candidate on main with the reviewed version; do not create a tag yet."
    exit 0
fi

run="$(gh api "repos/$repository/actions/runs/$run_id")"
if ! jq -e \
    --arg commit "$candidate_commit" \
    --argjson run_id "$run_id" \
    '.id == $run_id
        and .event == "workflow_dispatch"
        and (.path == ".github/workflows/release-candidate.yml"
            or .path == ".github/workflows/release-candidate.yml@main")
        and .head_branch == "main"
        and .head_sha == $commit
        and (.status == "waiting" or .status == "in_progress")
        and .conclusion == null
        and (.run_attempt | type == "number" and . > 0 and floor == .)' \
    <<< "$run" >/dev/null; then
    echo "Workflow run does not identify the waiting qualified main commit." >&2
    exit 1
fi

run_attempt="$(jq -r '.run_attempt' <<< "$run")"
package_producer="Full reversible qualification / Core, performance, packages, and SBOM"
attestation_producer="Attest qualified candidate"
publish_job="Verify tag, publish, and read back"
job_pages="$(gh api "repos/$repository/actions/runs/$run_id/jobs?filter=all&per_page=100" --paginate)"
if ! jobs="$(jq -cse \
    --arg commit "$candidate_commit" \
    --argjson run_id "$run_id" \
    --argjson attempt "$run_attempt" '
    [.[].jobs[]] as $executions
    | if ($executions | length) > 0 and all($executions[];
        .run_id == $run_id and .head_sha == $commit
        and (.name | type == "string" and length > 0)
        and (.run_attempt | type == "number" and . > 0 and floor == . and . <= $attempt))
      then $executions | group_by(.name) | map(
        (map(.run_attempt) | max) as $latest
        | [.[] | select(.run_attempt == $latest)]
        | if length == 1 then .[0] else error("Ambiguous latest job execution.") end)
      else error("Invalid workflow job identity or attempt.") end
' <<< "$job_pages")"; then
    echo "Workflow jobs are not in the required qualified-and-waiting state." >&2
    exit 1
fi

if ! jq -e \
    --arg package "$package_producer" \
    --arg attestations "$attestation_producer" \
    --arg publish "$publish_job" \
    --argjson attempt "$run_attempt" '
    [.[] | select(.name == $publish)] as $publish
    | ([.[] | select(.name == $package)] | length) == 1
        and ([.[] | select(.name == $attestations)] | length) == 1
        and ($publish | length) == 1
        and $publish[0].run_attempt == $attempt
        and $publish[0].status == "waiting"
        and $publish[0].conclusion == null
        and all(.[] | select(.name != $publish[0].name); .status == "completed" and .conclusion == "success")
' <<< "$jobs" >/dev/null; then
    echo "Workflow jobs are not in the required qualified-and-waiting state." >&2
    exit 1
fi

package_attempt="$(jq -r --arg name "$package_producer" \
    '.[] | select(.name == $name) | .run_attempt' <<< "$jobs")"
attestation_attempt="$(jq -r --arg name "$attestation_producer" \
    '.[] | select(.name == $name) | .run_attempt' <<< "$jobs")"
artifacts="$(gh api "repos/$repository/actions/runs/$run_id/artifacts?per_page=100" --paginate)"
qualified_artifact="safe-migrations-release-$package_version-$package_attempt"
attestation_artifact="safe-migrations-attestations-$package_version-$attestation_attempt"
if ! jq -se \
    --arg qualified "$qualified_artifact" \
    --arg attestations "$attestation_artifact" \
    --arg commit "$candidate_commit" \
    --argjson run_id "$run_id" '
    [.[].artifacts[]] as $artifacts
    | all([$qualified, $attestations][];
        . as $name
        | [$artifacts[] | select(.name == $name)] as $matches
        | ($matches | length) == 1
            and $matches[0].expired == false
            and $matches[0].workflow_run.id == $run_id
            and $matches[0].workflow_run.head_sha == $commit
            and $matches[0].workflow_run.head_branch == "main")' \
    <<< "$artifacts" >/dev/null; then
    echo "Workflow run does not expose the exact qualified package and attestation artifacts." >&2
    exit 1
fi

echo "Commit $candidate_commit is ready for signed tag $release_tag from workflow run $run_id."
