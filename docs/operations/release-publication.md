# Release publication operations

Use this runbook for every RC and stable release. The workflow qualifies an
untagged `main` commit first, then waits at the protected `nuget` environment.
Only after that wait begins does the operator create the signed tag and approve
publication. Run every command separately and stop on the first failure.

## One-time configuration

Configure these controls before the first release:

- protect `main` and require the successful CI jobs used by this repository;
- protect `v*` tags against update and deletion;
- enable immutable GitHub Releases;
- create environment `nuget`, restrict it to protected `main`, require a
  maintainer review, disable administrator bypass where the organization can
  support it, and store only the NuGet profile name as `NUGET_USER`;
- configure one NuGet Trusted Publishing policy with repository owner
  `doka-labs`, repository `Doka.EntityFrameworkCore.SafeMigrations`, workflow
  file `release-candidate.yml`, and environment `nuget`; and
- register the release operator's SSH signing key with GitHub and in
  [allowed-signers](../../eng/release/allowed-signers).

Do not configure a long-lived NuGet API key. The protected job exchanges its
GitHub OIDC token for a short-lived key immediately before publication.

## Publication procedure

### 1. Prepare reviewed main

Merge the complete release preparation through protected `main`. The source
must contain the intended `VersionPrefix`, one dated changelog entry for the
full version, current package metadata, API baselines, dependencies, and
support documentation. Then run:

```bash
git fetch origin main --tags
git switch main
git merge --ff-only origin/main
git status --short

release_commit="$(git rev-parse HEAD)"
test "${release_commit}" = "$(git rev-parse origin/main)"
```

`git status --short` must print nothing. Keep this terminal and checkout
unchanged until the tag is pushed.

### 2. Verify local pre-tag readiness

```bash
./eng/pre-tag-check.sh
```

This confirms a clean current `main`, no existing semantic release tag on the
candidate, and usable SSH tag-signing configuration. It creates no tag and
does not request publication credentials.

### 3. Start the untagged candidate and wait

In GitHub Actions, start **Release candidate**. Select branch `main` and enter:

```text
version: <release_version>
```

`release_version` never includes a leading `v`. Wait until these jobs are
green:

1. `Validate untagged candidate`;
2. every job below `Full reversible qualification`; and
3. `Attest qualified packages`.

The final `Verify tag and publish` job must show **Waiting** for approval on
environment `nuget`. Confirm that the run SHA equals `release_commit`. Do not
approve it yet.

### 4. Create the signed immutable identity

Only after the protected wait is visible, run:

```bash
release_version="<release_version>"
release_tag="v${release_version}"

test "$(git rev-parse HEAD)" = "${release_commit}"
test "$(git rev-parse origin/main)" = "${release_commit}"
git tag -s "${release_tag}" "${release_commit}" \
  -m "Doka.EntityFrameworkCore.SafeMigrations ${release_version}"
git tag -v "${release_tag}"
test "$(git rev-list -n 1 "${release_tag}")" = "${release_commit}"
git push origin "refs/tags/${release_tag}"
```

Push only that tag. Never use `git push --tags`, move a release tag, reuse a
published version, or create the tag before reversible qualification succeeds.

### 5. Approve the same waiting run

Return to the exact run checked in step 3. Approve `Verify tag and publish` for
environment `nuget`. The job then:

1. verifies the tag is annotated, identifies the qualified SHA, and has an
   authorized SSH signature;
2. verifies the downloaded package checksums and package contract again;
3. obtains a short-lived NuGet key through Trusted Publishing;
4. publishes the three primary and three symbol packages;
5. reads all primary packages back from NuGet.org, verifies their repository
   signatures, and compares their content with the qualified packages; and
6. completes or resumes a GitHub Release draft, verifies every asset digest,
   publishes it, and verifies the resulting immutable Release.

No package is rebuilt after qualification. Duplicate-tolerant pushes support a
same-run retry after a lost response; the subsequent signed-content readback is
the acceptance gate, not the push response alone.

### 6. Confirm completion

Require the complete workflow run to be green. Then inspect:

```bash
gh release view "${release_tag}" \
  --json tagName,isDraft,isImmutable,isPrerelease,assets,url
```

The release must be published and immutable, target the exact tag, have the
correct prerelease state, and contain exactly:

- three `.nupkg` files;
- three `.snupkg` files;
- `SHA256SUMS`; and
- `manifest.spdx.json`.

Confirm all three package pages and symbol validation status on NuGet.org.
Indexing can lag after the upload; a pending package is not a failed upload,
but the workflow's bounded signed-package readback must already have passed.

## Failure and recovery

- Before a tag exists, fix the source or workflow through review and start a
  new candidate run. No release identity has been consumed.
- After the tag exists, never move or delete it to hide a failure. Rerun only
  the failed publication job in the same workflow run.
- The publish job accepts matching already-published packages through
  `--skip-duplicate`, then verifies their signed content. A conflicting public
  package or immutable Release asset fails closed.
- A failed GitHub asset upload may leave a draft. The same job resumes only a
  draft with the expected tag, prerelease state, asset names, and SHA-256
  digests. It uploads missing assets and never overwrites a mismatch. The
  separately verified signed tag remains the commit identity; GitHub's
  `targetCommitish` metadata is not used as a second identity.
- If qualification artifacts expired or the original run can no longer be
  rerun, stop for maintainer recovery. Do not rebuild under the same tag.

Stable releases use the same procedure. A stable package is independently
qualified for its own version; an RC package is never renamed into a stable
package.

## Primary sources

- [GitHub environments and required reviewers](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments), retrieved 2026-08-26.
- [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations), retrieved 2026-08-26.
- [GitHub immutable Releases and `gh release create`](https://cli.github.com/manual/gh_release_create), retrieved 2026-08-26.
- [GitHub Release asset upload](https://cli.github.com/manual/gh_release_upload), retrieved 2026-08-26.
- [GitHub Release verification](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/verify-release-integrity), retrieved 2026-08-26.
- [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), retrieved 2026-08-26.
- [`dotnet nuget push`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-push), retrieved 2026-08-26.
- [NuGet symbol package publication](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg), retrieved 2026-08-26.
