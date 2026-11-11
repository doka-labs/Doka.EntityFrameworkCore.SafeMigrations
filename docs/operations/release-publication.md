# Release publication operations

Use this runbook for every RC and stable release. The workflow qualifies an
untagged authorized source-line commit first, then waits at the protected `nuget` environment.
Only after that wait begins does the operator create the signed tag and approve
publication. Run every command separately and stop on the first failure.

The release target is stable 10.7.2 for all five package IDs, retaining the
inventory introduced in 10.4.6. Review the
[10.7.2 compatibility notes](../../CHANGELOG.md#1072---2026-11-11) and
[upgrade procedure](../support-and-qualification.md#upgrade-to-1072), including
native expression/catalog corrections, renewed affected numeric-default
approvals, unchanged public APIs, and the
Doka 10.6.5, MySqlConnector 2.6.2, EF 10.0.14, SqlClient 6.1.8 and PKCS
9.0.22 floors and retained strict release audits. Review the
[transport rollout boundaries](../efcore-provider-upgrade-risk.md#sql-server-boundary),
including corrected explicit certificate-name validation.
Applications upgrading from before 10.7.0 must also review its
[explicit-token source break and approved SemVer exception](../support-and-qualification.md#upgrade-to-1070).
Applications upgrading from before 10.6.0 must also review its
[computed/CHECK approval requirements](../support-and-qualification.md#upgrade-to-1060).
Historical migration source, policies, report schemas, relational model-
fingerprint format, history, concurrency and recovery guards remain. Timing
evidence is informational; functional and
evidence failures remain blocking. Do not publish a subset or treat preparation,
earlier engine runs or native ARM skips as proof that this candidate's complete
qualification or public readback has succeeded.

As of 2026-11-11, 10.7.2 is a local release target, not a completed publication.
The changelog date identifies release notes, not qualification or public readback.
Functional evidence at source `5e729211a010` covers Core/SQLite, native MariaDB
12.3.3, MySQL 8.4.13 and 9.7.4, PostgreSQL 18.6, and offline SQL Server. It
precedes the 10.7.2 metadata/documentation changes and does not qualify the final
stable package bytes or every supported server version. Build and qualify fresh
10.7.2 packages, including their embedded README and isolated consumers.
Local success does not complete the required hosted matrix, native x64 SQL Server
2019/2022/2025, merged coverage, SBOM, attestations or public readback. No candidate
workflow or publication is started by this preparation. The complete candidate
workflow must succeed with its exact committed source and package identities.
Do not publish until the final committed candidate passes those checks and
the rest of the required workflow. Recheck unused version/tag identity at
admission; local preparation does not reserve a release. Record actual
qualification and publication status in the candidate run's
release evidence, including the source commit, exact package identities,
completed checks and public readback. Product version references describe the
contract, not the workflow's current state.

## One-time configuration

Configure these controls before the first release:

- protect each authorized publication branch and require its own successful CI;
- protect `v*` tags against update and deletion;
- enable immutable GitHub Releases;
- retain the existing environment `nuget`, maintainer reviewers, self-review and
  administrator-bypass policy; restrict deployment to the reviewed protected
  active refs and store only the NuGet profile name as `NUGET_USER`;
- configure one NuGet Trusted Publishing policy with repository owner
  `doka-labs`, repository `Doka.EntityFrameworkCore.SafeMigrations`, workflow
  file `release-candidate.yml`, and environment `nuget`; and
- register the release operator's SSH signing key with GitHub and in
  [allowed-signers](../../eng/release/allowed-signers).

Do not configure a long-lived NuGet API key. The protected job exchanges its
GitHub OIDC token for a short-lived key immediately before publication.
Only main/10 is active. The registered maintenance/10.x line remains inactive;
this implementation creates no branch, grant or .NET 11 support. Additional
line preparation/activation requires the separate review in
[Release lines](release-lines.md). It does not require a new environment or
credential generation.

## Publication procedure

### 1. Prepare the reviewed source line

Merge the complete release preparation through its protected authorized branch. The source
must contain the intended `VersionPrefix`, one dated changelog entry for the
full version, current package metadata, API baselines, dependencies, and
support documentation. Then run:

```bash
release_branch='main'
git fetch origin "$release_branch" --tags
git switch "$release_branch"
git merge --ff-only "origin/$release_branch"
git status --porcelain --untracked-files=all

release_commit="$(git rev-parse HEAD)"
test "${release_commit}" = "$(git rev-parse "origin/$release_branch")"
```

`git status --porcelain --untracked-files=all` must print nothing. Its stable
machine-readable output is unaffected by optional branch-status display
configuration. Keep this terminal and checkout unchanged until the tag is
pushed.

### 2. Verify local pre-tag readiness

```bash
./eng/pre-tag-check.sh
```

This confirms an eligible exact line, its clean current source, no existing semantic release tag on the
candidate, and usable SSH tag-signing configuration. It creates no tag and
does not request publication credentials. A successful run prints one
`[PASS]` row for the branch, working tree, source commit, release tag, and tag
signing before it says that the Release candidate workflow can be started. A
`[FAIL]` row identifies the condition to correct; stop there and do not start
the workflow.

### 3. Start the untagged candidate and wait

In GitHub Actions, start **Release candidate**. Select the authorized source
branch (`main` in the current active configuration) and enter:

```text
version: <release_version>
qualification-only: false
```

`release_version` never includes a leading `v`. Wait until these jobs are
green:

1. `Validate untagged candidate`;
2. every job below `Full reversible qualification`; and
3. `Attest qualified packages`, including portable SLSA provenance
   materialization and artifact upload.

The final `Verify tag and publish` job must show **Waiting** for approval on
environment `nuget`. Confirm that the run SHA equals `release_commit`. Do not
approve it yet.

For a prepared line or a nonpublishing rehearsal, set qualification-only true.
The complete reversible qualification and attestation run, but the publisher job
is skipped before requesting its environment or NuGet credentials. Do not create
a tag or perform steps 4/5 for that run.

### 4. Create the signed immutable identity

Only after the protected wait is visible, run:

```bash
release_version="<release_version>"
release_tag="v${release_version}"

test "$(git rev-parse HEAD)" = "${release_commit}"
test "$(git rev-parse "origin/$release_branch")" = "${release_commit}"
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
3. validates the portable Sigstore envelope, SLSA v1 predicate, and exact
   twelve-subject inventory, then cryptographically verifies every subject
   against the release workflow and qualified commit;
4. creates or resumes a metadata-matching GitHub Release draft, uploads the
   exact thirteen release assets, and verifies every asset name and SHA-256
   digest;
5. checks the public state of all five primary packages with bounded retries,
   then obtains a short-lived NuGet key through Trusted Publishing only after
   the complete draft has been read back;
6. publishes missing primary packages in dependency order (Core, MySQL,
   PostgreSQL, SQLite, SQL Server), then publishes all five symbol packages with duplicate
   tolerance;
7. reads all primary packages back from NuGet.org, verifies their repository
   signatures, and compares their content with the qualified packages; and
8. publishes the prepared draft and waits for the immutable Release plus every
   Release-asset attestation through a bounded five-minute readback window.

Before public draft state, NuGet credentials and final publication, the same
publisher checks current protected-main line authority and the original
admission, qualified artifact identities and exact bytes. All lines share its
non-cancelling queue. A lower stable major/patch can publish without displacing
a higher stable Latest; prereleases never become Latest.

No package is rebuilt after qualification. A same-run retry skips primary
packages already visible on NuGet.org and safely repeats duplicate-tolerant
symbol pushes. The subsequent signed-content readback is the acceptance gate,
not the status check or push response alone. A new workflow dispatch is not a
recovery path because the candidate preflight requires an unused version.

### 6. Confirm completion

Require the complete workflow run to be green. Then inspect:

```bash
gh release view "${release_tag}" \
  --json tagName,isDraft,isImmutable,isPrerelease,assets,url
```

The release must be published and immutable, target the exact tag, have the
correct prerelease state, and contain exactly:

- five `.nupkg` files;
- five `.snupkg` files;
- `SHA256SUMS`; and
- `manifest.spdx.json`; and
- `release-provenance.intoto.jsonl`.

Download the Release into an empty directory and verify every selected subject
against the portable bundle using the exact repository, signer-workflow,
signer-digest, source-ref, source-digest, and hosted-runner restrictions in
[Release verification](../security/release-verification.md). A matching
filename without successful cryptographic verification is not evidence.

Confirm all five package pages and symbol validation status on NuGet.org.
Indexing can lag after the upload; a pending package is not a failed upload,
but the workflow's bounded signed-package readback must already have passed.

## Failure and recovery

- Before a tag exists, fix the source or workflow through review and start a
  new candidate run. No release identity has been consumed.
- After the tag exists, never move or delete it to hide a failure. Rerun only
  the failed publication job in the same workflow run when that original code
  can resume safely. A rerun retains its original SHA/ref; merging corrected
  controls into main does not replace code inside the old run. If the original
  publisher itself is broken after uploads, use the completion-only procedure
  below rather than a new candidate dispatch or rebuilt package.
- If draft staging fails, no NuGet credential has been requested and no package
  has been pushed. The same job may resume only matching draft assets.
- Missing, malformed, ambiguous, or cryptographically invalid portable
  provenance fails before draft creation and NuGet credential exchange. Rerun
  the failed job only after the reviewed workflow or source correction; never
  fabricate or manually attach a replacement bundle.
- The publish job skips primary packages already visible on NuGet.org and
  repeats symbol pushes with `--skip-duplicate`, then verifies signed primary
  content. A conflicting public package or immutable Release asset fails
  closed.
- A failed GitHub asset upload may leave a draft. The same job resumes only a
  draft with the expected tag, prerelease state, asset names, and SHA-256
  digests. It uploads missing assets and never overwrites a mismatch. The
  separately verified signed tag remains the commit identity; GitHub's
  `targetCommitish` metadata is not used as a second identity.
- GitHub generates an immutable-release attestation when the draft is
  published, but its verification readback can lag the publish response. The
  job retries the complete Release-and-asset verification set up to 30 times
  at ten-second intervals. Exhaustion leaves the run red and requires a
  same-job retry; it does not invalidate matching public NuGet bytes or permit
  verification to be skipped.
- If qualification artifacts expired or the original run can no longer be
  rerun, stop for maintainer recovery. Do not rebuild under the same tag.
- Retain the original frozen-line and admission workflow artifacts. A failed-job
  retry verifies their original run, producer attempt, archive digest and exact
  candidate bytes; it must not infer original identities from the new attempt.
  Missing/expired admission after a write may have started cannot be replaced.
  Re-run all jobs is not an exact-byte retry if it rebuilds the candidate.
- Complete an affected publication before deactivating/reassigning its line.
  A frozen qualification is not a fallback for missing current authorization.

Stable releases use the same procedure. A stable package is independently
qualified for its own version; an RC package is never renamed into a stable
package.

## Complete an existing publication without uploading packages

This mode is available only after its reviewed controls reach protected main.
It is a new control run, not a new package release. The original run remains
unchanged, including its failed conclusion. A successful completion receipt
links that run and its original source/admission/artifacts to the new verified
completion. It does not relabel an old failure as successful qualification.

Before starting it, confirm the public symbol validation status for all five
packages in NuGet.org, as required for normal publication. Successful upload
steps prove acceptance, not completed symbol indexing. If validation failed,
stop; this mode cannot repair or re-upload symbols. Review the failed publisher,
the exact signed tag and the original qualified source. Do not rebuild or move
the tag. Original line, admission, package and provenance artifacts must still
exist and be unexpired. A missing admission after uploads cannot be replaced.

In **Release candidate**, select the reviewed current **main** and enter:

```text
version: 10.7.1
qualification-only: false
finalize-run-id: 42767338686
```

These values identify the interrupted 10.7.1 publication; substitute independently
verified identities when completing another release. Leave finalize-run-id empty
for ordinary new releases. Combining it with qualification-only is rejected
before requesting the protected environment. Starting this workflow and approving
it are operator actions; preparation or local tests do not authorize either.

The completion job retains the existing nuget review gate and the shared
non-cancelling publication lock. It has no OIDC write permission, NuGet login,
package build/pack, primary or symbol upload, tag write or asset staging step.
It can only finalize one existing Release ID with the complete original assets:

1. Authenticate the actual control run at current protected main. Independently
   restore the original publication admission and frozen source-line identity,
   checking archive digests and original repository, run, source and version.
2. Require every original matrix cell, preflight and attestation producer to have
   succeeded at its own admitted attempt. Require all five original symbol-upload
   steps to have succeeded. A failed aggregate publisher alone does not invalidate
   its successful qualification; missing or skipped producers do.
3. Download original package/provenance artifact IDs. Check the unchanged signed
   tag with the currently trusted signers, original package contract/checksums,
   exact thirteen-asset manifest and cryptographic portable provenance
   pinned to the original signer workflow, source ref and commit.
4. Read all five public primary packages, require valid repository signatures and
   compare all contents with the originals, excluding only the repository
   signature entry. The shared NuGet wait is one hour; timeout is not permission
   to accept a missing package or skip verification. The 90-minute job budget
   leaves time for the remaining evidence checks.
5. Recheck current authority, original evidence, candidate bytes and the exact
   existing Release ID immediately before completion. Verify the tag again and
   use release notes from the original signed source. Never create a missing
   release, fill missing assets, overwrite a mismatch or replace original admission.
6. Publish only that complete draft, then verify immutable Release state and
   every asset attestation. Repeating completion on a matching immutable release
   verifies it without another edit. Retain line/admission/finalization evidence
   under safe-migrations-finalization-RUN-ATTEMPT; completion.json is written only
   after all final checks succeed. A retained preparation receipt alone is not
   proof of successful completion.

Any foreign, expired, ambiguous, replaced or revoked evidence is blocking. Stop
for maintainer recovery if the original evidence is unavailable. Do not disable
identity checks, change GITHUB_SHA/GITHUB_RUN_ID to impersonate the old run,
publish replacement bytes under the same version, or manually mark the old run
successful. The normal signed-content and immutable-release acceptance gates
remain mandatory.

## Primary sources

- [GitHub environments and required reviewers](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments), retrieved 2026-08-26.
- [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations), retrieved 2026-08-26.
- [`actions/attest` bundle output](https://github.com/actions/attest#outputs), retrieved 2026-08-29.
- [GitHub offline attestation verification](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/verify-attestations-offline), retrieved 2026-08-29.
- [OpenSSF Signed-Releases check](https://github.com/ossf/scorecard/blob/main/docs/checks.md#signed-releases), retrieved 2026-08-29.
- [GitHub immutable Releases](https://docs.github.com/en/enterprise-cloud@latest/code-security/concepts/supply-chain-security/immutable-releases), retrieved 2026-08-29.
- [GitHub REST release inventory](https://docs.github.com/en/rest/releases/releases?apiVersion=2022-11-28#list-releases), retrieved 2026-08-29.
- [GitHub immutable Releases and `gh release create`](https://cli.github.com/manual/gh_release_create), retrieved 2026-08-26.
- [GitHub Release asset upload](https://cli.github.com/manual/gh_release_upload), retrieved 2026-08-26.
- [GitHub Release verification](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/verify-release-integrity), retrieved 2026-08-26.
- [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), retrieved 2026-08-26.
- [`dotnet nuget push`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-push), retrieved 2026-08-26.
- [NuGet symbol package publication](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg), retrieved 2026-08-26.
