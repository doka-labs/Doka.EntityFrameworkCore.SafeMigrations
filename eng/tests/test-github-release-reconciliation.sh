#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
reconciler="$repo_root/eng/reconcile-github-release.sh"
workflow="$repo_root/.github/workflows/release-candidate.yml"
quality_workflow="$repo_root/.github/workflows/quality-gates.yml"
fixture_root="$(mktemp -d)"
trap 'rm -rf -- "$fixture_root"' EXIT

fake_bin="$fixture_root/bin"
mkdir -p "$fake_bin"

cat >"$fake_bin/gh" <<'FAKE_GH'
#!/usr/bin/env bash

set -euo pipefail

: "${FAKE_GH_STATE:?}"
mkdir -p "$FAKE_GH_STATE"
printf '%s\n' "$*" >>"$FAKE_GH_STATE/commands.log"

release_file="$FAKE_GH_STATE/release.json"

digest() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | cut -d ' ' -f 1
    else
        shasum -a 256 "$1" | cut -d ' ' -f 1
    fi
}

case "${1:-} ${2:-}" in
    "api --paginate")
        if [[ "${FAKE_GH_API_FAILURE:-false}" == true ]]; then
            echo "simulated GitHub API failure" >&2
            exit 1
        fi

        if [[ ! -f "$release_file" ]]; then
            echo '[[]]'
            exit 0
        fi

        visibility_counter_file="$FAKE_GH_STATE/release-visibility-count"
        visibility_counter=0
        if [[ -f "$visibility_counter_file" ]]; then
            visibility_counter="$(<"$visibility_counter_file")"
        fi
        visibility_counter=$((visibility_counter + 1))
        printf '%s\n' "$visibility_counter" >"$visibility_counter_file"

        if ((visibility_counter <= ${FAKE_GH_RELEASE_VISIBILITY_FAILURES:-0})); then
            echo '[[]]'
            exit 0
        fi

        release_json="$(
            jq '{
                  tag_name: .tagName,
                  name: .name,
                  body: .body,
                  draft: .isDraft,
                  immutable: .isImmutable,
                  prerelease: .isPrerelease,
                  assets: .assets
                }' "$release_file"
        )"

        if [[ "${FAKE_GH_DUPLICATE_RELEASE:-false}" == true ]]; then
            jq -n --argjson release "$release_json" \
                '[[{tag_name: "v9.9.9"}], [$release, $release]]'
        else
            jq -n --argjson release "$release_json" \
                '[[{tag_name: "v9.9.9"}], [$release]]'
        fi
        ;;
    "release view")
        echo "Draft discovery must use the paginated REST inventory." >&2
        exit 2
        ;;
    "release create")
        if [[ -f "$release_file" ]]; then
            echo "release already exists" >&2
            exit 1
        fi

        prerelease=false
        release_name=""
        release_body=""
        previous_argument=""
        for argument in "$@"; do
            if [[ "$argument" == --prerelease ]]; then
                prerelease=true
            elif [[ "$previous_argument" == --title ]]; then
                release_name="$argument"
            elif [[ "$previous_argument" == --notes-file ]]; then
                release_body="$(<"$argument")"
            fi

            previous_argument="$argument"
        done

        jq -n \
            --arg tag "$3" \
            --arg name "$release_name" \
            --arg body "$release_body" \
            --argjson prerelease "$prerelease" \
            '{
              tagName: $tag,
              name: $name,
              body: $body,
              isDraft: true,
              isImmutable: false,
              isPrerelease: $prerelease,
              assets: []
            }' >"$release_file"
        echo "https://example.invalid/releases/draft"
        ;;
    "release upload")
        asset="$4"
        asset_name="$(basename "$asset")"
        asset_digest="sha256:$(digest "$asset")"
        temporary_file="$release_file.tmp"

        jq \
            --arg name "$asset_name" \
            --arg digest "$asset_digest" \
            '.assets += [{name: $name, digest: $digest}]' \
            "$release_file" >"$temporary_file"
        mv "$temporary_file" "$release_file"
        echo "uploaded $asset_name"
        ;;
    "release edit")
        temporary_file="$release_file.tmp"
        jq \
            '.isDraft = false | .isImmutable = true' \
            "$release_file" >"$temporary_file"
        mv "$temporary_file" "$release_file"
        echo "https://example.invalid/releases/published"
        ;;
    "release verify")
        counter_file="$FAKE_GH_STATE/release-verify-count"
        counter=0
        if [[ -f "$counter_file" ]]; then
            counter="$(<"$counter_file")"
        fi
        counter=$((counter + 1))
        printf '%s\n' "$counter" >"$counter_file"

        if ((counter <= ${FAKE_GH_RELEASE_VERIFY_FAILURES:-0})); then
            echo "no attestations for tag $3" >&2
            exit 1
        fi

        echo "release attestation verified"
        ;;
    "release verify-asset")
        asset="$4"
        asset_name="$(basename "$asset")"
        release_verify_count="$(<"$FAKE_GH_STATE/release-verify-count")"
        if ((release_verify_count <= ${FAKE_GH_ASSET_VERIFY_FAILURES:-0})); then
            echo "no asset attestation for $asset_name" >&2
            exit 1
        fi

        expected_digest="sha256:$(digest "$asset")"
        actual_digest="$(
            jq -r --arg name "$asset_name" \
                '.assets[] | select(.name == $name) | .digest' \
                "$release_file"
        )"

        if [[ "$actual_digest" != "$expected_digest" ]]; then
            echo "release asset verification failed: $asset_name" >&2
            exit 1
        fi

        echo "release asset verified: $asset_name"
        ;;
    *)
        echo "Unsupported fake gh command: $*" >&2
        exit 2
        ;;
