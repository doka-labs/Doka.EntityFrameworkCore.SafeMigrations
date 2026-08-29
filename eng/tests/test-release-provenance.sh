#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
provenance="$repo_root/eng/release-provenance.sh"
workflow="$repo_root/.github/workflows/release-candidate.yml"
quality_workflow="$repo_root/.github/workflows/quality-gates.yml"
fixture_root="$(mktemp -d)"
trap 'rm -rf -- "$fixture_root"' EXIT

sha256_file() {
    local file="$1"

    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file" | cut -d ' ' -f 1
    else
        shasum -a 256 "$file" | cut -d ' ' -f 1
    fi
}

create_bundle() {
    local output="$1"
    shift

    local records="$fixture_root/subjects.jsonl"
    local subjects="$fixture_root/subjects.json"
    local statement="$fixture_root/statement.json"
    local payload

    : >"$records"
    for subject in "$@"; do
        jq -cn \
            --arg name "$(basename -- "$subject")" \
            --arg sha256 "$(sha256_file "$subject")" \
            '{name: $name, digest: {sha256: $sha256}}' >>"$records"
    done

    jq -cs '.' "$records" >"$subjects"
    jq -cn \
        --argjson subjects "$(<"$subjects")" \
        '{
          _type: "https://in-toto.io/Statement/v1",
          subject: $subjects,
          predicateType: "https://slsa.dev/provenance/v1",
          predicate: {buildDefinition: {}, runDetails: {}}
        }' >"$statement"
    payload="$(jq -c . "$statement" | jq -sRr @base64)"

    jq -cn \
        --arg payload "$payload" \
        '{
          mediaType: "application/vnd.dev.sigstore.bundle.v0.3+json",
          verificationMaterial: {certificate: {rawBytes: "AA=="}},
          dsseEnvelope: {
            payloadType: "application/vnd.in-toto+json",
            payload: $payload,
            signatures: [{sig: "AA=="}]
          }
        }' >"$output"
}

expect_failure() {
    local name="$1"
    local expected_message="$2"
    shift 2

    if "$@" >"$fixture_root/$name.stdout" 2>"$fixture_root/$name.stderr"; then
        echo "Provenance case '$name' unexpectedly passed." >&2
        exit 1
    fi

    grep -Fq "$expected_message" "$fixture_root/$name.stderr"
}

subject_root="$fixture_root/subjects"
mkdir -p "$subject_root"
package="$subject_root/SafeMigrations.10.0.1.nupkg"
symbols="$subject_root/SafeMigrations.10.0.1.snupkg"
checksums="$subject_root/SHA256SUMS"
sbom="$subject_root/manifest.spdx.json"
printf 'package\n' >"$package"
printf 'symbols\n' >"$symbols"
printf 'checksums\n' >"$checksums"
printf '{"spdxVersion":"SPDX-2.2"}\n' >"$sbom"
subjects=("$package" "$symbols" "$checksums" "$sbom")

source_bundle="$fixture_root/attestation.json"
portable_bundle="$fixture_root/release-provenance.intoto.jsonl"
create_bundle "$source_bundle" "${subjects[@]}"

bash "$provenance" materialize \
    --bundle "$source_bundle" \
    --output "$portable_bundle" \
    --subject "$package" \
    --subject "$symbols" \
    --subject "$checksums" \
    --subject "$sbom" >"$fixture_root/materialize.stdout"

test "$(awk 'NF { count++ } END { print count + 0 }' "$portable_bundle")" -eq 1
jq -e 'type == "object"' "$portable_bundle" >/dev/null
grep -Fxq \
    "Portable SLSA provenance materialized and verified: $portable_bundle" \
    "$fixture_root/materialize.stdout"

bash "$provenance" verify \
    --bundle "$portable_bundle" \
    --subject "$package" \
    --subject "$symbols" \
    --subject "$checksums" \
    --subject "$sbom" >"$fixture_root/verify.stdout"
