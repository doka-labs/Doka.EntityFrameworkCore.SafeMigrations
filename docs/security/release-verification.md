# Verify a SafeMigrations release

This procedure is for a release that has actually been published. It performs
downloads and verification, not tag creation, workflow dispatch, package push,
or OIDC login. It does not imply that an initial candidate is already available.
The [publication operations guide](../operations/release-publication.md) is the
separate maintainer procedure.

## Establish independent expectations

Choose the intended version, repository, and full source commit from your
approved release record. Confirm that the signed tag, completed workflow run,
and release refer to that commit. Review the signing trust policy through an
already trusted source or maintainer channel. A public key downloaded only
alongside an untrusted package does not independently establish its authority.

Use Git with SSH-signature verification, GitHub CLI with attestation support,
the pinned .NET SDK for repository readback tooling, and the prerequisites in
[CONTRIBUTING.md](../../CONTRIBUTING.md). Use a separate verification directory;
do not overwrite existing downloads. Inspect repository scripts before running
them; they are executable code, not inert signature data.

## Signed source identity

In a reviewed clone containing the selected tag, set the intended values:

```bash
release_tag='vYOUR_APPROVED_VERSION'
expected_commit='YOUR_APPROVED_40_CHARACTER_COMMIT'
trusted_signers='/absolute/path/to/independently-reviewed-allowed-signers'
git cat-file -t "refs/tags/$release_tag"
git rev-parse "refs/tags/$release_tag^{commit}"
git -c gpg.format=ssh -c gpg.ssh.allowedSignersFile="$trusted_signers" \
  verify-tag "$release_tag"
```

Require an annotated `tag`, a valid authorized signature, and exact equality
between the peeled commit and `expected_commit`. A valid signature by an
unknown key is insufficient. A signed commit is not a substitute for the
required signed annotated tag. The repository's
[tag verifier](../../eng/release/verify-tag.sh) implements its own reviewed
signer policy for qualification.

## Qualified assets and provenance

Download the selected release's assets using GitHub's HTTPS interface or CLI:

```bash
release_repo='doka-labs/Doka.EntityFrameworkCore.SafeMigrations'
verification_dir="$(mktemp -d)"
gh release download "$release_tag" --repo "$release_repo" --dir "$verification_dir"
```

The immutable release contains exactly these eleven uploaded assets, with
`<version>` replaced by the approved version without `v`:

| Asset | Role |
| --- | --- |
| `Doka.EntityFrameworkCore.SafeMigrations.<version>.nupkg` | Qualified Core package |
| `Doka.EntityFrameworkCore.SafeMigrations.MySql.<version>.nupkg` | Qualified MySQL/MariaDB package |
| `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.<version>.nupkg` | Qualified PostgreSQL package |
| `Doka.EntityFrameworkCore.SafeMigrations.<version>.snupkg` | Qualified Core symbols |
| `Doka.EntityFrameworkCore.SafeMigrations.MySql.<version>.snupkg` | Qualified MySQL/MariaDB symbols |
| `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.<version>.snupkg` | Qualified PostgreSQL symbols |
| `SHA256SUMS` | Hashes of the six qualified package archives and `SYMBOLS.json` |
| `SYMBOLS.json` | Qualified assembly/PDB identities and checksums |
| `manifest.spdx.json` | Qualified SPDX 2.2 manifest |
| `build-provenance.sigstore.json` | Build-provenance attestation bundle |
| `sbom-attestation.sigstore.json` | SPDX 2.2 attestation bundle |

Verify the exact set; missing or unexpected assets fail verification. GitHub's
generated source ZIP/tar archives are separate from these uploaded assets.
The qualified packages are pre-NuGet bytes. NuGet adds its repository signature
later, so their whole-file digests differ from signed public packages.
`SIGNED_SHA256SUMS` and dynamic publication observations are not Release assets.
The GitHub Release can become visible while NuGet verification is still pending;
require the completed successful run and perform all checks below.

Verify provenance for each of the six packages, `SHA256SUMS`, `SYMBOLS.json`,
and `manifest.spdx.json`. For each artifact, set its absolute path and run:

```bash
artifact='/absolute/path/to/one-downloaded-qualified-artifact'
gh attestation verify "$artifact" \
  --bundle "$verification_dir/build-provenance.sigstore.json" \
  --repo "$release_repo" \
  --signer-workflow "$release_repo/.github/workflows/release-candidate.yml" \
  --signer-digest "$expected_commit" \
  --source-ref refs/heads/main \
  --source-digest "$expected_commit" \
  --deny-self-hosted-runners
```