esac
FAKE_GH
chmod +x "$fake_bin/gh"

package_version="10.0.0-rc.3"
release_tag="v$package_version"
release_inventory_command="api --paginate --slurp -H Accept: application/vnd.github+json"
release_inventory_command+=" -H X-GitHub-Api-Version: 2022-11-28"
release_inventory_command+=" /repos/{owner}/{repo}/releases?per_page=100"

create_case() {
    local name="$1"
    local fixture_version="${2:-$package_version}"
    local case_root="$fixture_root/$name"

    mkdir -p \
        "$case_root/artifacts/packages" \
        "$case_root/artifacts/release-provenance" \
        "$case_root/artifacts/sbom/_manifest/spdx_2.2" \
        "$case_root/state"

    for package_id in \
        Doka.EntityFrameworkCore.SafeMigrations \
        Doka.EntityFrameworkCore.SafeMigrations.MySql \
        Doka.EntityFrameworkCore.SafeMigrations.PostgreSql; do
        printf '%s primary\n' "$package_id" \
            >"$case_root/artifacts/packages/$package_id.$fixture_version.nupkg"
        printf '%s symbols\n' "$package_id" \
            >"$case_root/artifacts/packages/$package_id.$fixture_version.snupkg"
    done

    printf 'checksums\n' >"$case_root/artifacts/packages/SHA256SUMS"
    printf '{"spdxVersion":"SPDX-2.2"}\n' \
        >"$case_root/artifacts/sbom/_manifest/spdx_2.2/manifest.spdx.json"
    printf '{"mediaType":"application/vnd.dev.sigstore.bundle.v0.3+json"}\n' \
        >"$case_root/artifacts/release-provenance/release-provenance.intoto.jsonl"
    printf '# Changelog\n\n## [%s] - 2026-08-29\n\nRelease notes.\n\n## [10.0.0-rc.2]\n' \
        "$fixture_version" >"$case_root/CHANGELOG.md"
    printf '\nRelease notes.\n' >"$case_root/release-notes.md"

    printf '%s\n' "$case_root"
}

run_reconciler() {
    local case_root="$1"
    local operation="$2"
    local failures="${3:-0}"
    local run_version="${4:-$package_version}"
    local visibility_failures="${5:-0}"
    local duplicate_release="${6:-false}"
    local asset_failures="${7:-0}"
    local api_failure="${8:-false}"
    local run_tag="v$run_version"

    (
        cd "$case_root"
        PATH="$fake_bin:$PATH" \
        FAKE_GH_STATE="$case_root/state" \
        FAKE_GH_RELEASE_VERIFY_FAILURES="$failures" \
        FAKE_GH_RELEASE_VISIBILITY_FAILURES="$visibility_failures" \
        FAKE_GH_DUPLICATE_RELEASE="$duplicate_release" \
        FAKE_GH_ASSET_VERIFY_FAILURES="$asset_failures" \
        FAKE_GH_API_FAILURE="$api_failure" \
        SAFE_MIGRATIONS_RELEASE_READBACK_ATTEMPTS=3 \
        SAFE_MIGRATIONS_RELEASE_READBACK_DELAY_SECONDS=0 \
            bash "$reconciler" "$operation" "$run_version" "$run_tag"
    )
}

