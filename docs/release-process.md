# Release process

SafeMigrations has one stable, tag-driven publication path. CI and release call
the same reusable quality workflow; release adds stable dependency enforcement,
attestations, Trusted Publishing, signed readback, and GitHub Release creation.

## One-time repository configuration

1. Protect `main` and require the CI `Full qualification` result before merge.
2. Create a GitHub environment named `nuget` and require the intended release
   reviewers. Do not store a long-lived NuGet API key.
3. Store the NuGet account name as the `NUGET_USER` environment secret.
4. In nuget.org, configure a Trusted Publishing policy for each package:
   - owner: the repository owner;
   - repository: `Doka.EntityFrameworkCore.SafeMigrations`;
   - workflow: `release.yml`;
   - environment: `nuget`.
5. Enable artifact attestations for the repository and retain release workflow
   evidence according to organizational policy.

The workflow grants `id-token: write` only to the environment-protected publish
job. `NuGet/login` exchanges that OIDC token for a short-lived API key. Checkout
credentials are not persisted.

## Preconditions

- the release commit is merged into `main`;
- `main` CI is green for the exact commit;
- `CHANGELOG.md` describes the intended stable version;
- all package lockfiles are reviewed and locked restore succeeds;
- the Doka dependency is a stable 10.x package available from nuget.org;
- all three package IDs are owned by the configured NuGet account;
- no package with the target version exists unless it is an exact partial
  publication from the same source/tag and qualification bytes.

## Trigger

Create and push one stable SemVer tag:

```bash
git tag -a v1.0.0 -m "v1.0.0"
git push origin v1.0.0
```

The workflow accepts only `vMAJOR.MINOR.PATCH` with no leading zeroes or
prerelease suffix and verifies that the tagged commit is reachable from
`origin/main`. Creating or pushing the tag remains an explicit operator action;
the workflow has no code path that creates tags.

## Qualification graph

The release invokes `.github/workflows/quality-gates.yml` with the tag-derived
package version and `require-stable-dependencies: true`. It must complete:

- locked restore, warning-free Release build, Core tests;
- all performance and allocation budgets;
- six MySQL/MariaDB and two PostgreSQL engine cells;
- EF CLI, normal/idempotent/no-transaction scripts, and Bundle in every cell;
- Latest EF/Npgsql patch profile;
- deterministic double-pack and exact package-content verification;
- package-only consumer;
- Microsoft SBOM Tool generation and validation.

Exactly one workflow artifact contains the six qualified package files,
`SHA256SUMS`, performance results, and SPDX manifest. Its name contains the
version and workflow attempt so a rerun cannot collide with an earlier
immutable Actions artifact.

## Attestation and publication

The publish job downloads that artifact and verifies `SHA256SUMS`; it does not
checkout a different ref, rebuild, restore, or repack. It then creates:

- SLSA build provenance for all `.nupkg`, `.snupkg`, and the checksum file;
- an SPDX SBOM attestation for all package and symbol files.

`eng/publish-nuget.sh` publishes in dependency order:

1. Core;
2. MySQL/MariaDB adapter;
3. PostgreSQL adapter.

Only `.nupkg` is pushed. NuGet automatically publishes the corresponding
`.snupkg` found beside it. The command does not use `--skip-duplicate`.

NuGet cannot atomically publish three package IDs. On a retry after partial
publication, the script downloads each existing package, verifies its NuGet
repository signature, removes exactly one `.signature.p7s` entry in a temporary
copy, and recursively compares every remaining entry with the qualified
package. It skips only an exact match. A missing package is pushed; a different
or unsigned existing package stops publication. This makes forward completion
possible without accepting an unrelated duplicate version.

## Readback and release creation

After all pushes, `eng/readback-nuget.sh` waits for every flat-container package,
runs `dotnet nuget verify --all`, and proves content equality after removing the
single NuGet repository signature. Signed readback packages and their checksums
are retained as a separate workflow artifact.

Only after readback succeeds does the workflow create a draft GitHub Release,
upload qualified packages, symbol packages, checksums, SPDX, attestation
bundles, and signed-readback checksums, then publish the draft. A NuGet failure
cannot produce a public GitHub Release.

## Failure handling

- Before any package push: correct the source on `main`; do not move or reuse a
  published tag. Create the intended new version after review.
- During a partial NuGet push: rerun the failed workflow attempt for the same
  immutable tag. Exact existing bytes are verified before remaining packages
  are pushed.
- Different bytes already published: stop. NuGet versions are immutable; do not
  suppress the conflict. Investigate ownership and provenance, then release a
  new reviewed version.
- NuGet complete, readback delayed: rerun. The publish script verifies all
  existing bytes and readback resumes.
- Draft GitHub Release asset failure: keep the draft non-public, inspect the
  workflow evidence, and complete or replace it only through an approved
  operator action.

## Primary references

- [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
- [NuGet symbol packages](https://learn.microsoft.com/nuget/create-packages/symbol-packages-snupkg)
- [NuGet package signatures](https://learn.microsoft.com/nuget/reference/signed-package-verification-options)
- [GitHub artifact attestations](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds)
- [GitHub reusable workflows](https://docs.github.com/actions/using-workflows/reusing-workflows)
- [Microsoft SBOM Tool](https://github.com/microsoft/sbom-tool)