grep -Fxq "Portable SLSA provenance verified: $portable_bundle" \
    "$fixture_root/verify.stdout"

fake_bin="$fixture_root/bin"
mkdir -p "$fake_bin"
cat >"$fake_bin/gh" <<'FAKE_GH'
#!/usr/bin/env bash

set -euo pipefail

: "${FAKE_GH_LOG:?}"
printf '%s\n' "$*" >>"$FAKE_GH_LOG"

if [[ -n "${FAKE_GH_FAIL_SUBJECT:-}" \
    && "$(basename -- "${3:-}")" == "$FAKE_GH_FAIL_SUBJECT" ]]; then
    echo "simulated attestation verification failure: $FAKE_GH_FAIL_SUBJECT" >&2
    exit 1
fi

echo "verified ${3:-}"
FAKE_GH
chmod +x "$fake_bin/gh"

repository="doka-labs/Doka.EntityFrameworkCore.SafeMigrations"
signer_workflow="$repository/.github/workflows/release-candidate.yml"
commit="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
github_log="$fixture_root/github-commands.log"

PATH="$fake_bin:$PATH" FAKE_GH_LOG="$github_log" \
    bash "$provenance" verify-github \
        --bundle "$portable_bundle" \
        --repository "$repository" \
        --signer-workflow "$signer_workflow" \
        --signer-digest "$commit" \
        --source-ref refs/heads/main \
        --source-digest "$commit" \
        --subject "$package" \
        --subject "$symbols" \
        --subject "$checksums" \
        --subject "$sbom" >"$fixture_root/verify-github.stdout"

test "$(wc -l <"$github_log" | tr -d ' ')" -eq 4
grep -Fxq \
    "attestation verify $package --bundle $portable_bundle --repo $repository --signer-workflow $signer_workflow --signer-digest $commit --source-ref refs/heads/main --source-digest $commit --deny-self-hosted-runners" \
    "$github_log"
grep -Fxq \
    "Portable SLSA provenance and GitHub identity verified: $portable_bundle" \
    "$fixture_root/verify-github.stdout"

: >"$github_log"
expect_failure \
    github-verification \
    "simulated attestation verification failure: $(basename -- "$symbols")" \
    env \
        PATH="$fake_bin:$PATH" \
        FAKE_GH_LOG="$github_log" \
        FAKE_GH_FAIL_SUBJECT="$(basename -- "$symbols")" \
        bash "$provenance" verify-github \
            --bundle "$portable_bundle" \
            --repository "$repository" \
            --signer-workflow "$signer_workflow" \
            --signer-digest "$commit" \
            --source-ref refs/heads/main \
            --source-digest "$commit" \
            --subject "$package" \
            --subject "$symbols" \
            --subject "$checksums" \
            --subject "$sbom"
test "$(wc -l <"$github_log" | tr -d ' ')" -eq 2

expect_failure \
    missing-github-identity \
    "Usage:" \
    bash "$provenance" verify-github \
        --bundle "$portable_bundle" \
        --repository "$repository" \
        --signer-workflow "$signer_workflow" \
        --signer-digest "$commit" \
        --source-ref refs/heads/main \
        --subject "$package"

missing_subject="$subject_root/missing.nupkg"
printf 'missing\n' >"$missing_subject"
expect_failure \
    missing-subject \
    "SLSA provenance subject inventory does not match the selected release assets." \
    bash "$provenance" verify \
        --bundle "$portable_bundle" \
        --subject "$package" \
        --subject "$symbols" \
        --subject "$checksums" \
        --subject "$sbom" \
        --subject "$missing_subject"

wrong_digest="$fixture_root/wrong-digest.json"
jq '
    .dsseEnvelope.payload = (
      .dsseEnvelope.payload
      | @base64d
      | fromjson
      | .subject[0].digest.sha256 = ("0" * 64)
      | tojson
      | @base64)' \
    "$source_bundle" >"$wrong_digest"