create_remote_draft() {
    local case_root="$1"
    local prerelease="${2:-true}"
    local arguments=(
        release create "$release_tag"
        --draft
        --title "SafeMigrations $package_version"
        --notes-file "$case_root/release-notes.md"
    )

    if [[ "$prerelease" == true ]]; then
        arguments+=(--prerelease)
    fi

    (
        cd "$case_root"
        PATH="$fake_bin:$PATH" FAKE_GH_STATE="$case_root/state" \
            gh "${arguments[@]}" >/dev/null
    )
}

assert_command_order() {
    local file="$1"
    local first_pattern="$2"
    local second_pattern="$3"
    local first_line
    local second_line

    first_line="$(grep -nF -- "$first_pattern" "$file" | head -n 1 | cut -d : -f 1)"
    second_line="$(grep -nF -- "$second_pattern" "$file" | head -n 1 | cut -d : -f 1)"

    if ((first_line >= second_line)); then
        echo "Expected '$first_pattern' before '$second_pattern' in $file." >&2
        exit 1
    fi
}

assert_empty() {
    local file="$1"

    if [[ -s "$file" ]]; then
        echo "Expected an empty file: $file" >&2
        cat "$file" >&2
        exit 1
    fi
}

assert_file() {
    local file="$1"

    if [[ ! -f "$file" ]]; then
        echo "Expected file does not exist: $file" >&2
        exit 1
    fi
}

fresh_case="$(create_case fresh)"
if ! run_reconciler "$fresh_case" stage \
    >"$fresh_case/stage.stdout" \
    2>"$fresh_case/stage.stderr"; then
    cat "$fresh_case/stage.stdout" >&2
    cat "$fresh_case/stage.stderr" >&2
    exit 1
fi
jq -e \
    '.isDraft == true
      and .isImmutable == false
      and .isPrerelease == true
      and (.assets | length) == 9' \
    "$fresh_case/state/release.json" >/dev/null
grep -Fxq "GitHub Release draft is complete and verified." "$fresh_case/stage.stdout"
assert_empty "$fresh_case/stage.stderr"
test "$(grep -c '^release upload ' "$fresh_case/state/commands.log")" -eq 9
test "$(grep -c '^release edit ' "$fresh_case/state/commands.log" || true)" -eq 0
test "$(grep -c '^api --paginate ' "$fresh_case/state/commands.log")" -ge 2
grep -Fq "$release_inventory_command" "$fresh_case/state/commands.log"
test "$(grep -c '^release view ' "$fresh_case/state/commands.log" || true)" -eq 0

run_reconciler "$fresh_case" publish 2 \
    >"$fresh_case/publish.stdout" \
    2>"$fresh_case/publish.stderr"
jq -e \
    '.isDraft == false
      and .isImmutable == true
      and .isPrerelease == true
      and (.assets | length) == 9' \
    "$fresh_case/state/release.json" >/dev/null
grep -Fxq "3" "$fresh_case/state/release-verify-count"
grep -Fq "Waiting for GitHub Release and asset attestations (1/3)..." \
    "$fresh_case/publish.stderr"
grep -Fq "Waiting for GitHub Release and asset attestations (2/3)..." \
    "$fresh_case/publish.stderr"
grep -Fxq "Immutable GitHub Release and every asset attestation are verified." \
    "$fresh_case/publish.stdout"
assert_command_order \
    "$fresh_case/state/commands.log" \
    "release edit $release_tag" \
    "release verify $release_tag"

: >"$fresh_case/state/commands.log"
run_reconciler "$fresh_case" publish \
    >"$fresh_case/retry.stdout" \
    2>"$fresh_case/retry.stderr"
