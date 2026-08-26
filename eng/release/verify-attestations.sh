#!/usr/bin/env bash

set -euo pipefail

manifest=artifacts/sbom/_manifest/spdx_2.2/manifest.spdx.json
provenance_bundle=artifacts/attestations/build-provenance.sigstore.json
sbom_bundle=artifacts/attestations/sbom-attestation.sigstore.json

# The pinned actions/attest derives its predicate from spdxVersion. Validate
# the producer's supported format, then derive the verifier from that same file.
spdx_version="$(jq -er '.spdxVersion | select(. == "SPDX-2.2")' "$manifest")"
sbom_predicate="https://spdx.dev/Document/v${spdx_version#SPDX-}"
identity_arguments=(
    --repo "${GITHUB_REPOSITORY:?}"
    --signer-workflow "$GITHUB_REPOSITORY/.github/workflows/release-candidate.yml"
    --signer-digest "${GITHUB_SHA:?}"
    --source-ref refs/heads/main
    --source-digest "$GITHUB_SHA"
    --deny-self-hosted-runners
)

for artifact in \
    artifacts/packages/*.nupkg \
    artifacts/packages/*.snupkg \
    artifacts/packages/SHA256SUMS \
    artifacts/packages/SYMBOLS.json \
    "$manifest"; do
    gh attestation verify "$artifact" \
        --bundle "$provenance_bundle" \
        "${identity_arguments[@]}"
done

for artifact in artifacts/packages/*.nupkg artifacts/packages/*.snupkg; do
    gh attestation verify "$artifact" \
        --bundle "$sbom_bundle" \
        --predicate-type "$sbom_predicate" \
        "${identity_arguments[@]}"
done
