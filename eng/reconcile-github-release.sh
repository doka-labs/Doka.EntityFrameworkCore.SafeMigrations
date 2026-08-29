#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 <stage|publish> <package-version> <release-tag>" >&2
}

if (($# != 3)); then
    usage
    exit 2
fi

operation="$1"
package_version="$2"
release_tag="$3"

if [[ "$operation" != stage && "$operation" != publish ]]; then
    usage
    exit 2
fi

if [[ -z "$package_version" || "$release_tag" != "v$package_version" ]]; then
    echo "Release tag must equal v<package-version>." >&2
    exit 2
fi

readback_attempts="${SAFE_MIGRATIONS_RELEASE_READBACK_ATTEMPTS:-30}"
readback_delay_seconds="${SAFE_MIGRATIONS_RELEASE_READBACK_DELAY_SECONDS:-10}"

if [[ ! "$readback_attempts" =~ ^[1-9][0-9]*$ ]]; then
    echo "SAFE_MIGRATIONS_RELEASE_READBACK_ATTEMPTS must be a positive integer." >&2
    exit 2
fi

if [[ ! "$readback_delay_seconds" =~ ^[0-9]+$ ]]; then
    echo "SAFE_MIGRATIONS_RELEASE_READBACK_DELAY_SECONDS must be a non-negative integer." >&2
    exit 2
fi

shopt -s nullglob
primary_packages=(artifacts/packages/*.nupkg)
symbol_packages=(artifacts/packages/*.snupkg)
shopt -u nullglob

if ((${#primary_packages[@]} != 3 || ${#symbol_packages[@]} != 3)); then
    echo "Expected exactly three primary packages and three symbol packages." >&2
    exit 1
fi

assets=(
    "${primary_packages[@]}"
    "${symbol_packages[@]}"
)
assets+=(
    artifacts/packages/SHA256SUMS
    artifacts/sbom/_manifest/spdx_2.2/manifest.spdx.json
)

for asset in "${assets[@]}"; do
    if [[ ! -f "$asset" ]]; then
        echo "Release asset does not exist: $asset" >&2
        exit 1
    fi
done

expected_prerelease=false
expected_release_title="SafeMigrations $package_version"
publication_arguments=(--latest)
if [[ "$package_version" == *-* ]]; then
    expected_prerelease=true
    publication_arguments=(--prerelease --latest=false)
fi

expected_assets="$({
    for asset in "${assets[@]}"; do
        basename "$asset"
    done
} | LC_ALL=C sort)"

release_json=""
release_notes_file=""
expected_release_notes=""

cleanup() {
    if [[ -n "$release_notes_file" ]]; then
        rm -f -- "$release_notes_file"
    fi
}
trap cleanup EXIT

asset_digest() {
    local asset="$1"
    local digest

    if command -v sha256sum >/dev/null 2>&1; then
        digest="$(sha256sum "$asset" | cut -d ' ' -f 1)"
    else
        digest="$(shasum -a 256 "$asset" | cut -d ' ' -f 1)"
    fi

    printf 'sha256:%s\n' "$digest"
}

prepare_release_notes() {
    local temporary_root

    if [[ -n "$release_notes_file" ]]; then
        return
    fi

    temporary_root="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
    release_notes_file="$(mktemp "$temporary_root/safemigrations-release-notes.XXXXXX")"

    awk -v heading="## [$package_version]" '
        index($0, heading) == 1 { found = 1; next }
        found && /^## \[/ { exit }
        found { print }
        END { if (!found) exit 1 }
    ' CHANGELOG.md >"$release_notes_file"
    expected_release_notes="$(<"$release_notes_file")"
}

read_release() {
    local inventory
    local match_count

    # The tag endpoint returns published releases only. The authenticated,
    # paginated inventory is required to resume a draft after an interruption.
    inventory="$(
        gh api \
            --paginate \
            --slurp \
            -H "Accept: application/vnd.github+json" \
            -H "X-GitHub-Api-Version: 2022-11-28" \
            "/repos/{owner}/{repo}/releases?per_page=100"
    )"

    if ! jq -e \
        'type == "array" and all(.[]; type == "array")' \
        <<<"$inventory" >/dev/null; then
        echo "GitHub returned an invalid paginated release inventory." >&2
        return 1
    fi

    match_count="$(
        jq --arg tag "$release_tag" \
            '[.[][] | select(.tag_name == $tag)] | length' \
            <<<"$inventory"
    )"

    if [[ "$match_count" == 0 ]]; then
        return 3
    fi

    if [[ "$match_count" != 1 ]]; then
        echo "GitHub returned duplicate releases for tag: $release_tag." >&2
        return 1
    fi

    jq -c --arg tag "$release_tag" '
        [.[][] | select(.tag_name == $tag)][0]
        | {
            tagName: .tag_name,
            name: .name,
            body: .body,
            isDraft: .draft,
            isImmutable: .immutable,
            isPrerelease: .prerelease,
            assets: [.assets[] | { name: .name, digest: .digest }]
          }
    ' <<<"$inventory"
}

wait_for_release_visibility() {
    for ((attempt = 1; attempt <= readback_attempts; attempt++)); do
        local read_status=0

        release_json="$(read_release)" || read_status=$?
        if ((read_status == 0)); then
            return 0
        fi

        if ((read_status != 3)); then
            return "$read_status"
        fi

        if ((attempt < readback_attempts)); then
            echo "Waiting for GitHub Release draft visibility ($attempt/$readback_attempts)..." >&2
            sleep "$readback_delay_seconds"
        fi
    done

    echo "Timed out waiting for GitHub Release draft visibility after $readback_attempts attempts." >&2
    return 1
}

release_identity_is_valid() {
    local actual_release_notes

    actual_release_notes="$(jq -r '.body // ""' <<<"$release_json")"
    if jq -e \
        --arg tag "$release_tag" \
        --arg name "$expected_release_title" \
        --argjson prerelease "$expected_prerelease" \
        '.tagName == $tag
          and .name == $name
          and .isPrerelease == $prerelease
          and ((.isDraft == true and .isImmutable == false)
            or (.isDraft == false and .isImmutable == true))' \
        <<<"$release_json" >/dev/null \
        && [[ "$actual_release_notes" == "$expected_release_notes" ]]; then
        return 0
    fi

    echo "GitHub Release metadata, classification, or mutability state differs." >&2
    return 1
}

assert_existing_assets_match() {
    local actual_assets
    local unexpected_assets

    actual_assets="$(jq -r '.assets[].name' <<<"$release_json" | LC_ALL=C sort)"
    unexpected_assets="$(comm -13 \
        <(printf '%s\n' "$expected_assets") \
        <(printf '%s\n' "$actual_assets"))"
    if [[ -n "$unexpected_assets" ]]; then
        echo "GitHub Release contains unexpected assets:" >&2
        printf '%s\n' "$unexpected_assets" >&2
        exit 1
    fi

    for asset in "${assets[@]}"; do
        local asset_name
        local expected_digest
        local actual_digest

        asset_name="$(basename "$asset")"
        expected_digest="$(asset_digest "$asset")"
        actual_digest="$(
            jq -r --arg name "$asset_name" \
                '[.assets[] | select(.name == $name) | .digest]
                 | if length == 0 then ""
                   elif length == 1 then .[0]
                   else error("duplicate release asset")
                   end' \
                <<<"$release_json"
        )"

        if [[ -n "$actual_digest" && "$actual_digest" != "$expected_digest" ]]; then
            echo "GitHub Release asset digest differs: $asset_name." >&2
            exit 1
        fi
    done
}

release_state_and_assets_match() {
    local expected_draft="$1"
    local expected_immutable="$2"
    local actual_assets

    if ! release_json="$(read_release 2>/dev/null)"; then
        return 1
    fi

    if ! jq -e \
        --arg tag "$release_tag" \
        --argjson draft "$expected_draft" \
        --argjson immutable "$expected_immutable" \
        --argjson prerelease "$expected_prerelease" \
        '.tagName == $tag
          and .isDraft == $draft
          and .isImmutable == $immutable
          and .isPrerelease == $prerelease' \
        <<<"$release_json" >/dev/null; then
        return 1
    fi

    actual_assets="$(jq -r '.assets[].name' <<<"$release_json" | LC_ALL=C sort)"
    if [[ "$actual_assets" != "$expected_assets" ]]; then
        return 1
    fi

    for asset in "${assets[@]}"; do
        local asset_name
        local expected_digest
        local actual_digest

        asset_name="$(basename "$asset")"
        expected_digest="$(asset_digest "$asset")"
        actual_digest="$(
            jq -r --arg name "$asset_name" \
                '.assets[] | select(.name == $name) | .digest' \
                <<<"$release_json"
        )"

        if [[ "$actual_digest" != "$expected_digest" ]]; then
            return 1
        fi
    done
}

wait_for_release_state() {
    local description="$1"
    local expected_draft="$2"
    local expected_immutable="$3"

    for ((attempt = 1; attempt <= readback_attempts; attempt++)); do
        if release_state_and_assets_match "$expected_draft" "$expected_immutable"; then
            return 0
        fi

        if ((attempt < readback_attempts)); then
            echo "Waiting for $description ($attempt/$readback_attempts)..." >&2
            sleep "$readback_delay_seconds"
        fi
    done

    echo "Timed out waiting for $description after $readback_attempts attempts." >&2
    return 1
}

verify_release_attestations() {
    local verification_output=""

    # GitHub creates the immutable-release attestation asynchronously. A
    # successful publish response therefore does not imply immediate readback.
    for ((attempt = 1; attempt <= readback_attempts; attempt++)); do
        local verified=true

        if ! verification_output="$(gh release verify "$release_tag" 2>&1)"; then
            verified=false
        else
            for asset in "${assets[@]}"; do
                local asset_output

                if ! asset_output="$(
                    gh release verify-asset "$release_tag" "$asset" 2>&1
                )"; then
                    verification_output="$asset_output"
                    verified=false
                    break
                fi

                verification_output+=$'\n'"$asset_output"
            done
        fi

        if [[ "$verified" == true ]]; then
            printf '%s\n' "$verification_output"
            return 0
        fi

        if ((attempt < readback_attempts)); then
            echo "Waiting for GitHub Release and asset attestations ($attempt/$readback_attempts)..." >&2
            sleep "$readback_delay_seconds"
        fi
    done

    printf '%s\n' "$verification_output" >&2
    echo "Timed out waiting for GitHub Release and asset attestations after $readback_attempts attempts." >&2
    return 1
}

stage_release() {
    local create_arguments
    local read_status=0

    prepare_release_notes

    release_json="$(read_release)" || read_status=$?
    if ((read_status == 3)); then
        create_arguments=(
            release create "$release_tag"
            --draft
            --verify-tag
            --title "$expected_release_title"
            --notes-file "$release_notes_file"
        )
        if [[ "$expected_prerelease" == true ]]; then
            create_arguments+=(--prerelease)
        fi

        gh "${create_arguments[@]}"
        wait_for_release_visibility
    elif ((read_status != 0)); then
        return "$read_status"
    fi

    release_identity_is_valid
    assert_existing_assets_match

    if [[ "$(jq -r '.isDraft' <<<"$release_json")" == false ]]; then
        wait_for_release_state "existing immutable GitHub Release" false true
        echo "Matching immutable GitHub Release already exists."
        return 0
    fi

    for asset in "${assets[@]}"; do
        local asset_name
        local actual_digest

        asset_name="$(basename "$asset")"
        actual_digest="$(
            jq -r --arg name "$asset_name" \
                '.assets[] | select(.name == $name) | .digest' \
                <<<"$release_json"
        )"

        if [[ -z "$actual_digest" ]]; then
            gh release upload "$release_tag" "$asset"
        fi
    done

    wait_for_release_state "complete GitHub Release draft" true false
    echo "GitHub Release draft is complete and verified."
}

publish_release() {
    prepare_release_notes
    release_json="$(read_release)"
    release_identity_is_valid
    assert_existing_assets_match

    if [[ "$(jq -r '.isDraft' <<<"$release_json")" == true ]]; then
        wait_for_release_state "complete GitHub Release draft" true false
        gh release edit "$release_tag" \
            --draft=false \
            "${publication_arguments[@]}"
    fi

    wait_for_release_state "published immutable GitHub Release" false true
    verify_release_attestations

    echo "Immutable GitHub Release and every asset attestation are verified."
}

case "$operation" in
    stage)
        stage_release
        ;;
    publish)
        publish_release
        ;;
esac