test "$(grep -c '^release edit ' "$fresh_case/state/commands.log" || true)" -eq 0
grep -Fxq "Immutable GitHub Release and every asset attestation are verified." \
    "$fresh_case/retry.stdout"
assert_empty "$fresh_case/retry.stderr"

stable_version="10.0.0"
stable_case="$(create_case stable "$stable_version")"
run_reconciler "$stable_case" stage 0 "$stable_version" \
    >"$stable_case/stage.stdout" \
    2>"$stable_case/stage.stderr"
if [[ ! -f "$stable_case/state/release.json" ]]; then
    cat "$stable_case/stage.stdout" >&2
    cat "$stable_case/stage.stderr" >&2
fi
assert_file "$stable_case/state/release.json"
jq -e \
    '.isDraft == true
      and .isImmutable == false
      and .isPrerelease == false
      and (.assets | length) == 9' \
    "$stable_case/state/release.json" >/dev/null
if grep -Fq -- "--prerelease" "$stable_case/state/commands.log"; then
    echo "Stable draft was incorrectly classified as a prerelease." >&2
    exit 1
fi
run_reconciler "$stable_case" publish 0 "$stable_version" \
    >"$stable_case/publish.stdout" \
    2>"$stable_case/publish.stderr"
grep -Fq -- "--latest" "$stable_case/state/commands.log"
assert_empty "$stable_case/stage.stderr"
assert_empty "$stable_case/publish.stderr"

partial_case="$(create_case partial)"
create_remote_draft "$partial_case"
(
    cd "$partial_case"
    PATH="$fake_bin:$PATH" FAKE_GH_STATE="$partial_case/state" \
        gh release upload "$release_tag" \
        "artifacts/packages/Doka.EntityFrameworkCore.SafeMigrations.$package_version.nupkg" \
        >/dev/null
)
: >"$partial_case/state/commands.log"
run_reconciler "$partial_case" stage \
    >"$partial_case/stage.stdout" \
    2>"$partial_case/stage.stderr"
test "$(grep -c '^release upload ' "$partial_case/state/commands.log")" -eq 8
test "$(grep -c '^release create ' "$partial_case/state/commands.log" || true)" -eq 0
assert_empty "$partial_case/stage.stderr"

asset_visibility_case="$(create_case asset-visibility)"
run_reconciler "$asset_visibility_case" stage >/dev/null
run_reconciler \
    "$asset_visibility_case" publish 0 "$package_version" 0 false 2 \
    >"$asset_visibility_case/publish.stdout" \
    2>"$asset_visibility_case/publish.stderr"
grep -Fxq "3" "$asset_visibility_case/state/release-verify-count"
test "$(grep -c '^release verify-asset ' \
    "$asset_visibility_case/state/commands.log")" -eq 11
grep -Fq "Waiting for GitHub Release and asset attestations (2/3)..." \
    "$asset_visibility_case/publish.stderr"
grep -Fxq "Immutable GitHub Release and every asset attestation are verified." \
    "$asset_visibility_case/publish.stdout"

visibility_case="$(create_case visibility)"
run_reconciler "$visibility_case" stage 0 "$package_version" 2 \
    >"$visibility_case/stage.stdout" \
    2>"$visibility_case/stage.stderr"
grep -Fxq "4" "$visibility_case/state/release-visibility-count"
grep -Fq "Waiting for GitHub Release draft visibility (1/3)..." \
    "$visibility_case/stage.stderr"
grep -Fq "Waiting for GitHub Release draft visibility (2/3)..." \
    "$visibility_case/stage.stderr"
grep -Fxq "GitHub Release draft is complete and verified." \
    "$visibility_case/stage.stdout"

duplicate_case="$(create_case duplicate)"
create_remote_draft "$duplicate_case"
: >"$duplicate_case/state/commands.log"
if run_reconciler "$duplicate_case" stage 0 "$package_version" 0 true \
    >"$duplicate_case/stage.stdout" \
    2>"$duplicate_case/stage.stderr"; then
    echo "Duplicate releases for one tag unexpectedly passed." >&2
    exit 1
fi
grep -Fxq "GitHub returned duplicate releases for tag: $release_tag." \
    "$duplicate_case/stage.stderr"
test "$(grep -c '^release create ' "$duplicate_case/state/commands.log" || true)" -eq 0

