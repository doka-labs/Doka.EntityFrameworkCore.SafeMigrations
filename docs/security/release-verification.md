# Verify a SafeMigrations release

Use this procedure after a release is public. It performs read-only downloads
and verification; it does not dispatch workflows, create tags, request OIDC
credentials, or publish packages.

## Verify the signed source identity

Start from an independently approved version, commit, and copy of the
allowed-signers policy:

```bash
release_tag='vYOUR_APPROVED_VERSION'
expected_commit='YOUR_APPROVED_40_CHARACTER_COMMIT'
trusted_signers='/absolute/path/to/allowed-signers'

git cat-file -t "refs/tags/$release_tag"
git rev-parse "refs/tags/$release_tag^{commit}"
git -c gpg.format=ssh \
  -c gpg.ssh.allowedSignersFile="$trusted_signers" \
  verify-tag "$release_tag"
```

Require an annotated tag, an authorized signature, and exact equality between
the peeled commit and `expected_commit`. A key downloaded only alongside the
release is not an independent trust root.

## Verify immutable Release assets

```bash
release_repo='doka-labs/Doka.EntityFrameworkCore.SafeMigrations'
verification_dir="$(mktemp -d)"

gh release view "$release_tag" --repo "$release_repo" \
  --json tagName,isDraft,isImmutable,isPrerelease,assets,url
gh release verify "$release_tag" --repo "$release_repo"
gh release download "$release_tag" --repo "$release_repo" \
  --dir "$verification_dir"
```

GitHub creates the immutable-release attestation during publication. Its API
readback can become visible after the publish response, so the repository
publication workflow uses a bounded retry. An independent verifier may repeat
the same command for the same immutable tag; never treat a missing attestation
as success or disable verification.

Require exactly three `.nupkg`, three `.snupkg`, `SHA256SUMS`,
`manifest.spdx.json`, and `release-provenance.intoto.jsonl`. Then verify each
downloaded asset against the immutable Release:

```bash
for asset in "$verification_dir"/*; do
  gh release verify-asset "$release_tag" "$asset" --repo "$release_repo"
done
```

Verify the qualified package checksums from the download directory. On Linux:

```bash
cd "$verification_dir"
sha256sum --check SHA256SUMS
```

On macOS, use `shasum -a 256 --check SHA256SUMS` instead.

## Verify portable build provenance

The portable bundle must bind the six packages, `SHA256SUMS`, and the SPDX
manifest. Verify each subject while pinning the repository, signer workflow,
workflow commit, source ref, source commit, and hosted-runner requirement:

```bash
provenance_bundle="$verification_dir/release-provenance.intoto.jsonl"

for artifact in \
  "$verification_dir"/*.nupkg \
  "$verification_dir"/*.snupkg \
  "$verification_dir/SHA256SUMS" \
  "$verification_dir/manifest.spdx.json"; do
  gh attestation verify "$artifact" \
    --bundle "$provenance_bundle" \
    --repo "$release_repo" \
    --signer-workflow "$release_repo/.github/workflows/release-candidate.yml" \
    --signer-digest "$expected_commit" \
    --source-ref refs/heads/main \
    --source-digest "$expected_commit" \
    --deny-self-hosted-runners
done
```

`--repo` alone is insufficient because it would accept another authorized
workflow in the same repository. The additional signer and source constraints
bind the bundle to the reviewed release workflow and signed commit. The bundle
is portable, but an independently protected trusted root is still required for
fully offline verification; obtain it separately with
`gh attestation trusted-root` and provide it through `--custom-trusted-root`.

The repository validator intentionally accepts only
`application/vnd.dev.sigstore.bundle.v0.3+json`, the exact media type emitted
by the pinned `actions/attest` producer. The Sigstore bundle schema also
requires clients to understand older parameterized media types. This release
gate is deliberately narrower: a producer-format change fails closed and
requires an explicit review instead of silently broadening accepted evidence.

For each package's separately published SBOM attestation, repeat the online
verification without `--bundle` and select the SPDX predicate while retaining
every signer and source constraint:

```bash
for artifact in "$verification_dir"/*.nupkg "$verification_dir"/*.snupkg; do
  gh attestation verify "$artifact" \
    --repo "$release_repo" \
    --signer-workflow "$release_repo/.github/workflows/release-candidate.yml" \
    --signer-digest "$expected_commit" \
    --source-ref refs/heads/main \
    --source-digest "$expected_commit" \
    --predicate-type https://spdx.dev/Document/v2.2 \
    --deny-self-hosted-runners
done
```

## Verify NuGet repository signatures and content

Use Linux or Windows because NuGet signed-package verification is not supported
on macOS. Download and verify each public primary package with:

```bash
dotnet nuget verify /absolute/path/to/public-package.nupkg --all
```

From reviewed repository source, the same bounded comparison used by the
publication job is:

```bash
package_version='YOUR_APPROVED_VERSION_WITHOUT_V'
bash eng/readback-nuget.sh \
  --package-dir "$verification_dir" \
  --version "$package_version" \
  --timeout-seconds 1200
```

It requires valid NuGet repository signatures and compares every public
package entry with the qualified Release asset after excluding only
`.signature.p7s`. NuGet.org separately validates and indexes the submitted
`.snupkg` files; confirm the public symbol status for all three package IDs.

Stop on an unknown signer, source mismatch, missing or unexpected asset,
invalid attestation, invalid NuGet signature, content difference, or failed
symbol validation. Do not weaken a verification option to make a release pass.

## Primary sources

- [GitHub release asset verification](https://cli.github.com/manual/gh_release_verify-asset), retrieved 2026-08-26.
- [GitHub immutable Release verification](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/verify-release-integrity), retrieved 2026-08-26.
- [GitHub immutable Releases](https://docs.github.com/en/enterprise-cloud@latest/code-security/concepts/supply-chain-security/immutable-releases), retrieved 2026-08-29.
- [GitHub attestation verification](https://cli.github.com/manual/gh_attestation_verify), retrieved 2026-08-26.
- [GitHub offline attestation verification](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/verify-attestations-offline), retrieved 2026-08-29.
- [`actions/attest` bundle output](https://github.com/actions/attest#outputs), retrieved 2026-08-29.
- [Sigstore bundle media-type contract](https://github.com/sigstore/protobuf-specs/blob/main/protos/sigstore_bundle.proto), retrieved 2026-08-30.
- [OpenSSF Signed-Releases check](https://github.com/ossf/scorecard/blob/main/docs/checks.md#signed-releases), retrieved 2026-08-29.
- [`dotnet nuget verify`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify), retrieved 2026-08-26.
- [NuGet signed-package verification](https://learn.microsoft.com/en-us/dotnet/core/tools/nuget-signed-package-verification), retrieved 2026-08-26.
- [NuGet symbol package validation](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg), retrieved 2026-08-26.