expect_failure \
    wrong-digest \
    "SLSA provenance subject inventory does not match the selected release assets." \
    bash "$provenance" materialize \
        --bundle "$wrong_digest" \
        --output "$fixture_root/wrong/release-provenance.intoto.jsonl" \
        --subject "$package" \
        --subject "$symbols" \
        --subject "$checksums" \
        --subject "$sbom"

non_slsa="$fixture_root/non-slsa.json"
jq '
    .dsseEnvelope.payload = (
      .dsseEnvelope.payload
      | @base64d
      | fromjson
      | .predicateType = "https://in-toto.io/attestation/release/v0.2"
      | tojson
      | @base64)' \
    "$source_bundle" >"$non_slsa"
expect_failure \
    non-slsa \
    "Bundle does not contain SLSA build provenance." \
    bash "$provenance" materialize \
        --bundle "$non_slsa" \
        --output "$fixture_root/non-slsa/release-provenance.intoto.jsonl" \
        --subject "$package"

duplicate_subject="$fixture_root/duplicate-subject.json"
jq '
    .dsseEnvelope.payload = (
      .dsseEnvelope.payload
      | @base64d
      | fromjson
      | .subject += [.subject[0]]
      | tojson
      | @base64)' \
    "$source_bundle" >"$duplicate_subject"
expect_failure \
    duplicate-subject \
    "SLSA provenance subject inventory is invalid." \
    bash "$provenance" materialize \
        --bundle "$duplicate_subject" \
        --output "$fixture_root/duplicate/release-provenance.intoto.jsonl" \
        --subject "$package"

unexpected_subject="$fixture_root/unexpected-subject.json"
jq '
    .dsseEnvelope.payload = (
      .dsseEnvelope.payload
      | @base64d
      | fromjson
      | .subject += [{name: "unexpected.bin", digest: {sha256: ("0" * 64)}}]
      | tojson
      | @base64)' \
    "$source_bundle" >"$unexpected_subject"
expect_failure \
    unexpected-subject \
    "SLSA provenance subject inventory does not match the selected release assets." \
    bash "$provenance" materialize \
        --bundle "$unexpected_subject" \
        --output "$fixture_root/unexpected/release-provenance.intoto.jsonl" \
        --subject "$package" \
        --subject "$symbols" \
        --subject "$checksums" \
        --subject "$sbom"

invalid_subject_name="$fixture_root/invalid-subject-name.json"
jq '
    .dsseEnvelope.payload = (
      .dsseEnvelope.payload
      | @base64d
      | fromjson
      | .subject[0].name = "nested/package.nupkg"
      | tojson
      | @base64)' \
    "$source_bundle" >"$invalid_subject_name"
expect_failure \
    invalid-subject-name \
    "SLSA provenance subject inventory is invalid." \
    bash "$provenance" materialize \
        --bundle "$invalid_subject_name" \
        --output "$fixture_root/invalid-name/release-provenance.intoto.jsonl" \
        --subject "$package"

invalid_media_type="$fixture_root/invalid-media-type.json"
jq '.mediaType = "application/json"' "$source_bundle" >"$invalid_media_type"
expect_failure \
    invalid-media-type \
    "Sigstore bundle envelope is invalid." \
    bash "$provenance" materialize \
        --bundle "$invalid_media_type" \
        --output "$fixture_root/media/release-provenance.intoto.jsonl" \
        --subject "$package"

missing_verification_material="$fixture_root/missing-verification-material.json"
jq '.verificationMaterial = {}' "$source_bundle" >"$missing_verification_material"
expect_failure \
    missing-verification-material \
    "Sigstore bundle envelope is invalid." \
    bash "$provenance" materialize \
        --bundle "$missing_verification_material" \
        --output "$fixture_root/material/release-provenance.intoto.jsonl" \
        --subject "$package"

missing_signature="$fixture_root/missing-signature.json"
jq '.dsseEnvelope.signatures = []' "$source_bundle" >"$missing_signature"
expect_failure \
    missing-signature \
    "Sigstore bundle envelope is invalid." \
    bash "$provenance" materialize \
        --bundle "$missing_signature" \
        --output "$fixture_root/signature/release-provenance.intoto.jsonl" \
        --subject "$package"