The default predicate is SLSA provenance v1. Repeat for each package's SBOM
attestation using `sbom-attestation.sigstore.json` and additionally
`--predicate-type https://spdx.dev/Document/v2.2`. The pinned Action derives
the predicate version from the manifest's `spdxVersion`; this repository's
producer emits `SPDX-2.2`. The release workflow verifies that same predicate;
a mismatched manifest or predicate blocks publication.
Do not silently omit identity constraints to make verification pass.

Check `SHA256SUMS` against the qualified files. For Linux use
`sha256sum --check SHA256SUMS`; on macOS use `shasum -a 256 --check SHA256SUMS`
from the directory containing those files. Compare manifest subjects and
dependencies with the intended package family and version. A successful hash
comparison proves byte agreement, not origin; provenance and authorized source
identity supply separate checks. Attestations do not certify absence of defects
or establish an independently evaluated SLSA level.

## NuGet signatures, content, and symbols

Use a supported Linux or Windows environment for NuGet signature verification.
Run the complete Bash readback procedure below on supported Ubuntu/Linux.
Microsoft does not currently support signed-package verification on macOS;
the earlier macOS hash command does not extend that support. Move the NuGet
verification phase to a supported host instead of disabling signatures.

Download the same package versions from nuget.org over HTTPS. Use:

```bash
dotnet nuget verify /absolute/path/to/downloaded-package.nupkg --all
```

For each primary package require a valid NuGet repository signature, correct
package ID/version, and agreement with the qualified content after accounting
only for NuGet's `.signature.p7s` entry. Signature validity alone does not prove
the package was built by the expected workflow. Public Portable PDB readback
must match each candidate assembly's PDB identity/checksum through the qualified
`SYMBOLS.json` manifest. This is a content/identity check, not a separate digital
signature on each PDB.

The repository provides the same detailed, credential-free readback used by
publication. From trusted, reviewed source with the SDK available:

```bash
package_version='YOUR_APPROVED_VERSION_WITHOUT_V'
readback_dir="$(mktemp -d)"
eng/readback-nuget.sh \
  --package-dir "$verification_dir" \
  --output "$readback_dir" \
  --version "$package_version"
```

This downloads public package/symbol content, verifies signatures and canonical
content, and writes local evidence. It consumes the existing qualified symbol
manifest without building a helper and can retry public indexing for up to its
one-hour deadline; it never publishes or obtains a NuGet publication credential.
No published `SIGNED_SHA256SUMS` is needed: the independently verified qualified
assets, attestations, and symbol manifest are the comparison baseline. The
helper creates a local `SIGNED_SHA256SUMS` listing hashes of repository-signed
packages and public PDBs; that checksum file is not itself signed. Maintainer
copies belong to the publication-attempt artifact, not the GitHub Release.
Retain the verified files, tool versions, commit, version, and verification
output under your own evidence policy.

An exact qualified payload awaiting its NuGet repository signature remains
pending during bounded readback, never a verification success. Completion
requires `dotnet nuget verify --all` to pass for all three public packages and
all public PDB identity/checksum checks to pass. Content mismatches and invalid
signatures fail closed; do not replace verification with an unsigned checksum.

## Failure handling

Stop on unknown signer, source mismatch, missing provenance/asset, invalid
signature, content difference, wrong package graph, or PDB mismatch. Preserve
the evidence and contact the maintainer privately if substitution is suspected.
Do not disable signature validation, accept a different version, or treat a
matching checksum from the same suspect channel as remediation.

The repository's deterministic double-pack checks two packs of the same build.
They are useful evidence but are not an independent bit-for-bit rebuild by
another party/environment. No such broader reproducibility claim is made here.

## Primary sources

- [GitHub CLI attestation verification](https://cli.github.com/manual/gh_attestation_verify),
  retrieved 2026-08-26.
- [dotnet nuget verify](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify),
  retrieved 2026-08-26.
- [NuGet signature verification](https://learn.microsoft.com/en-us/nuget/reference/signed-package-verification-options),
  retrieved 2026-08-26.
- [NuGet verification platform support](https://learn.microsoft.com/en-us/dotnet/core/tools/nuget-signed-package-verification),
  retrieved 2026-08-26.
- [Pinned Action SBOM predicate construction](https://github.com/actions/attest/blob/1e69f48acb82d1966a394da916b4c1698aa569d6/src/sbom.ts),
  retrieved 2026-08-26.