api_failure_case="$(create_case api-failure)"
if run_reconciler \
    "$api_failure_case" stage 0 "$package_version" 0 false 0 true \
    >"$api_failure_case/stage.stdout" \
    2>"$api_failure_case/stage.stderr"; then
    echo "GitHub API failure was incorrectly treated as an absent release." >&2
    exit 1
fi
grep -Fxq "simulated GitHub API failure" "$api_failure_case/stage.stderr"
test "$(grep -c '^release create ' "$api_failure_case/state/commands.log" || true)" -eq 0

package_shape_case="$(create_case package-shape)"
mv \
    "$package_shape_case/artifacts/packages/Doka.EntityFrameworkCore.SafeMigrations.$package_version.snupkg" \
    "$package_shape_case/artifacts/packages/Unexpected.$package_version.nupkg"
if run_reconciler "$package_shape_case" stage \
    >"$package_shape_case/stage.stdout" \
    2>"$package_shape_case/stage.stderr"; then
    echo "Invalid primary and symbol package distribution unexpectedly passed." >&2
    exit 1
fi
grep -Fxq "Expected exactly three primary packages and three symbol packages." \
    "$package_shape_case/stage.stderr"
test ! -f "$package_shape_case/state/commands.log"

missing_provenance_case="$(create_case missing-provenance)"
rm "$missing_provenance_case/artifacts/release-provenance/release-provenance.intoto.jsonl"
if run_reconciler "$missing_provenance_case" stage \
    >"$missing_provenance_case/stage.stdout" \
    2>"$missing_provenance_case/stage.stderr"; then
    echo "Missing portable provenance unexpectedly passed." >&2
    exit 1
fi
grep -Fxq \
    "Release asset does not exist: artifacts/release-provenance/release-provenance.intoto.jsonl" \
    "$missing_provenance_case/stage.stderr"
test ! -f "$missing_provenance_case/state/commands.log"

mismatch_case="$(create_case mismatch)"
create_remote_draft "$mismatch_case"
(
    cd "$mismatch_case"
    PATH="$fake_bin:$PATH" FAKE_GH_STATE="$mismatch_case/state" \
        gh release upload "$release_tag" \
        "artifacts/packages/Doka.EntityFrameworkCore.SafeMigrations.$package_version.nupkg" \
        >/dev/null
    printf 'different bytes\n' \
        >"artifacts/packages/Doka.EntityFrameworkCore.SafeMigrations.$package_version.nupkg"
)
if run_reconciler "$mismatch_case" stage \
    >"$mismatch_case/stage.stdout" \
    2>"$mismatch_case/stage.stderr"; then
    echo "Mismatched draft asset unexpectedly passed." >&2
    exit 1
fi
grep -Fxq \
    "GitHub Release asset digest differs: Doka.EntityFrameworkCore.SafeMigrations.$package_version.nupkg." \
    "$mismatch_case/stage.stderr"

provenance_mismatch_case="$(create_case provenance-mismatch)"
create_remote_draft "$provenance_mismatch_case"
(
    cd "$provenance_mismatch_case"
    PATH="$fake_bin:$PATH" FAKE_GH_STATE="$provenance_mismatch_case/state" \
        gh release upload "$release_tag" \
        "artifacts/release-provenance/release-provenance.intoto.jsonl" \
        >/dev/null
    printf '{"different":"bundle"}\n' \
        >"artifacts/release-provenance/release-provenance.intoto.jsonl"
)
if run_reconciler "$provenance_mismatch_case" stage \
    >"$provenance_mismatch_case/stage.stdout" \
    2>"$provenance_mismatch_case/stage.stderr"; then
    echo "Mismatched portable provenance unexpectedly passed." >&2
    exit 1
fi
grep -Fxq \
    "GitHub Release asset digest differs: release-provenance.intoto.jsonl." \
    "$provenance_mismatch_case/stage.stderr"

unexpected_case="$(create_case unexpected)"
create_remote_draft "$unexpected_case"
jq '.assets += [{name: "unexpected.bin", digest: "sha256:00"}]' \
    "$unexpected_case/state/release.json" \
    >"$unexpected_case/state/release.json.tmp"