invalid_payload="$fixture_root/invalid-payload.json"
jq '.dsseEnvelope.payload = "%%%"' "$source_bundle" >"$invalid_payload"
expect_failure \
    invalid-payload \
    "Sigstore bundle payload is invalid." \
    bash "$provenance" materialize \
        --bundle "$invalid_payload" \
        --output "$fixture_root/payload/release-provenance.intoto.jsonl" \
        --subject "$package"

multiple_records="$fixture_root/multiple.intoto.jsonl"
printf '%s\n%s\n' "$(<"$portable_bundle")" "$(<"$portable_bundle")" \
    >"$multiple_records"
expect_failure \
    multiple-records \
    "Portable provenance must contain exactly one JSONL record." \
    bash "$provenance" verify \
        --bundle "$multiple_records" \
        --subject "$package"

multiple_source_records="$fixture_root/multiple-source.json"
printf '%s\n%s\n' "$(<"$source_bundle")" "$(<"$source_bundle")" \
    >"$multiple_source_records"
expect_failure \
    multiple-source-records \
    "Provenance bundle must contain one JSON object." \
    bash "$provenance" materialize \
        --bundle "$multiple_source_records" \
        --output "$fixture_root/multiple-source/release-provenance.intoto.jsonl" \
        --subject "$package"

expect_failure \
    wrong-output-name \
    "Portable provenance must be named release-provenance.intoto.jsonl." \
    bash "$provenance" materialize \
        --bundle "$source_bundle" \
        --output "$fixture_root/provenance.json" \
        --subject "$package"

duplicate_root="$fixture_root/duplicate-name"
mkdir -p "$duplicate_root/one" "$duplicate_root/two"
printf 'one\n' >"$duplicate_root/one/duplicate.nupkg"
printf 'two\n' >"$duplicate_root/two/duplicate.nupkg"
expect_failure \
    duplicate-input-name \
    "Provenance subject names must be unique: duplicate.nupkg" \
    bash "$provenance" materialize \
        --bundle "$source_bundle" \
        --output "$fixture_root/input-name/release-provenance.intoto.jsonl" \
        --subject "$duplicate_root/one/duplicate.nupkg" \
        --subject "$duplicate_root/two/duplicate.nupkg"

symlink_subject="$fixture_root/symlink.nupkg"
ln -s "$package" "$symlink_subject"
expect_failure \
    symlink-subject \
    "Provenance subject is missing or non-regular: $symlink_subject" \
    bash "$provenance" verify \
        --bundle "$portable_bundle" \
        --subject "$symlink_subject"

expect_failure \
    missing-bundle \
    "Provenance bundle is missing or non-regular: $fixture_root/missing.json" \
    bash "$provenance" verify \
        --bundle "$fixture_root/missing.json" \
        --subject "$package"

grep -Fq "id: attestation" "$workflow"
grep -Fq 'steps.attestation.outputs.bundle-path' "$workflow"
grep -Fq 'release-provenance.intoto.jsonl' "$workflow"
grep -Fq 'attestations: read' "$workflow"
grep -Fq 'verify-github' "$workflow"
grep -Fq -- "--bundle \"\$provenance_bundle\"" "$workflow"
grep -Fq -- '--signer-workflow' "$workflow"
grep -Fq -- "--signer-digest \"\$GITHUB_SHA\"" "$workflow"
grep -Fq -- '--source-ref refs/heads/main' "$workflow"
grep -Fq -- "--source-digest \"\$GITHUB_SHA\"" "$workflow"
grep -Fq "gh attestation verify \"\$subject\"" "$provenance"
grep -Fq -- '--deny-self-hosted-runners' "$provenance"
grep -Fq "bash eng/tests/test-release-provenance.sh" "$quality_workflow"

echo "Release provenance positive and negative cases passed."
