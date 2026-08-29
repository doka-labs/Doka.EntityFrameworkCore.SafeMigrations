#!/usr/bin/env bash

set -euo pipefail

portable_provenance_name="release-provenance.intoto.jsonl"
sigstore_bundle_media_type="application/vnd.dev.sigstore.bundle.v0.3+json"
in_toto_payload_type="application/vnd.in-toto+json"
in_toto_statement_type="https://in-toto.io/Statement/v1"
slsa_provenance_type="https://slsa.dev/provenance/v1"

usage() {
    echo "Usage: $0 <materialize|verify|verify-github> --bundle <path>" >&2
    echo "       [--output <path>] --subject <path>... [GitHub identity options]" >&2
}

fail() {
    echo "Release provenance failed: $1" >&2
    exit 1
}

sha256_file() {
    local file="$1"

    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file" | cut -d ' ' -f 1
    else
        shasum -a 256 "$file" | cut -d ' ' -f 1
    fi
}

if (($# == 0)); then
    usage
    exit 2
fi

operation="$1"
shift

if [[ "$operation" != materialize \
    && "$operation" != verify \
    && "$operation" != verify-github ]]; then
    usage
    exit 2
fi

bundle=""
output=""
repository=""
signer_workflow=""
signer_digest=""
source_ref=""
source_digest=""
subjects=()

while (($# > 0)); do
    case "$1" in
        --bundle)
            if (($# < 2)); then
                usage
                exit 2
            fi

            bundle="$2"
            shift 2
            ;;
        --output)
            if (($# < 2)); then
                usage
                exit 2
            fi

            output="$2"
            shift 2
            ;;
        --subject)
            if (($# < 2)); then
                usage
                exit 2
            fi

            subjects+=("$2")
            shift 2
            ;;
        --repository)
            if (($# < 2)); then
                usage
                exit 2
            fi

            repository="$2"
            shift 2
            ;;
        --signer-workflow)
            if (($# < 2)); then
                usage
                exit 2
            fi

            signer_workflow="$2"
            shift 2
            ;;
        --signer-digest)
            if (($# < 2)); then
                usage
                exit 2
            fi

            signer_digest="$2"
            shift 2
            ;;
        --source-ref)
            if (($# < 2)); then
                usage
                exit 2
            fi

            source_ref="$2"
            shift 2
            ;;
        --source-digest)
            if (($# < 2)); then
                usage
                exit 2
            fi

            source_digest="$2"
            shift 2
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ -z "$bundle" || ${#subjects[@]} -eq 0 ]]; then
    usage
    exit 2
fi

if [[ "$operation" == materialize ]]; then
    if [[ -z "$output" || "$(basename -- "$output")" != "$portable_provenance_name" ]]; then
        fail "Portable provenance must be named $portable_provenance_name."
    fi

    if [[ -n "$repository$signer_workflow$signer_digest$source_ref$source_digest" ]]; then
        usage
        exit 2
    fi
elif [[ -n "$output" ]]; then
    usage
    exit 2
fi

if [[ "$operation" == verify \
    && -n "$repository$signer_workflow$signer_digest$source_ref$source_digest" ]]; then
    usage
    exit 2
fi

if [[ "$operation" == verify-github \
    && ( -z "$repository" \
        || -z "$signer_workflow" \
        || -z "$signer_digest" \
        || -z "$source_ref" \
        || -z "$source_digest" ) ]]; then
    usage
    exit 2
fi

if [[ ! -f "$bundle" || -L "$bundle" ]]; then
    fail "Provenance bundle is missing or non-regular: $bundle"
fi

temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/safe-migrations-provenance.XXXXXX")"
trap 'rm -rf -- "$temporary_root"' EXIT

expected_records="$temporary_root/expected.jsonl"
: >"$expected_records"

for subject in "${subjects[@]}"; do
    if [[ ! -f "$subject" || -L "$subject" ]]; then
        fail "Provenance subject is missing or non-regular: $subject"
    fi

    # in-toto identifies subjects by name, so duplicate basenames would make
    # the local name-to-digest binding ambiguous even when paths differ.
    subject_name="$(basename -- "$subject")"
    if jq -e --arg name "$subject_name" \
        'select(.name == $name)' "$expected_records" >/dev/null; then
        fail "Provenance subject names must be unique: $subject_name"
    fi

    subject_digest="$(sha256_file "$subject")"
    jq -cn \
        --arg name "$subject_name" \
        --arg sha256 "$subject_digest" \
        '{name: $name, sha256: $sha256}' >>"$expected_records"
done

expected_inventory="$temporary_root/expected.json"
jq -cs 'sort_by(.name)' "$expected_records" >"$expected_inventory"

validate_bundle() {
    local candidate="$1"
    local json_lines="$2"
    local statement="$temporary_root/statement.json"
    local actual_inventory="$temporary_root/actual.json"
    local non_empty_lines

    if [[ "$json_lines" == true ]]; then
        non_empty_lines="$(awk 'NF { count++ } END { print count + 0 }' "$candidate")"
        if [[ "$non_empty_lines" != 1 ]]; then
            fail "Portable provenance must contain exactly one JSONL record."
        fi
    fi

    if ! jq -e -s 'length == 1 and (.[0] | type == "object")' \
        "$candidate" >/dev/null 2>&1; then
        fail "Provenance bundle must contain one JSON object."
    fi

    if ! jq -e \
        --arg media_type "$sigstore_bundle_media_type" \
        --arg payload_type "$in_toto_payload_type" \
        '.mediaType == $media_type
          and (.verificationMaterial | type == "object" and length > 0)
          and (.dsseEnvelope | type == "object")
          and .dsseEnvelope.payloadType == $payload_type
          and (.dsseEnvelope.payload | type == "string" and length > 0)
          and (.dsseEnvelope.signatures | type == "array" and length > 0)
          and all(.dsseEnvelope.signatures[];
            type == "object" and (.sig | type == "string" and length > 0))' \
        "$candidate" >/dev/null 2>&1; then
        fail "Sigstore bundle envelope is invalid."
    fi

    if ! jq -e '.dsseEnvelope.payload | @base64d | fromjson' \
        "$candidate" >"$statement" 2>/dev/null; then
        fail "Sigstore bundle payload is invalid."
    fi

    if ! jq -e \
        --arg statement_type "$in_toto_statement_type" \
        --arg provenance_type "$slsa_provenance_type" \
        'type == "object"
          and ._type == $statement_type
          and .predicateType == $provenance_type
          and (.predicate | type == "object")' \
        "$statement" >/dev/null 2>&1; then
        fail "Bundle does not contain SLSA build provenance."
    fi

    if ! jq -ce '
        if ((.subject | type) != "array" or (.subject | length) == 0) then
          error("missing subjects")
        else
          .subject
        end
        | map(
            if (type == "object"
                and (.name | type == "string" and length > 0)
                and (.name | test("[/\\\\]") | not)
                and (.digest | type == "object")
                and (.digest.sha256 | type == "string"
                  and test("^[0-9a-f]{64}$"))) then
              {name: .name, sha256: .digest.sha256}
            else
              error("invalid subject")
            end)
        | sort_by(.name)
        | if ([.[].name] | unique | length) == length then
            .
          else
            error("duplicate subject")
          end' \
        "$statement" >"$actual_inventory" 2>/dev/null; then
        fail "SLSA provenance subject inventory is invalid."
    fi

    if ! cmp -s "$expected_inventory" "$actual_inventory"; then
        fail \
            "SLSA provenance subject inventory does not match the selected release assets."
    fi
}

if [[ "$operation" == materialize ]]; then
    validate_bundle "$bundle" false

    output_directory="$(dirname -- "$output")"
    mkdir -p "$output_directory"
    temporary_output="$temporary_root/$portable_provenance_name"
    jq -cS . "$bundle" >"$temporary_output"
    validate_bundle "$temporary_output" true
    mv -- "$temporary_output" "$output"

    echo "Portable SLSA provenance materialized and verified: $output"
else
    validate_bundle "$bundle" true
    if [[ "$operation" == verify-github ]]; then
        # Structural validation detects ambiguity and byte drift. The GitHub
        # CLI remains the cryptographic verifier for signatures and identity.
        for subject in "${subjects[@]}"; do
            gh attestation verify "$subject" \
                --bundle "$bundle" \
                --repo "$repository" \
                --signer-workflow "$signer_workflow" \
                --signer-digest "$signer_digest" \
                --source-ref "$source_ref" \
                --source-digest "$source_digest" \
                --deny-self-hosted-runners
        done

        echo "Portable SLSA provenance and GitHub identity verified: $bundle"
    else
        echo "Portable SLSA provenance verified: $bundle"
    fi
fi
