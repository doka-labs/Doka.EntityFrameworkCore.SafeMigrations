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

Require exactly three `.nupkg`, three `.snupkg`, `SHA256SUMS`, and
`manifest.spdx.json`. Then verify each downloaded asset against the immutable
Release:

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

## Verify build provenance and SBOM attestations

For every downloaded package and the SBOM, run from a GitHub CLI version with
attestation support:

```bash
artifact='/absolute/path/to/downloaded-asset'
gh attestation verify "$artifact" \
  --repo "$release_repo" \
  --signer-workflow "$release_repo/.github/workflows/release-candidate.yml" \
  --source-ref refs/heads/main \
  --source-digest "$expected_commit" \
  --deny-self-hosted-runners
```

The default predicate verifies build provenance. For each package's SBOM
attestation, repeat with
`--predicate-type https://spdx.dev/Document/v2.2`.

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
- [GitHub attestation verification](https://cli.github.com/manual/gh_attestation_verify), retrieved 2026-08-26.
- [`dotnet nuget verify`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify), retrieved 2026-08-26.
- [NuGet signed-package verification](https://learn.microsoft.com/en-us/dotnet/core/tools/nuget-signed-package-verification), retrieved 2026-08-26.
- [NuGet symbol package validation](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg), retrieved 2026-08-26.