mv "$unexpected_case/state/release.json.tmp" "$unexpected_case/state/release.json"
if run_reconciler "$unexpected_case" stage \
    >"$unexpected_case/stage.stdout" \
    2>"$unexpected_case/stage.stderr"; then
    echo "Unexpected draft asset unexpectedly passed." >&2
    exit 1
fi
grep -Fxq "GitHub Release contains unexpected assets:" \
    "$unexpected_case/stage.stderr"
grep -Fxq "unexpected.bin" "$unexpected_case/stage.stderr"

classification_case="$(create_case classification)"
create_remote_draft "$classification_case" false
if run_reconciler "$classification_case" stage \
    >"$classification_case/stage.stdout" \
    2>"$classification_case/stage.stderr"; then
    echo "Incorrect release classification unexpectedly passed." >&2
    exit 1
fi
grep -Fxq "GitHub Release metadata, classification, or mutability state differs." \
    "$classification_case/stage.stderr"

title_case="$(create_case title-conflict)"
create_remote_draft "$title_case"
jq '.name = "Different release"' \
    "$title_case/state/release.json" \
    >"$title_case/state/release.json.tmp"
mv "$title_case/state/release.json.tmp" "$title_case/state/release.json"
if run_reconciler "$title_case" stage \
    >"$title_case/stage.stdout" \
    2>"$title_case/stage.stderr"; then
    echo "Conflicting GitHub Release title unexpectedly passed." >&2
    exit 1
fi
grep -Fxq "GitHub Release metadata, classification, or mutability state differs." \
    "$title_case/stage.stderr"

notes_case="$(create_case notes-conflict)"
create_remote_draft "$notes_case"
jq '.body = "Different notes."' \
    "$notes_case/state/release.json" \
    >"$notes_case/state/release.json.tmp"
mv "$notes_case/state/release.json.tmp" "$notes_case/state/release.json"
if run_reconciler "$notes_case" stage \
    >"$notes_case/stage.stdout" \
    2>"$notes_case/stage.stderr"; then
    echo "Conflicting GitHub Release notes unexpectedly passed." >&2
    exit 1
fi
grep -Fxq "GitHub Release metadata, classification, or mutability state differs." \
    "$notes_case/stage.stderr"

timeout_case="$(create_case timeout)"
run_reconciler "$timeout_case" stage >/dev/null
if run_reconciler "$timeout_case" publish 99 \
    >"$timeout_case/publish.stdout" \
    2>"$timeout_case/publish.stderr"; then
    echo "Unavailable release attestation unexpectedly passed." >&2
    exit 1
fi
grep -Fxq "3" "$timeout_case/state/release-verify-count"
grep -Fxq "no attestations for tag $release_tag" "$timeout_case/publish.stderr"
grep -Fxq \
    "Timed out waiting for GitHub Release and asset attestations after 3 attempts." \
    "$timeout_case/publish.stderr"

missing_notes_case="$(create_case missing-notes)"
printf '# Changelog\n\n## [10.0.0-rc.2]\n' >"$missing_notes_case/CHANGELOG.md"
if run_reconciler "$missing_notes_case" stage \
    >"$missing_notes_case/stage.stdout" \
    2>"$missing_notes_case/stage.stderr"; then
    echo "Missing release notes unexpectedly passed." >&2
    exit 1
fi
test ! -f "$missing_notes_case/state/release.json"

assert_command_order \
    "$workflow" \
    "- name: Verify portable SLSA provenance" \
    "- name: Prepare verified GitHub Release draft"
assert_command_order \
    "$workflow" \
    "- name: Prepare verified GitHub Release draft" \
    "- name: Request short-lived NuGet.org key"
assert_command_order \
    "$workflow" \
    "- name: Verify public NuGet packages" \
    "- name: Publish or verify immutable GitHub Release"
grep -Fq "stage \"\$PACKAGE_VERSION\" \"\$RELEASE_TAG\"" "$workflow"
grep -Fq "publish \"\$PACKAGE_VERSION\" \"\$RELEASE_TAG\"" "$workflow"
if grep -Fq "gh release" "$workflow"; then
    echo "Release reconciliation must remain in its tested engineering script." >&2
    exit 1
fi
grep -Fq "bash eng/tests/test-github-release-reconciliation.sh" "$quality_workflow"

echo "GitHub Release reconciliation positive and negative cases passed."
